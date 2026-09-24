using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Claude.AgentSdk.Mcp;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

public sealed record CountryFacts(string Capital, string Country, double PopulationMillions);

public sealed record WordArgs(string Word);

[JsonSerializable(typeof(CountryFacts))]
[JsonSerializable(typeof(WordArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ConvenienceJson : JsonSerializerContext;

public sealed class WordTools
{
    public ConcurrentQueue<string> Seen { get; } = new();

    [McpTool("reverse_word", Description = "Reverse the letters of a word.", ReadOnly = true)]
    public string Reverse(WordArgs args)
    {
        Seen.Enqueue(args.Word);
        return new string(args.Word.Reverse().ToArray());
    }
}

/// <summary>The .NET convenience surface (QueryTextAsync, QueryAsync&lt;T&gt;, [McpTool], ILogger) against the real CLI.</summary>
[Trait("Category", "Integration")]
public class ConvenienceTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    [IntegrationFact]
    public async Task QueryTextAsync_ReturnsFinalAnswerText()
    {
        var options = Options();
        var answer = await Claude.QueryTextAsync(
            "What is 17 * 3? Reply with only the number.", options, Transport(options), Ct);

        Assert.Equal("51", answer.Trim().TrimEnd('.'));
    }

    [IntegrationFact]
    public async Task QueryAsyncT_ReturnsTypedStructuredOutput()
    {
        var options = Options() with { MaxTurns = 4 };
        var facts = await Claude.QueryAsync(
            "What is the capital of France, and roughly how many million people live in France?",
            ConvenienceJson.Default.CountryFacts, options, Transport(options), Ct);

        Assert.Equal("Paris", facts.Capital);
        Assert.Contains("France", facts.Country);
        Assert.InRange(facts.PopulationMillions, 50, 90);
    }

    [IntegrationFact]
    public Task AttributedTool_IsCalledWithExactArguments() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var tools = new WordTools();
        var word = $"zq{NewNonce().ToLowerInvariant()}";
        var options = Options() with
        {
            McpServers = McpServers.Sdk("words", b => b.ToolsFrom(tools, ConvenienceJson.Default)),
            AllowedTools = ["mcp__words__reverse_word"],
        };

        var answer = await Claude.QueryTextAsync(
            $"Call the mcp__words__reverse_word tool exactly once with word=\"{word}\", " +
            "then reply with only the text it returned.",
            options, Transport(options, suffix: attempt > 1 ? "retry" : null), Ct);

        ModelCompliance.Require(!tools.Seen.IsEmpty, "model never called mcp__words__reverse_word");
        Assert.Equal(word, Assert.Single(tools.Seen));
        Assert.Contains(new string(word.Reverse().ToArray()), answer);
    }, Log);

    [IntegrationFact]
    public async Task Logger_SeesSpawnAndControlTraffic_WithoutPromptText()
    {
        var logger = new ListLogger();
        var secret = $"secret-{NewNonce()}";
        var options = Options() with { Logger = logger };

        await Claude.QueryTextAsync($"Reply with the word ok. ({secret})", options, Transport(options), Ct);

        var lines = logger.Lines.ToArray();
        Assert.Contains(lines, l => l.Contains("Started Claude Code CLI") && l.Contains("--input-format"));
        Assert.Contains(lines, l => l.Contains("Sending control request initialize"));
        Assert.Contains(lines, l => l.Contains("exited with code 0"));
        Assert.DoesNotContain(lines, l => l.Contains(secret));
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}
