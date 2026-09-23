using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.AotSmoke;

/// <summary>
/// An in-memory stand-in for the Claude Code CLI: answers the SDK's control
/// requests, sends CLI-initiated control requests, and emits scripted messages.
/// </summary>
internal sealed class ScriptedTransport : ITransport
{
    private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private int _requestCounter;

    /// <summary>Every frame the SDK wrote to "stdin".</summary>
    public ConcurrentQueue<JsonElement> Written { get; } = new();

    /// <summary>Inner response payload for an SDK control request, by subtype.</summary>
    public Func<string, JsonElement, JsonNode?> ControlResponder { get; set; } = (_, _) => new JsonObject();

    /// <summary>Messages to emit when the SDK sends a user message.</summary>
    public Func<JsonElement, IEnumerable<JsonNode>> OnUserMessage { get; set; } = _ => [];

    /// <summary>End the message stream after answering the first user message (one-shot query mode).</summary>
    public bool CompleteAfterUserMessage { get; set; }

    public bool IsReady => true;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var frame = Parse(line);
            Written.Enqueue(frame);
            switch (frame.GetProperty("type").GetString())
            {
                case "control_request":
                {
                    var request = frame.GetProperty("request");
                    var payload = ControlResponder(request.GetProperty("subtype").GetString()!, request) ?? new JsonObject();
                    Emit(new JsonObject
                    {
                        ["type"] = "control_response",
                        ["response"] = new JsonObject
                        {
                            ["subtype"] = "success",
                            ["request_id"] = frame.GetProperty("request_id").GetString(),
                            ["response"] = payload
                        }
                    });
                    break;
                }
                case "control_response":
                {
                    var response = frame.GetProperty("response");
                    if (_pending.TryRemove(response.GetProperty("request_id").GetString()!, out var tcs))
                        tcs.TrySetResult(response);
                    break;
                }
                case "user":
                    foreach (var message in OnUserMessage(frame))
                        Emit(message);
                    if (CompleteAfterUserMessage)
                        _incoming.Writer.TryComplete();
                    break;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Send a CLI-initiated control request and wait for the SDK's control_response.</summary>
    public async Task<JsonElement> RequestAsync(JsonNode request)
    {
        var id = $"cli_{Interlocked.Increment(ref _requestCounter)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Emit(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Emit(JsonNode frame) => _incoming.Writer.TryWrite(Parse(frame.ToJsonString()));

    public void Emit(JsonElement frame) => _incoming.Writer.TryWrite(frame.Clone());

    public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _incoming.Reader.ReadAllAsync(cancellationToken))
            yield return frame;
    }

    public Task CloseAsync()
    {
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
