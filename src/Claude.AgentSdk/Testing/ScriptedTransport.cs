// Claude Agent SDK for .NET
// A scripted, in-memory ITransport for unit-testing code built on the SDK
// without the Claude Code CLI (no latency, no cost, deterministic).

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.Testing;

/// <summary>
/// An <see cref="ITransport"/> that plays scripted CLI turns, for unit-testing
/// agent code that uses <see cref="Claude.QueryAsync(string, ClaudeAgentOptions?, ITransport?, CancellationToken)"/>
/// or <see cref="ClaudeSDKClient"/>.
/// </summary>
/// <remarks>
/// <para>Each <see cref="Turn"/> is released when the SDK writes a user message,
/// like the real CLI answering a prompt. Within a turn, a control request (a
/// permission prompt or hook callback) pauses the script until the SDK answers
/// it, so your <c>CanUseTool</c> / hook code runs exactly as it would live.
/// After the last turn has played, closing input ends the stream.</para>
/// <para>SDK-initiated control requests (initialize, set_model, ...) are answered
/// by <see cref="ControlResponder"/>. Everything the SDK writes is recorded in
/// <see cref="Written"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// var transport = new ScriptedTransport();
/// transport.Turn(t => t
///     .PermissionRequest("Write", """{"file_path":"/etc/passwd","content":"x"}""")
///     .AssistantText("I could not write that file.")
///     .Result("I could not write that file."));
///
/// var answer = await Claude.QueryTextAsync("write /etc/passwd", options, transport);
/// Assert.Equal("deny", transport.PermissionResponses[0].GetProperty("behavior").GetString());
/// </code>
/// </example>
public sealed class ScriptedTransport : ITransport
{
    private readonly Channel<JsonElement> _outgoing = Channel.CreateUnbounded<JsonElement>();
    private readonly ConcurrentQueue<ScriptedTurn> _turns = new();
    private readonly ConcurrentQueue<JsonElement> _written = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _awaitingResponses = new();
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private int _controlRequestCounter;
    private int _activePlays;
    private volatile bool _inputEnded;

    /// <summary>
    /// Answers SDK-initiated control requests: receives the request body
    /// (<c>{"subtype": "initialize", ...}</c>) and returns the response payload.
    /// Defaults to an empty object for every request.
    /// </summary>
    public Func<JsonElement, JsonElement>? ControlResponder { get; set; }

    /// <summary>Session id stamped on generated frames.</summary>
    public string SessionId { get; set; } = "00000000-0000-4000-8000-000000000000";

    /// <summary>Model name stamped on generated assistant frames.</summary>
    public string Model { get; set; } = "claude-scripted";

    /// <summary>Every frame the SDK wrote to the CLI, in order.</summary>
    public IReadOnlyList<JsonElement> Written => _written.ToArray();

    /// <summary>User messages the SDK sent (the prompts).</summary>
    public IReadOnlyList<JsonElement> UserMessages => Written
        .Where(w => Str(w, "type") == "user")
        .ToArray();

    /// <summary>
    /// Inner payloads of the SDK's answers to scripted permission requests
    /// (<c>{"behavior": "allow"|"deny", ...}</c>), in order.
    /// </summary>
    public IReadOnlyList<JsonElement> PermissionResponses => ResponsesTo("can_use_tool");

    /// <summary>Inner payloads of the SDK's answers to scripted hook callbacks, in order.</summary>
    public IReadOnlyList<JsonElement> HookResponses => ResponsesTo("hook_callback");

    /// <summary>Whether the SDK closed its input (stdin) stream.</summary>
    public bool InputEnded => _inputEnded;

    /// <inheritdoc />
    public bool IsReady => true;

    /// <summary>Queue the CLI's response to the next user message.</summary>
    public ScriptedTransport Turn(Action<ScriptedTurn> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var turn = new ScriptedTurn(this);
        build(turn);
        _turns.Enqueue(turn);
        return this;
    }

    /// <summary>
    /// Emit frames immediately, independent of any prompt (e.g. a <c>system/init</c>
    /// frame the CLI sends at startup).
    /// </summary>
    public void Emit(JsonElement frame) => _outgoing.Writer.TryWrite(frame.Clone());

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var frame = SdkJson.ParseElement(line);
            _written.Enqueue(frame);

            switch (Str(frame, "type"))
            {
                case "control_request":
                    AnswerSdkControlRequest(frame);
                    break;
                case "control_response":
                    var response = frame.GetProperty("response");
                    if (Str(response, "request_id") is { } id && _awaitingResponses.TryRemove(id, out var waiter))
                        waiter.TrySetResult(response);
                    break;
                case "user":
                    // Play on a background task: the script may wait for this
                    // SDK to answer control requests, which it can only do
                    // after this write returns.
                    if (_turns.TryDequeue(out var turn))
                    {
                        Interlocked.Increment(ref _activePlays);
                        _ = PlayAsync(turn);
                    }
                    break;
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        // Like the CLI exiting on stdin EOF: end the stream once every turn
        // that was already prompted has finished playing.
        _inputEnded = true;
        CompleteIfDone();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void CompleteIfDone()
    {
        if (_inputEnded && Volatile.Read(ref _activePlays) == 0)
            _outgoing.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        _outgoing.Writer.TryComplete();
        foreach (var waiter in _awaitingResponses.Values)
            waiter.TrySetCanceled();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task PlayAsync(ScriptedTurn turn)
    {
        await _playLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var step in turn.Steps)
            {
                if (step.AwaitsResponse)
                {
                    var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _awaitingResponses[step.RequestId!] = waiter;
                    _outgoing.Writer.TryWrite(step.Frame);
                    try
                    {
                        await waiter.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // transport closed mid-script
                    }
                }
                else
                {
                    _outgoing.Writer.TryWrite(step.Frame);
                }
            }
        }
        finally
        {
            _playLock.Release();
            Interlocked.Decrement(ref _activePlays);
            CompleteIfDone();
        }
    }

    private void AnswerSdkControlRequest(JsonElement frame)
    {
        var request = frame.GetProperty("request");
        var payload = ControlResponder?.Invoke(request) ?? SdkJson.EmptyObject();
        _outgoing.Writer.TryWrite(SdkJson.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "control_response",
            ["response"] = new Dictionary<string, object?>
            {
                ["subtype"] = "success",
                ["request_id"] = Str(frame, "request_id"),
                ["response"] = payload
            }
        }));
    }

    private IReadOnlyList<JsonElement> ResponsesTo(string subtype)
    {
        var requestIds = new HashSet<string>(_turnRequestIds
            .Where(kv => kv.Value == subtype)
            .Select(kv => kv.Key));
        return Written
            .Where(w => Str(w, "type") == "control_response")
            .Select(w => w.GetProperty("response"))
            .Where(r => Str(r, "request_id") is { } id && requestIds.Contains(id))
            .Select(r => r.TryGetProperty("response", out var inner) ? inner : r)
            .ToArray();
    }

    private readonly ConcurrentDictionary<string, string> _turnRequestIds = new();

    internal string NextControlRequestId(string subtype)
    {
        var id = $"cli_req_{Interlocked.Increment(ref _controlRequestCounter)}";
        _turnRequestIds[id] = subtype;
        return id;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// The frames the scripted CLI emits in response to one user message. Build it
/// with the fluent helpers, or <see cref="Frame(JsonElement)"/> for anything else
/// (e.g. a frame copied from a real CLI recording).
/// </summary>
public sealed class ScriptedTurn
{
    private readonly ScriptedTransport _owner;
    internal List<Step> Steps { get; } = new();

    internal ScriptedTurn(ScriptedTransport owner) => _owner = owner;

    internal sealed record Step(JsonElement Frame, bool AwaitsResponse, string? RequestId);

    /// <summary>Emit a raw frame.</summary>
    public ScriptedTurn Frame(JsonElement frame)
    {
        Steps.Add(new Step(frame.Clone(), false, null));
        return this;
    }

    /// <summary>Emit a raw frame given as JSON text.</summary>
    public ScriptedTurn Frame(string json) => Frame(SdkJson.ParseElement(json));

    /// <summary>An assistant message with one text block.</summary>
    public ScriptedTurn AssistantText(string text) => Assistant(new Dictionary<string, object?>
    {
        ["type"] = "text",
        ["text"] = text
    });

    /// <summary>An assistant message with one <c>tool_use</c> block.</summary>
    /// <param name="toolName">Tool name, e.g. <c>Write</c> or <c>mcp__server__tool</c>.</param>
    /// <param name="inputJson">Tool input as a JSON object string.</param>
    /// <param name="toolUseId">Tool use id; generated when omitted.</param>
    public ScriptedTurn ToolUse(string toolName, string inputJson, string? toolUseId = null) => Assistant(new Dictionary<string, object?>
    {
        ["type"] = "tool_use",
        ["id"] = toolUseId ?? $"toolu_{Guid.NewGuid():N}",
        ["name"] = toolName,
        ["input"] = SdkJson.ParseElement(inputJson)
    });

    /// <summary>
    /// A <c>can_use_tool</c> permission request. The script pauses until the SDK
    /// answers (through your <c>CanUseTool</c> callback); read the answer from
    /// <see cref="ScriptedTransport.PermissionResponses"/>.
    /// </summary>
    public ScriptedTurn PermissionRequest(string toolName, string inputJson, string? toolUseId = null)
    {
        var id = _owner.NextControlRequestId("can_use_tool");
        var request = new Dictionary<string, object?>
        {
            ["subtype"] = "can_use_tool",
            ["tool_name"] = toolName,
            ["input"] = SdkJson.ParseElement(inputJson),
            ["tool_use_id"] = toolUseId ?? $"toolu_{Guid.NewGuid():N}"
        };
        return ControlRequest(id, request);
    }

    /// <summary>
    /// A <c>hook_callback</c> request for the hook registered at <paramref name="callbackId"/>
    /// (the SDK assigns <c>hook_0</c>, <c>hook_1</c>, ... in registration order). The script
    /// pauses until the SDK answers; read it from <see cref="ScriptedTransport.HookResponses"/>.
    /// </summary>
    public ScriptedTurn HookCallback(string callbackId, string inputJson, string? toolUseId = null)
    {
        var id = _owner.NextControlRequestId("hook_callback");
        var request = new Dictionary<string, object?>
        {
            ["subtype"] = "hook_callback",
            ["callback_id"] = callbackId,
            ["input"] = SdkJson.ParseElement(inputJson),
            ["tool_use_id"] = toolUseId
        };
        return ControlRequest(id, request);
    }

    /// <summary>
    /// A call to a tool on one of your in-process SDK MCP servers (<c>mcp_message</c>
    /// carrying <c>tools/call</c>). The script pauses until the SDK answers.
    /// </summary>
    public ScriptedTurn McpToolCall(string serverName, string toolName, string argumentsJson)
    {
        var id = _owner.NextControlRequestId("mcp_message");
        var request = new Dictionary<string, object?>
        {
            ["subtype"] = "mcp_message",
            ["server_name"] = serverName,
            ["message"] = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["arguments"] = SdkJson.ParseElement(argumentsJson)
                }
            }
        };
        return ControlRequest(id, request);
    }

    /// <summary>The <c>result</c> frame that ends the turn.</summary>
    /// <param name="result">Result text.</param>
    /// <param name="isError">Whether the run failed.</param>
    /// <param name="subtype"><c>success</c>, <c>error_max_turns</c>, <c>error_during_execution</c>, ...</param>
    /// <param name="structuredOutputJson">Structured output (for <c>OutputFormat</c> / <c>QueryAsync&lt;T&gt;</c>).</param>
    /// <param name="totalCostUsd">Reported cost.</param>
    public ScriptedTurn Result(
        string? result = null,
        bool isError = false,
        string subtype = "success",
        string? structuredOutputJson = null,
        decimal totalCostUsd = 0m)
    {
        var frame = new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["duration_ms"] = 1,
            ["duration_api_ms"] = 1,
            ["is_error"] = isError,
            ["num_turns"] = 1,
            ["session_id"] = _owner.SessionId,
            ["total_cost_usd"] = totalCostUsd
        };
        if (result != null)
            frame["result"] = result;
        if (structuredOutputJson != null)
            frame["structured_output"] = SdkJson.ParseElement(structuredOutputJson);
        return Frame(SdkJson.SerializeToElement(frame));
    }

    private ScriptedTurn Assistant(Dictionary<string, object?> block) => Frame(SdkJson.SerializeToElement(new Dictionary<string, object?>
    {
        ["type"] = "assistant",
        ["message"] = new Dictionary<string, object?>
        {
            ["model"] = _owner.Model,
            ["role"] = "assistant",
            ["content"] = new List<object?> { block }
        },
        ["parent_tool_use_id"] = null,
        ["session_id"] = _owner.SessionId
    }));

    private ScriptedTurn ControlRequest(string requestId, Dictionary<string, object?> request)
    {
        var frame = SdkJson.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = request
        });
        Steps.Add(new Step(frame, true, requestId));
        return this;
    }
}
