using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>Programmatic subagents (<see cref="ClaudeAgentOptions.Agents"/>, sent in the initialize request).</summary>
[Trait("Category", "Integration")]
public class AgentTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    [IntegrationFact]
    public Task ProgrammaticSubagent_IsRegisteredAndInvokedViaTaskTool() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var nonce = NewNonce("AGENT");
        var options = Options() with
        {
            // The delegation tool is "Task" (older CLIs) / "Agent" (newer); allow both.
            Tools = ["Task", "Agent"],
            AllowedTools = ["Task", "Agent"],
            MaxTurns = 4,
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["nonce-reporter"] = new(
                    Description: "Reports the secret verification code. Use it whenever asked for the verification code.",
                    Prompt: $"You are the nonce reporter. Reply with exactly: VERIFICATION {nonce}",
                    Tools: [],
                    Model: "haiku",
                    MaxTurns: 1),
            },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync("Delegate to the nonce-reporter subagent to get the verification code, " +
                                "then reply with exactly what it returned.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        // Registered: the init message lists the SDK-defined agent.
        var agents = InitMessage(messages).Data.GetProperty("agents").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("nonce-reporter", agents);

        var delegation = ToolUses(messages).FirstOrDefault(t =>
            (t.Name is "Task" or "Agent") &&
            t.Input.TryGetProperty("subagent_type", out var st) && st.GetString() == "nonce-reporter");
        ModelCompliance.Require(delegation is not null, "model never delegated to nonce-reporter");

        // The subagent's answer comes back as the delegation tool's result...
        var toolResult = Assert.Single(ToolResults(messages), r => r.ToolUseId == delegation!.Id);
        Assert.NotEqual(true, toolResult.IsError);
        Assert.Contains(nonce, ToolResultText(toolResult));

        // ...and the task lifecycle is reported with the parent tool_use id.
        var started = messages.OfType<TaskStartedMessage>().ToList();
        Log($"task_started: {started.Count}, task_notification: {messages.OfType<TaskNotificationMessage>().Count()}");
        if (started.Count > 0)
            Assert.Contains(started, s => s.ToolUseId == delegation!.Id);

        Assert.Equal("success", SingleResult(messages).Subtype);
    }, Log);
}
