using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// Pass-through <see cref="ITransport"/> around the real <see cref="SubprocessTransport"/>
/// that captures both directions of the stream-json protocol:
/// <list type="bullet">
/// <item><c>{"dir":"in","msg":{...}}</c> — a line the CLI wrote to stdout;</item>
/// <item><c>{"dir":"out","msg":{...}}</c> — a line the SDK wrote to the CLI's stdin;</item>
/// <item><c>{"dir":"out","end_input":true}</c> — the SDK closed stdin.</item>
/// </list>
/// The first line is a <c>{"fixture":...}</c> header. On dispose the capture is
/// scrubbed (<see cref="FixtureScrubber"/>) and written as JSONL; the unit-test
/// project's ReplayTransport plays these files back deterministically.
/// Enabled by <c>CLAUDE_AGENT_SDK_RECORD_FIXTURES=&lt;dir&gt;</c>.
/// </summary>
internal sealed class RecordingTransport : ITransport
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SubprocessTransport _inner;
    private readonly string _path;
    private readonly string _fixtureName;
    private readonly string _model;
    private readonly FixtureScrubber _scrubber;
    private readonly List<JsonObject> _lines = new();
    private readonly object _gate = new();
    private int _flushed;

    public RecordingTransport(ClaudeAgentOptions options, string directory, string fixtureName)
    {
        _inner = new SubprocessTransport(prompt: "", options);
        _fixtureName = fixtureName;
        _model = options.Model ?? "";
        _scrubber = new FixtureScrubber(options.Cwd ?? Environment.CurrentDirectory, options.Agents?.Keys);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, fixtureName + ".jsonl");
    }

    public bool IsReady => _inner.IsReady;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _inner.ConnectAsync(cancellationToken);

    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { node = JsonValue.Create(line); }
            Capture(new JsonObject { ["dir"] = "out", ["msg"] = node });
        }
        await _inner.WriteAsync(data, cancellationToken);
    }

    public async Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        Capture(new JsonObject { ["dir"] = "out", ["end_input"] = true });
        await _inner.EndInputAsync(cancellationToken);
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var e = _inner.ReadMessagesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            JsonElement msg;
            try
            {
                if (!await e.MoveNextAsync()) break;
                msg = e.Current;
            }
            catch (ProcessException ex)
            {
                // The CLI exited non-zero (e.g. after an error result): record it so the
                // replay can raise the same failure at the same point.
                Capture(new JsonObject
                {
                    ["dir"] = "exit",
                    ["exit_code"] = ex.ExitCode,
                    ["error"] = "Command failed",
                });
                throw;
            }
            Capture(new JsonObject { ["dir"] = "in", ["msg"] = JsonNode.Parse(msg.GetRawText()) });
            yield return msg;
        }
    }

    private void Capture(JsonObject line)
    {
        lock (_gate) _lines.Add(line);
    }

    public async Task CloseAsync()
    {
        await _inner.CloseAsync();
        Flush();
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        Flush();
    }

    private void Flush()
    {
        if (Interlocked.Exchange(ref _flushed, 1) == 1) return;
        List<JsonObject> lines;
        lock (_gate) lines = _lines.ToList();

        var sb = new StringBuilder();
        var header = new JsonObject
        {
            ["fixture"] = _fixtureName,
            ["model"] = _model,
            ["recorded_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["note"] = "Recorded from a live Claude Code CLI run by Claude.AgentSdk.IntegrationTests; scrubbed. See docs/TESTING.md.",
        };
        sb.Append(header.ToJsonString(WriteOptions)).Append('\n');
        foreach (var line in lines)
            sb.Append(_scrubber.ScrubLine(line).ToJsonString(WriteOptions)).Append('\n');
        File.WriteAllText(_path, sb.ToString());
    }
}
