using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.Tests.Replay;

/// <summary>
/// Deterministic <see cref="ITransport"/> that replays a recorded real-CLI session.
/// </summary>
/// <remarks>
/// <para>The script is played in recorded order, in lock-step with the SDK:</para>
/// <list type="bullet">
/// <item><c>in</c> lines are yielded to the SDK as CLI stdout.</item>
/// <item><c>out</c> lines are <em>expectations</em>: replay pauses until the SDK has
///   written a matching frame (same type; same control subtype for requests; same
///   request_id for responses to CLI-initiated requests). A mismatch or a missing
///   write fails with a <see cref="ReplayMismatchException"/> after
///   <see cref="StepTimeout"/>.</item>
/// <item>Control requests the SDK initiates get fresh ids each run, so the recorded
///   id is mapped to the live one and rewritten in the replayed control_response.
///   Ids of CLI-initiated requests (hook_callback, can_use_tool, mcp_message) are
///   replayed verbatim, so the SDK's answers carry the recorded ids.</item>
/// <item>An <c>exit</c> line raises the same <see cref="ProcessException"/> the real
///   transport raises when the CLI exits non-zero.</item>
/// </list>
/// <para>Every SDK write is kept in <see cref="Written"/>, and each recorded
/// expectation with the live frame that satisfied it in <see cref="Matches"/>.</para>
/// </remarks>
internal sealed class ReplayTransport : ITransport
{
    private readonly Fixture _fixture;
    private readonly List<JsonElement> _unmatched = new();
    private readonly Dictionary<string, string> _idMap = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wrote = new(0);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closed = new();

    public ReplayTransport(Fixture fixture) => _fixture = fixture;

    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Every frame the SDK wrote, in order.</summary>
    public List<JsonElement> Written { get; } = new();

    /// <summary>(recorded expectation, live SDK frame) pairs, in script order.</summary>
    public List<(JsonElement Recorded, JsonElement Actual)> Matches { get; } = new();

    public bool InputEnded { get; private set; }

    public bool IsReady => true;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var el = JsonSerializer.Deserialize<JsonElement>(line);
            lock (_gate)
            {
                Written.Add(el);
                _unmatched.Add(el);
            }
            _wrote.Release();
        }
        return Task.CompletedTask;
    }

    public Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        InputEnded = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var ct = linked.Token;

        foreach (var line in _fixture.Lines)
        {
            if (ct.IsCancellationRequested) yield break;
            switch (line.Dir)
            {
                case "out" when line.Msg is { } expected:
                    await ExpectWriteAsync(expected, ct);
                    break;
                case "in":
                    yield return Remap(line.Msg!.Value);
                    break;
                case "exit":
                    throw new ProcessException(
                        $"Command failed with exit code {line.ExitCode}",
                        line.ExitCode,
                        "Check stderr output for details");
            }
        }
    }

    private async Task ExpectWriteAsync(JsonElement expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (true)
        {
            lock (_gate)
            {
                var hit = _unmatched.FindIndex(a => IsMatch(expected, a));
                if (hit >= 0)
                {
                    var actual = _unmatched[hit];
                    _unmatched.RemoveAt(hit);
                    Matches.Add((expected, actual));
                    if (expected.GetProperty("type").GetString() == "control_request")
                        _idMap[expected.GetProperty("request_id").GetString()!] = actual.GetProperty("request_id").GetString()!;
                    return;
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                string pending;
                lock (_gate) pending = string.Join("\n  ", _unmatched.Select(u => Describe(u)));
                throw new ReplayMismatchException(
                    $"[{_fixture.Name}] timed out waiting for the SDK to write {Describe(expected)}.\n" +
                    $"Unmatched SDK writes:\n  {(pending.Length == 0 ? "(none)" : pending)}");
            }
            try
            {
                await _wrote.WaitAsync(remaining, ct);
            }
            catch (OperationCanceledException)
            {
                return; // transport closed
            }
        }
    }

    private static bool IsMatch(JsonElement expected, JsonElement actual)
    {
        var type = expected.GetProperty("type").GetString();
        if (actual.GetProperty("type").GetString() != type) return false;
        return type switch
        {
            "control_request" => expected.GetProperty("request").GetProperty("subtype").GetString() ==
                                 actual.GetProperty("request").GetProperty("subtype").GetString(),
            // Same CLI request, answered the same way (success vs error).
            "control_response" => expected.GetProperty("response").GetProperty("request_id").GetString() ==
                                  actual.GetProperty("response").GetProperty("request_id").GetString() &&
                                  expected.GetProperty("response").GetProperty("subtype").GetString() ==
                                  actual.GetProperty("response").GetProperty("subtype").GetString(),
            _ => true,
        };
    }

    private JsonElement Remap(JsonElement msg)
    {
        if (msg.GetProperty("type").GetString() != "control_response") return msg;
        var recordedId = msg.GetProperty("response").GetProperty("request_id").GetString();
        string? liveId;
        lock (_gate)
        {
            if (recordedId is null || !_idMap.TryGetValue(recordedId, out liveId)) return msg;
        }
        var node = JsonNode.Parse(msg.GetRawText())!;
        node["response"]!["request_id"] = liveId;
        return JsonSerializer.SerializeToElement(node);
    }

    private static string Describe(JsonElement m)
    {
        var type = m.GetProperty("type").GetString();
        return type switch
        {
            "control_request" => $"control_request/{m.GetProperty("request").GetProperty("subtype").GetString()}",
            "control_response" => $"control_response(request_id={m.GetProperty("response").GetProperty("request_id").GetString()})",
            _ => type ?? "?",
        };
    }

    /// <summary>The live SDK frame that satisfied the recorded control_response to CLI request <paramref name="requestId"/>.</summary>
    public JsonElement ResponseTo(string requestId) => Matches
        .Select(m => m.Actual)
        .Single(a => a.GetProperty("type").GetString() == "control_response" &&
                     a.GetProperty("response").GetProperty("request_id").GetString() == requestId);

    /// <summary>Live SDK control requests of the given subtype, in order.</summary>
    public IEnumerable<JsonElement> RequestsOf(string subtype) => Written.Where(w =>
        w.GetProperty("type").GetString() == "control_request" &&
        w.GetProperty("request").GetProperty("subtype").GetString() == subtype);

    public Task CloseAsync()
    {
        _closed.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

internal sealed class ReplayMismatchException(string message) : Exception(message);
