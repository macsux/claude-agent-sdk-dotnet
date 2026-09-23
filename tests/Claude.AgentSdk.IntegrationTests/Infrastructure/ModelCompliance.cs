namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// Thrown when the model did not do what the prompt asked (e.g. never called the
/// tool the test needs), so the SDK behavior under test was never exercised.
/// This is model non-determinism, not an SDK failure.
/// </summary>
public sealed class ModelDidNotComplyException(string message) : Exception(message);

internal static class ModelCompliance
{
    /// <summary>
    /// Run <paramref name="attempt"/> and retry once if — and only if — it throws
    /// <see cref="ModelDidNotComplyException"/>. SDK assertions (xunit failures, protocol
    /// errors, timeouts) are never retried. Used only by tests whose precondition is
    /// "the model calls tool X", which haiku satisfies almost always but not always.
    /// Each attempt receives its 1-based number so it can use fresh file names.
    /// </summary>
    public static async Task RetryOnceAsync(Func<int, Task> attempt, Action<string> log)
    {
        try
        {
            await attempt(1);
        }
        catch (ModelDidNotComplyException ex)
        {
            log($"!! model did not comply on attempt 1 ({ex.Message}); retrying once");
            await attempt(2);
        }
    }

    public static void Require(bool condition, string whatTheModelFailedToDo)
    {
        if (!condition) throw new ModelDidNotComplyException(whatTheModelFailedToDo);
    }
}
