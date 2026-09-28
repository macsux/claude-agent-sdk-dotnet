using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>One-shot <see cref="Claude.QueryAsync(string, ClaudeAgentOptions?, Transport.ITransport?, CancellationToken)"/>.</summary>
[Trait("Category", "Integration")]
public class QueryTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    [IntegrationFact]
    public async Task StringPrompt_YieldsInitAssistantAndSuccessResult()
    {
        var options = Options();

        var messages = await CollectAsync(Claude.QueryAsync(
            "Reply with exactly the single word PONG and nothing else.",
            options, Transport(options), Ct));

        // Stream shape: init first, result last, at least one assistant message in between.
        var init = Assert.IsType<SystemInitMessage>(messages[0]);
        Assert.Equal("init", init.Subtype);
        Assert.IsType<ResultMessage>(messages[^1]);

        var sessionId = init.Data.GetProperty("session_id").GetString();
        Assert.True(Guid.TryParse(sessionId, out _), $"init session_id should be a UUID, got '{sessionId}'");
        Assert.Equal(Model, init.Data.GetProperty("model").GetString());
        Assert.Equal(Cwd, init.Data.GetProperty("cwd").GetString());
        // Tools = [] => `--tools ""`: the CLI must expose no built-in tools.
        Assert.Equal(0, init.Data.GetProperty("tools").GetArrayLength());
        // StrictMcpConfig + SettingSources=[] => none of the developer's MCP servers leaked in.
        Assert.Equal(0, init.Data.GetProperty("mcp_servers").GetArrayLength());

        var assistant = messages.OfType<AssistantMessage>().ToList();
        Assert.NotEmpty(assistant);
        Assert.All(assistant, a =>
        {
            Assert.Equal(Model, a.Model);
            Assert.Equal(sessionId, a.SessionId);
            Assert.False(string.IsNullOrEmpty(a.Uuid));
            Assert.StartsWith("msg_", a.MessageId);
            Assert.NotNull(a.Usage);
        });

        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.False(result.IsError);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(1, result.NumTurns);
        Assert.True(result.TotalCostUsd > 0, "a real API call must report a non-zero cost");
        Assert.True(result.DurationMs > 0);
        Assert.True(result.DurationApiMs > 0);
        Assert.Equal("end_turn", result.StopReason);
        Assert.True(result.Usage!.Value.GetProperty("output_tokens").GetInt32() > 0);
        Assert.Contains("PONG", result.Result, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationFact]
    public async Task StreamingPromptIterable_MultipleUserMessages_AreAllAnswered()
    {
        var nonce = NewNonce();
        var options = Options();

        async IAsyncEnumerable<Dictionary<string, object?>> Prompts()
        {
            yield return UserMessage($"My project's codename is {nonce}. Please keep that in mind. Acknowledge with just OK.");
            await Task.Yield();
            yield return UserMessage("What is the codename of my project? Answer with just the codename.");
        }

        var messages = await CollectAsync(Claude.QueryAsync(Prompts(), options, Transport(options), Ct));

        // Two user turns => two results, both successful, same session.
        var results = messages.OfType<ResultMessage>().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("success", r.Subtype));
        Assert.Single(results.Select(r => r.SessionId).Distinct());
        Assert.Contains(nonce, results[1].Result);
    }

    private static Dictionary<string, object?> UserMessage(string text) => new()
    {
        ["type"] = "user",
        ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = text },
        ["parent_tool_use_id"] = null,
        ["session_id"] = "",
    };
}
