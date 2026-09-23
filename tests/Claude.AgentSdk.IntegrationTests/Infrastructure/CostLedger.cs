using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Claude.AgentSdk.IntegrationTests.Infrastructure;

/// <summary>
/// Process-wide record of what the live suite spent, taken from
/// <see cref="ResultMessage.TotalCostUsd"/>. Each test prints its own cost and the
/// running total; the full table is written to <c>integration-costs.log</c> next to
/// the test assembly (and to stderr) when the test process exits.
/// </summary>
internal static class CostLedger
{
    private static readonly ConcurrentDictionary<string, decimal> _perTest = new();
    private static readonly ConcurrentDictionary<string, TimeSpan> _durations = new();
    private static long _totalMicroUsd;

    static CostLedger()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteSummary();
    }

    public static decimal Total => Interlocked.Read(ref _totalMicroUsd) / 1_000_000m;

    public static void Add(string test, decimal usd)
    {
        _perTest.AddOrUpdate(test, usd, (_, prev) => prev + usd);
        Interlocked.Add(ref _totalMicroUsd, (long)Math.Round(usd * 1_000_000m));
    }

    public static void RecordDuration(string test, TimeSpan duration) => _durations[test] = duration;

    public static decimal For(string test) => _perTest.TryGetValue(test, out var v) ? v : 0m;

    /// <summary>Fail fast instead of spending past the configured cap.</summary>
    public static void EnsureBudgetRemaining()
    {
        var cap = IntegrationEnvironment.MaxTotalUsd;
        if (Total >= cap)
            throw new InvalidOperationException(
                $"Integration suite spend ${Total:F4} reached the cap of ${cap} " +
                $"({IntegrationEnvironment.MaxTotalVar}); refusing to start another live test.");
    }

    private static void WriteSummary()
    {
        if (_perTest.IsEmpty && _durations.IsEmpty) return;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Claude.AgentSdk integration run {DateTime.UtcNow:O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# model={IntegrationEnvironment.Model}");
        foreach (var name in _perTest.Keys.Union(_durations.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var cost = For(name);
            var dur = _durations.TryGetValue(name, out var d) ? d.TotalSeconds : 0;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{cost,10:F6} USD  {dur,6:F1}s  {name}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Total,10:F6} USD  TOTAL");
        var text = sb.ToString();
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "integration-costs.log"), text);
        }
        catch
        {
            // Best effort only.
        }
        Console.Error.Write(text);
    }
}
