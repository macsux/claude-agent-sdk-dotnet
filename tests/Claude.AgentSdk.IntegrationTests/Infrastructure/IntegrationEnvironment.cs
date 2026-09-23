using System.Globalization;

namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// Environment switches for the live-CLI suite. Everything is read once per test
/// process; see docs/TESTING.md for the full list.
/// </summary>
internal static class IntegrationEnvironment
{
    /// <summary>Opt-in switch. Without it every integration test is skipped.</summary>
    public const string EnableVar = "CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS";

    /// <summary>Model override (default <see cref="DefaultModel"/>).</summary>
    public const string ModelVar = "CLAUDE_AGENT_SDK_IT_MODEL";

    /// <summary>Directory to write recorded JSONL fixtures to (optional).</summary>
    public const string RecordVar = "CLAUDE_AGENT_SDK_RECORD_FIXTURES";

    /// <summary>Per-test timeout in seconds (default 120).</summary>
    public const string TimeoutVar = "CLAUDE_AGENT_SDK_IT_TIMEOUT_SECONDS";

    /// <summary>Hard cap on the whole run's spend in USD (default 5). Tests fail fast once exceeded.</summary>
    public const string MaxTotalVar = "CLAUDE_AGENT_SDK_IT_MAX_TOTAL_USD";

    public const string DefaultModel = "claude-haiku-4-5-20251001";

    public static bool Enabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(EnableVar);
            return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string Model =>
        Environment.GetEnvironmentVariable(ModelVar) is { Length: > 0 } m ? m : DefaultModel;

    public static string? RecordDirectory =>
        Environment.GetEnvironmentVariable(RecordVar) is { Length: > 0 } d ? d : null;

    public static TimeSpan PerTestTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable(TimeoutVar), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(120);

    public static decimal MaxTotalUsd =>
        decimal.TryParse(Environment.GetEnvironmentVariable(MaxTotalVar), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var d) && d > 0
            ? d
            : 5m;

    private static readonly Lazy<string?> _cliPath = new(FindCli);

    /// <summary>The CLI the suite will run, or null when none is installed.</summary>
    public static string? CliPath => _cliPath.Value;

    /// <summary>
    /// Same discovery order the SDK uses (CLAUDE_CLI_PATH, PATH, well-known
    /// install locations). The result is passed explicitly as
    /// <see cref="ClaudeAgentOptions.CliPath"/> so the SDK runs exactly this binary.
    /// </summary>
    private static string? FindCli()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        var exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] locations =
        [
            Path.Combine(home, ".local", "bin", exe),
            Path.Combine(home, ".npm-global", "bin", exe),
            "/usr/local/bin/claude",
            Path.Combine(home, ".claude", "local", exe),
        ];
        return locations.FirstOrDefault(File.Exists);
    }

    /// <summary>Why integration tests are skipped right now, or null if they should run.</summary>
    public static string? SkipReason
    {
        get
        {
            if (!Enabled)
                return $"Integration tests talk to the real Claude Code CLI and cost money. Set {EnableVar}=1 to run them.";
            if (CliPath is null)
                return "Claude Code CLI not found (checked CLAUDE_CLI_PATH, PATH, ~/.local/bin/claude). Install it or set CLAUDE_CLI_PATH.";
            return null;
        }
    }

    /// <summary>
    /// Variables a parent Claude Code session exports to its children. When the suite
    /// itself is launched from inside Claude Code they leak into the CLI under test and
    /// change its behavior (child-session mode, messaging socket, effort level), so the
    /// harness blanks them. No-op in a normal terminal or CI.
    /// </summary>
    public static readonly string[] ParentSessionVariables =
    [
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_CODE_BRIDGE_SESSION_ID",
        "CLAUDE_PID",
        "CLAUDE_EFFORT",
    ];
}
