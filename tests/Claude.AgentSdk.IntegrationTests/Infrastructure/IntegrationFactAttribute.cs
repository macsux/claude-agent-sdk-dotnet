using Xunit;

namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that run the real Claude Code CLI.
/// Skipped (never failed) unless <c>CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS=1</c>
/// and a CLI is installed. The check happens at test-discovery time in the test
/// process, so no compile-time constant is involved. An explicit
/// <c>Skip = "..."</c> on the attribute (e.g. for a known SDK bug) still wins.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (IntegrationEnvironment.SkipReason is { } reason)
            Skip = reason;
    }
}
