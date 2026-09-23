using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.Tests;

/// <summary>
/// In-memory <see cref="ITransport"/>: tests push CLI → SDK frames with
/// <see cref="Send"/> and inspect SDK → CLI frames in <see cref="Written"/>.
/// SDK-initiated control requests are answered by <see cref="ControlResponder"/>.
/// </summary>
internal sealed class FakeTransport : ITransport
{
    private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();

    public ConcurrentQueue<JsonElement> Written { get; } = new();

    public TaskCompletionSource InputEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Maps an SDK control request body to the inner response payload.</summary>
    public Func<JsonElement, object?> ControlResponder { get; set; } = _ => new Dictionary<string, object?>();

    public bool IsReady => true;

    public void Send(object frame) => _incoming.Writer.TryWrite(JsonSerializer.SerializeToElement(frame));

    public void Complete() => _incoming.Writer.TryComplete();

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var json = JsonSerializer.Deserialize<JsonElement>(line);
            Written.Enqueue(json);

            if (json.GetProperty("type").GetString() == "control_request")
            {
                Send(new
                {
                    type = "control_response",
                    response = new
                    {
                        subtype = "success",
                        request_id = json.GetProperty("request_id").GetString(),
                        response = ControlResponder(json.GetProperty("request"))
                    }
                });
            }
        }
        return Task.CompletedTask;
    }

    public Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        InputEnded.TrySetResult();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _incoming.Reader.ReadAllAsync(cancellationToken))
            yield return item;
    }

    public Task CloseAsync()
    {
        Complete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>Control responses the SDK wrote for a CLI-initiated request id.</summary>
    public List<JsonElement> ResponsesFor(string requestId) => Written
        .Where(w => w.GetProperty("type").GetString() == "control_response" &&
                    w.GetProperty("response").GetProperty("request_id").GetString() == requestId)
        .ToList();

    public async Task WaitForAsync(Func<FakeTransport, bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition(this))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met");
            await Task.Delay(10);
        }
    }
}
