using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>Bidirectional <see cref="ClaudeSDKClient"/> sessions and its control requests.</summary>
[Trait("Category", "Integration")]
public class ClientTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    [IntegrationFact]
    public async Task MultiTurn_RetainsContextAcrossQueries()
    {
        var nonce = NewNonce("CODE");
        var options = Options();
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        await client.QueryAsync($"My project's codename is {nonce}. Please keep that in mind. Acknowledge with just OK.", cancellationToken: Ct);
        var first = await CollectAsync(client.ReceiveResponseAsync(Ct));

        await client.QueryAsync("What is the codename of my project? Answer with just the codename.", cancellationToken: Ct);
        var second = await CollectAsync(client.ReceiveResponseAsync(Ct));

        var r1 = SingleResult(first);
        var r2 = SingleResult(second);
        Assert.Equal("success", r1.Subtype);
        Assert.Equal("success", r2.Subtype);
        // Same CLI process, same conversation.
        Assert.Equal(r1.SessionId, r2.SessionId);
        // ReceiveResponseAsync stops at (and includes) the result of each turn.
        Assert.IsType<ResultMessage>(first[^1]);
        Assert.IsType<ResultMessage>(second[^1]);
        Assert.Contains(nonce, AllAssistantText(second));
    }

    [IntegrationFact]
    public async Task Interrupt_StopsLongResponse_AndClientRemainsUsable()
    {
        var options = Options() with { IncludePartialMessages = true };
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        await client.QueryAsync(
            "Write the integers from 1 to 400, one per line, as plain digits, with no other text.",
            cancellationToken: Ct);

        var messages = new List<Message>();
        var interrupted = false;
        await foreach (var m in client.ReceiveResponseAsync(Ct))
        {
            messages.Add(m);
            Observe(m);
            // Interrupt as soon as the model has started streaming text.
            if (!interrupted && m is StreamEvent se &&
                se.Event.GetProperty("type").GetString() == "content_block_delta")
            {
                interrupted = true;
                await client.InterruptAsync(Ct);
                Log("-> interrupt sent");
            }
        }

        Assert.True(interrupted, "never saw a streamed text delta to interrupt");
        var result = SingleResult(messages);
        // The turn ends early rather than running to completion.
        Assert.NotEqual("end_turn", result.StopReason);
        Assert.DoesNotContain("\n400", AllAssistantText(messages));

        // The same connection still serves a new turn.
        await client.QueryAsync("Reply with just the word READY.", cancellationToken: Ct);
        var after = await CollectAsync(client.ReceiveResponseAsync(Ct));
        var r2 = SingleResult(after);
        Assert.Equal("success", r2.Subtype);
        Assert.Contains("READY", r2.Result, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationFact]
    public async Task GetServerInfo_ReturnsInitializeResponse_WithoutAnyModelCall()
    {
        var options = Options();
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        var info = client.GetServerInfo();

        Assert.NotNull(info);
        var root = info!.Value;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        // The initialize response advertises the slash commands, output styles and models.
        Assert.True(root.TryGetProperty("commands", out var commands), root.GetRawText()[..Math.Min(500, root.GetRawText().Length)]);
        Assert.Equal(JsonValueKind.Array, commands.ValueKind);
        Assert.True(commands.GetArrayLength() > 0);
        Assert.All(commands.EnumerateArray(), c => Assert.False(string.IsNullOrEmpty(c.GetProperty("name").GetString())));
        Assert.True(root.TryGetProperty("output_style", out _) || root.TryGetProperty("available_output_styles", out _),
            "initialize response should describe output styles");
    }

    [IntegrationFact]
    public async Task GetContextUsage_ReportsCategoriesAndModel()
    {
        var options = Options();
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        var usage = await client.GetContextUsageAsync(Ct);

        Assert.Equal(Model, usage.Model);
        Assert.NotEmpty(usage.Categories);
        Assert.All(usage.Categories, c => Assert.False(string.IsNullOrEmpty(c.Name)));
        Assert.True(usage.MaxTokens > 0);
        Assert.True(usage.TotalTokens >= 0);
        Assert.InRange(usage.Percentage, 0, 100);
        Assert.True(usage.TotalTokens <= usage.MaxTokens);
    }

    [IntegrationFact]
    public async Task SetModel_SwitchesModelForNextTurn()
    {
        // Start on a different (more expensive) model but switch before the first
        // query, so the only API call is made with the cheap target model.
        var start = Model.Contains("haiku", StringComparison.Ordinal) ? "claude-sonnet-4-5" : "claude-haiku-4-5";
        var options = Options() with { Model = start };
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        await client.SetModelAsync(Model, Ct);
        await client.QueryAsync("Reply with just OK.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        Assert.Equal("success", SingleResult(messages).Subtype);
        var assistant = messages.OfType<AssistantMessage>().ToList();
        Assert.NotEmpty(assistant);
        Assert.All(assistant, a => Assert.Equal(Model, a.Model));
    }

    [IntegrationFact]
    public async Task SetPermissionMode_AcceptEdits_LetsWriteRunWithoutCallback()
    {
        // Default permission mode, no CanUseTool callback: the CLI cannot ask anyone,
        // so a Write is refused. After switching to acceptEdits the same Write runs.
        var options = Options() with { Tools = ["Write"] };
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        var denied = Path.Combine(Cwd, "denied.txt");
        await client.QueryAsync($"Use the Write tool to create the file {denied} containing the text one. " +
                                "If it fails, do not retry; just say FAILED.", cancellationToken: Ct);
        var first = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(ToolUses(first).Any(t => t.Name == "Write"), "model never called Write");
        Assert.False(File.Exists(denied), "Write must not run in default mode without a permission callback");
        Assert.Contains(ToolResults(first), r => r.IsError == true);

        await client.SetPermissionModeAsync("acceptEdits", Ct);

        var allowed = Path.Combine(Cwd, "allowed.txt");
        await client.QueryAsync($"Use the Write tool to create the file {allowed} containing exactly the text two.",
            cancellationToken: Ct);
        var second = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(ToolUses(second).Any(t => t.Name == "Write"), "model never called Write");
        Assert.True(File.Exists(allowed), "Write should run after switching to acceptEdits");
        Assert.Equal("two", File.ReadAllText(allowed).Trim());
    }
}
