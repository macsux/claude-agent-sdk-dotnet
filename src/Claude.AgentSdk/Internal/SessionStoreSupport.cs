// Claude Agent SDK for .NET
// Shared SessionStore wiring for Claude.QueryAsync and ClaudeSDKClient.
// Port of the session_store handling in claude-agent-sdk-python/client.py and _internal/client.py.

using Claude.AgentSdk.Sessions;

namespace Claude.AgentSdk.Internal;

internal static class SessionStoreSupport
{
    /// <summary>
    /// Repoint <paramref name="options"/> at a materialized temp config dir.
    /// Python: <c>apply_materialized_options</c>.
    /// </summary>
    public static ClaudeAgentOptions ApplyMaterialized(ClaudeAgentOptions options, MaterializedResume materialized)
    {
        var env = new Dictionary<string, string>(options.Env)
        {
            ["CLAUDE_CONFIG_DIR"] = materialized.ConfigDir
        };
        return options with
        {
            Env = env,
            Resume = materialized.ResumeSessionId,
            ContinueConversation = false
        };
    }

    /// <summary>Attach the transcript mirror batcher when a SessionStore is configured.</summary>
    public static void AttachMirrorBatcher(QueryHandler queryHandler, ClaudeAgentOptions options, MaterializedResume? materialized)
    {
        if (options.SessionStore is null)
            return;

        queryHandler.SetTranscriptMirrorBatcher(SessionResume.BuildMirrorBatcher(
            options.SessionStore,
            materialized,
            options.Env,
            (key, error, _) =>
            {
                queryHandler.ReportMirrorError(key, error);
                return Task.CompletedTask;
            },
            options.SessionStoreFlush));
    }

    /// <summary>
    /// Load / resume-materialization timeout (Python: <c>load_timeout_ms / 1000</c>
    /// passed to <c>anyio.fail_after</c>). Python treats 0 or less as an
    /// immediate timeout; <see cref="SessionResume"/> requires a positive span,
    /// so those map to the smallest positive one (one tick).
    /// </summary>
    public static TimeSpan LoadTimeout(ClaudeAgentOptions options) =>
        options.LoadTimeoutMs > 0 ? TimeSpan.FromMilliseconds(options.LoadTimeoutMs) : TimeSpan.FromTicks(1);

    /// <summary>Resume a session from the store honoring <see cref="ClaudeAgentOptions.LoadTimeoutMs"/>.</summary>
    public static Task<MaterializedResume?> MaterializeAsync(ClaudeAgentOptions options, CancellationToken cancellationToken) =>
        SessionResume.MaterializeResumeSessionAsync(options, LoadTimeout(options), cancellationToken);

    /// <summary>Initialize timeout: CLAUDE_CODE_STREAM_CLOSE_TIMEOUT (ms), floored at 60s.</summary>
    public static TimeSpan InitializeTimeout()
    {
        var timeoutMs = int.TryParse(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_STREAM_CLOSE_TIMEOUT"),
            out var ms
        ) ? ms : 60000;
        return TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 60000));
    }
}
