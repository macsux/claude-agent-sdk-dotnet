using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// Shared harness for live-CLI tests. Every test gets:
/// <list type="bullet">
/// <item>an isolated, canonicalized temp working directory (deleted afterwards, together
///   with the CLI's <c>~/.claude/projects/&lt;cwd-key&gt;</c> transcript directory);</item>
/// <item>a <see cref="Ct"/> that fires after the per-test timeout (default 120s);</item>
/// <item><see cref="Options"/>: cheap, hermetic defaults — the configured model
///   (haiku unless overridden), <c>MaxTurns=3</c>, <c>MaxBudgetUsd=0.25</c>, no built-in
///   tools unless the test opts in, <c>SettingSources=[]</c> and <c>StrictMcpConfig</c>
///   so the developer's own settings / CLAUDE.md / MCP servers / plugins don't leak in;</item>
/// <item>cost accounting printed to the test output, plus a process-wide total.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected const decimal DefaultBudgetUsd = 0.25m;
    protected const int DefaultMaxTurns = 3;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly CancellationTokenSource _timeout;
    private readonly ConcurrentQueue<string> _stderr = new();
    private readonly List<string> _sessionIds = new();

    protected IntegrationTestBase(ITestOutputHelper output)
    {
        Output = output;
        _timeout = new CancellationTokenSource(IntegrationEnvironment.PerTestTimeout);
        var dir = Path.Combine(Path.GetTempPath(), "claude-sdk-it", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        Cwd = SessionPaths.CanonicalizePath(dir);
    }

    protected ITestOutputHelper Output { get; }

    /// <summary>Isolated working directory for this test (canonical path, symlinks resolved).</summary>
    protected string Cwd { get; }

    /// <summary>Cancelled when the per-test timeout elapses.</summary>
    protected CancellationToken Ct => _timeout.Token;

    protected static string Model => IntegrationEnvironment.Model;

    /// <summary>Short random token to plant in prompts and look for in outputs.</summary>
    protected static string NewNonce(string prefix = "N") =>
        $"{prefix}{Random.Shared.Next(100000, 999999)}";

    /// <summary>Test name used for cost accounting and fixture file names.</summary>
    protected virtual string TestName => GetType().Name;

    private string? _currentTest;

    /// <summary>
    /// Hermetic, cheap default options. Tests adjust with <c>with { ... }</c>.
    /// </summary>
    protected ClaudeAgentOptions Options([CallerMemberName] string test = "")
    {
        _currentTest ??= $"{GetType().Name}.{test}";
        CostLedger.EnsureBudgetRemaining();

        var env = new Dictionary<string, string>();
        foreach (var name in IntegrationEnvironment.ParentSessionVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
                env[name] = "";
        }

        return new ClaudeAgentOptions
        {
            CliPath = IntegrationEnvironment.CliPath,
            Model = Model,
            Cwd = Cwd,
            MaxTurns = DefaultMaxTurns,
            MaxBudgetUsd = DefaultBudgetUsd,
            // No built-in tools unless a test opts in: keeps the prompt (and the bill) tiny.
            Tools = [],
            // Empty list => `--setting-sources=`: no user/project/local settings, CLAUDE.md,
            // hooks, permissions or plugins from the developer's machine.
            SettingSources = [],
            StrictMcpConfig = true,
            Env = env,
            StderrCallback = line => _stderr.Enqueue(line),
        };
    }

    /// <summary>
    /// A <see cref="RecordingTransport"/> when fixture recording is enabled
    /// (<c>CLAUDE_AGENT_SDK_RECORD_FIXTURES=dir</c>), otherwise null so the SDK
    /// uses its own transport. Do not use with SessionStore resume: a custom
    /// transport bypasses the SDK's store materialization.
    /// </summary>
    protected ITransport? Transport(ClaudeAgentOptions options, [CallerMemberName] string test = "", string? suffix = null)
    {
        var dir = IntegrationEnvironment.RecordDirectory;
        if (dir is null) return null;
        var name = $"{GetType().Name.Replace("Tests", "", StringComparison.Ordinal)}.{test}{(suffix is null ? "" : "." + suffix)}";
        return new RecordingTransport(options, dir, name);
    }

    /// <summary>Drain a message stream, logging each message and accounting for result costs.</summary>
    protected async Task<List<Message>> CollectAsync(IAsyncEnumerable<Message> messages)
    {
        var list = new List<Message>();
        await foreach (var m in messages.WithCancellation(Ct))
        {
            list.Add(m);
            Observe(m);
        }
        return list;
    }

    /// <summary>Log a message and, for results, record its cost.</summary>
    protected void Observe(Message m)
    {
        switch (m)
        {
            case ResultMessage r:
                var cost = r.TotalCostUsd ?? 0m;
                CostLedger.Add(_currentTest ?? GetType().Name, cost);
                if (!string.IsNullOrEmpty(r.SessionId)) _sessionIds.Add(r.SessionId);
                Log($"<- result subtype={r.Subtype} is_error={r.IsError} turns={r.NumTurns} " +
                    $"stop_reason={r.StopReason} cost=${cost:F6} session={r.SessionId}");
                break;
            case AssistantMessage a:
                foreach (var b in a.Content)
                {
                    Log(b switch
                    {
                        TextBlock t => $"<- assistant text: {Truncate(t.Text)}",
                        ToolUseBlock tu => $"<- assistant tool_use {tu.Name} {Truncate(tu.Input.GetRawText())}",
                        ThinkingBlock => "<- assistant thinking",
                        _ => $"<- assistant {b.GetType().Name}",
                    });
                }
                break;
            case UserMessage u:
                Log($"<- user {Truncate(u.Content.GetRawText())}");
                break;
            case StreamEvent:
                break;
            case SystemMessage s:
                Log($"<- system/{s.Subtype}");
                break;
            default:
                Log($"<- {m.GetType().Name}");
                break;
        }
    }

    protected void Log(string line)
    {
        try { Output.WriteLine($"[{_stopwatch.Elapsed.TotalSeconds,6:F1}s] {line}"); }
        catch (InvalidOperationException) { /* output helper already closed */ }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    protected static ResultMessage SingleResult(IEnumerable<Message> messages)
    {
        var results = messages.OfType<ResultMessage>().ToList();
        Assert.True(results.Count == 1, $"expected exactly one ResultMessage, got {results.Count}");
        return results[0];
    }

    protected static SystemMessage InitMessage(IEnumerable<Message> messages)
    {
        var init = messages.OfType<SystemMessage>().FirstOrDefault(m => m.Subtype == "init");
        Assert.NotNull(init);
        return init!;
    }

    protected static string AllAssistantText(IEnumerable<Message> messages) =>
        string.Join("\n", messages.OfType<AssistantMessage>()
            .SelectMany(a => a.Content.OfType<TextBlock>())
            .Select(t => t.Text));

    protected static IEnumerable<ToolUseBlock> ToolUses(IEnumerable<Message> messages) =>
        messages.OfType<AssistantMessage>().SelectMany(a => a.Content.OfType<ToolUseBlock>());

    /// <summary>
    /// tool_result blocks from user messages. Parsed from the raw content rather than via
    /// <see cref="UserMessage.GetContentBlocks"/>, which throws on real CLI output (see
    /// <c>UserMessageTests.GetContentBlocks_ParsesRealCliToolResults</c>).
    /// </summary>
    protected static IEnumerable<ToolResultBlock> ToolResults(IEnumerable<Message> messages) =>
        messages.OfType<UserMessage>()
            .Where(u => u.Content.ValueKind == JsonValueKind.Array)
            .SelectMany(u => u.Content.EnumerateArray())
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
            .Select(b => new ToolResultBlock(
                b.GetProperty("tool_use_id").GetString()!,
                b.TryGetProperty("content", out var c) ? c.Clone() : null,
                b.TryGetProperty("is_error", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? e.GetBoolean()
                    : null));

    /// <summary>
    /// Drain a stream that is expected to end with an exception (the CLI exits non-zero
    /// after an error result). Returns everything yielded before it and the exception.
    /// </summary>
    protected async Task<(List<Message> Messages, Exception? Error)> CollectUntilErrorAsync(IAsyncEnumerable<Message> messages)
    {
        var list = new List<Message>();
        try
        {
            await foreach (var m in messages.WithCancellation(Ct))
            {
                list.Add(m);
                Observe(m);
            }
            return (list, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"<- stream ended with {ex.GetType().Name}: {ex.Message}");
            return (list, ex);
        }
    }

    protected static string ToolResultText(ToolResultBlock block)
    {
        if (block.Content is not { } c) return "";
        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n", c.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("text", out var t)
                    ? t.GetString()
                    : e.GetRawText())),
            _ => c.GetRawText(),
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        var name = _currentTest ?? GetType().Name;
        CostLedger.RecordDuration(name, _stopwatch.Elapsed);
        Log(string.Format(CultureInfo.InvariantCulture,
            "== cost this test ${0:F6}; suite running total ${1:F6}; {2:F1}s",
            CostLedger.For(name), CostLedger.Total, _stopwatch.Elapsed.TotalSeconds));
        if (!_stderr.IsEmpty)
        {
            Log($"== CLI stderr ({_stderr.Count} lines, last 15):");
            foreach (var l in _stderr.TakeLast(15)) Log("   " + Truncate(l));
        }

        _timeout.Dispose();
        TryDelete(Cwd);
        // The CLI keeps transcripts under ~/.claude/projects/<sanitized cwd>/; this
        // directory is unique to our temp cwd, so remove it to leave no trace.
        try
        {
            var projectDir = Path.Combine(SessionPaths.GetProjectsDir(), SessionPaths.ProjectKeyForDirectory(Cwd));
            TryDelete(projectDir);
        }
        catch
        {
            // best effort
        }
        return Task.CompletedTask;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
