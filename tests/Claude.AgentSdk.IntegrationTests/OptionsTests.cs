using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>Options that change what the CLI returns: output format, limits, streaming, stderr.</summary>
[Trait("Category", "Integration")]
public class OptionsTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    [IntegrationFact]
    public async Task OutputFormat_JsonSchema_ProducesValidStructuredOutput()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "json_schema",
            schema = new
            {
                type = "object",
                properties = new
                {
                    capital = new { type = "string" },
                    country = new { type = "string" },
                    population_millions = new { type = "number" },
                },
                required = new[] { "capital", "country", "population_millions" },
                additionalProperties = false,
            },
        });
        var options = Options() with { OutputFormat = schema, MaxTurns = 4 };

        var messages = await CollectAsync(Claude.QueryAsync(
            "What is the capital of France and roughly how many million people live in France?",
            options, Transport(options), Ct));

        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.NotNull(result.StructuredOutput);
        var so = result.StructuredOutput!.Value;
        Assert.Equal(JsonValueKind.Object, so.ValueKind);
        Assert.Equal("Paris", so.GetProperty("capital").GetString());
        Assert.Equal(JsonValueKind.String, so.GetProperty("country").ValueKind);
        Assert.Equal(JsonValueKind.Number, so.GetProperty("population_millions").ValueKind);
        Assert.InRange(so.GetProperty("population_millions").GetDouble(), 30, 120);
    }

    [IntegrationFact]
    public async Task MaxTurns_Exceeded_YieldsErrorMaxTurnsResultThenThrows()
    {
        var options = Options() with
        {
            Tools = ["Write"],
            PermissionMode = PermissionMode.AcceptEdits,
            MaxTurns = 1,
        };
        var a = Path.Combine(Cwd, "a.txt");
        var b = Path.Combine(Cwd, "b.txt");

        var (messages, error) = await CollectUntilErrorAsync(Claude.QueryAsync(
            $"First use the Write tool to create {a} containing a. After that succeeds, use the Write tool " +
            $"to create {b} containing b. Then say DONE.",
            options, Transport(options), Ct));

        // The error result is yielded first (Python parity)...
        var result = SingleResult(messages);
        Assert.Equal("error_max_turns", result.Subtype);
        Assert.True(result.IsError);
        Assert.Same(result, messages[^1]);
        Assert.False(File.Exists(b), "the second tool call is beyond the turn limit");
        // ...then the CLI's non-zero exit surfaces as an exception carrying the CLI's
        // reason instead of a bare "exit code 1".
        var pex = Assert.IsAssignableFrom<ProcessException>(error);
        Assert.Contains("maximum number of turns", pex.Message);
        Assert.Equal(1, pex.ExitCode);
    }

    [IntegrationFact]
    public async Task MaxBudgetUsd_Exceeded_YieldsErrorMaxBudgetResultThenThrows()
    {
        // Any real call costs more than this, so the CLI stops after the first response.
        var options = Options() with { MaxBudgetUsd = 0.000001m };

        var (messages, error) = await CollectUntilErrorAsync(
            Claude.QueryAsync("Reply with just OK.", options, Transport(options), Ct));

        var result = SingleResult(messages);
        Assert.Equal("error_max_budget_usd", result.Subtype);
        Assert.True(result.IsError);
        Assert.True(result.TotalCostUsd > options.MaxBudgetUsd);
        var pex = Assert.IsAssignableFrom<ProcessException>(error);
        Assert.Contains("maximum budget", pex.Message);
    }

    [IntegrationFact(Skip = "SDK bug: after an is_error result the SDK throws a plain ProcessException " +
                            "(QueryHandler.ReadMessagesLoopAsync), never the public ResultException that carries " +
                            "Subtype/Errors; Python raises ResultError(subtype=...) in the same situation.")]
    public async Task ErrorResult_SurfacesAsTypedResultException()
    {
        var options = Options() with { MaxBudgetUsd = 0.000001m };

        var (_, error) = await CollectUntilErrorAsync(
            Claude.QueryAsync("Reply with just OK.", options, Transport(options), Ct));

        var rex = Assert.IsType<ResultException>(error);
        Assert.Equal("error_max_budget_usd", rex.Subtype);
    }

    [IntegrationFact]
    public async Task IncludePartialMessages_StreamEventsReassembleToFinalText()
    {
        var options = Options() with { IncludePartialMessages = true };

        var messages = await CollectAsync(Claude.QueryAsync(
            "Write one short sentence about the sea.", options, Transport(options), Ct));

        var events = messages.OfType<StreamEvent>().ToList();
        Assert.NotEmpty(events);
        var types = events.Select(e => e.Event.GetProperty("type").GetString()).ToList();
        Assert.Equal("message_start", types.First());
        Assert.Contains("content_block_start", types);
        Assert.Contains("content_block_delta", types);
        Assert.Contains("content_block_stop", types);
        Assert.Contains("message_stop", types);
        // Stream events precede the assistant message they describe.
        Assert.True(messages.IndexOf(events[0]) < messages.FindIndex(m => m is AssistantMessage));

        var sessionId = SingleResult(messages).SessionId;
        Assert.All(events, e =>
        {
            Assert.Equal(sessionId, e.SessionId);
            Assert.False(string.IsNullOrEmpty(e.Uuid));
            Assert.Null(e.ParentToolUseId);
        });

        // Concatenated text_delta fragments == the final TextBlock text.
        var streamed = new StringBuilder();
        foreach (var e in events.Where(e => e.Event.GetProperty("type").GetString() == "content_block_delta"))
        {
            var delta = e.Event.GetProperty("delta");
            if (delta.GetProperty("type").GetString() == "text_delta")
                streamed.Append(delta.GetProperty("text").GetString());
        }
        Assert.Equal(AllAssistantText(messages), streamed.ToString());
    }

    [IntegrationFact]
    public async Task StderrCallback_ReceivesDebugOutput()
    {
        var lines = new ConcurrentQueue<string>();
        var options = Options() with
        {
            StderrCallback = lines.Enqueue,
            ExtraArgs = new Dictionary<string, string?> { ["debug-to-stderr"] = null },
        };

        var messages = await CollectAsync(Claude.QueryAsync("Reply with just OK.", options, Transport(options), Ct));

        Assert.Equal("success", SingleResult(messages).Subtype);
        Assert.NotEmpty(lines);
        Log($"stderr lines: {lines.Count}; first: {lines.First()}");
        // Lines are delivered one at a time, already split and trimmed.
        Assert.All(lines, l => Assert.DoesNotContain('\n', l));
        Assert.Contains(lines, l => l.Contains("DEBUG", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationFact]
    public async Task SystemPrompt_String_IsUsed()
    {
        var marker = NewNonce("ZEBRA");
        var options = Options() with
        {
            SystemPrompt = $"You are a terse assistant. You must end every reply with the token {marker}.",
        };

        var messages = await CollectAsync(Claude.QueryAsync("Say hello.", options, Transport(options), Ct));

        Assert.Contains(marker, SingleResult(messages).Result);
    }

    [IntegrationFact]
    public async Task SystemPrompt_PresetWithAppend_IsUsed()
    {
        var marker = NewNonce("OKAPI");
        var options = Options() with
        {
            SystemPrompt = SystemPromptPreset.ClaudeCode($"Always end every reply with the token {marker}."),
        };

        var messages = await CollectAsync(Claude.QueryAsync("Say hello.", options, Transport(options), Ct));

        Assert.Contains(marker, SingleResult(messages).Result);
    }

    [IntegrationFact]
    public async Task SystemPrompt_PresetVsString_ChangesSystemPromptSize()
    {
        // No model call: compare the CLI's own accounting of the system prompt.
        async Task<int> SystemPromptTokens(SystemPromptConfig? systemPrompt)
        {
            var options = Options() with { SystemPrompt = systemPrompt };
            await using var client = new ClaudeSDKClient(options);
            await client.ConnectAsync(cancellationToken: Ct);
            var usage = await client.GetContextUsageAsync(Ct);
            var cat = usage.Categories.FirstOrDefault(c => c.Name.Contains("System prompt", StringComparison.OrdinalIgnoreCase));
            Log($"{systemPrompt?.GetType().Name ?? "null"}: " +
                string.Join(", ", usage.Categories.Select(c => $"{c.Name}={c.Tokens}")));
            Assert.NotNull(cat);
            return cat!.Tokens;
        }

        var custom = await SystemPromptTokens("You are terse.");
        var preset = await SystemPromptTokens(SystemPromptPreset.ClaudeCode());

        Assert.True(preset > custom + 500,
            $"claude_code preset system prompt ({preset} tokens) should be far larger than a one-line string ({custom})");
    }
}
