using System.Collections.Concurrent;
using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>
/// <see cref="ClaudeAgentOptions.CanUseTool"/>: the CLI asks the SDK (can_use_tool
/// control request) before running a tool that needs permission. Writes in the default
/// permission mode always need permission, so each test's Write goes through the callback.
/// </summary>
[Trait("Category", "Integration")]
public class PermissionTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    private sealed record Call(string ToolName, JsonElement Input, ToolPermissionContext Context);

    private ClaudeAgentOptions WriteOptions(ConcurrentQueue<Call> calls, Func<Call, PermissionResult> decide,
        [System.Runtime.CompilerServices.CallerMemberName] string test = "") =>
        Options(test) with
        {
            Tools = ["Write"],
            CanUseTool = (tool, input, ctx, _) =>
            {
                var call = new Call(tool, input.Clone(), ctx);
                calls.Enqueue(call);
                return Task.FromResult(decide(call));
            },
        };

    [IntegrationFact]
    public Task Allow_ViaQueryAsyncStringPrompt_WriteRuns() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        // Also a regression check: Claude.QueryAsync(string) with CanUseTool as the only
        // control-protocol consumer must keep stdin open until the result, or the CLI's
        // permission request fails with "Stream closed" (Python e2e parity).
        var target = Path.Combine(Cwd, $"allowed-{attempt}.txt");
        var calls = new ConcurrentQueue<Call>();
        var options = WriteOptions(calls, _ => new PermissionResultAllow());

        var messages = await CollectAsync(Claude.QueryAsync(
            $"Use the Write tool to create the file {target} containing exactly the text hello.",
            options, Transport(options, suffix: attempt > 1 ? "retry" : null), Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");
        var call = Assert.Single(calls);
        Assert.Equal("Write", call.ToolName);
        Assert.Equal(target, call.Input.GetProperty("file_path").GetString());
        Assert.Equal("hello", call.Input.GetProperty("content").GetString()!.Trim());
        // The callback context identifies the exact tool call it is about.
        Assert.Equal(ToolUses(messages).First(t => t.Name == "Write").Id, call.Context.ToolUseId);

        Assert.True(File.Exists(target), "allowed Write should have created the file");
        Assert.Equal("hello", File.ReadAllText(target).Trim());
        Assert.Equal("success", SingleResult(messages).Subtype);
    }, Log);

    [IntegrationFact]
    public Task Deny_PreventsWrite_AndMessageReachesModel() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var target = Path.Combine(Cwd, $"denied-{attempt}.txt");
        var reason = $"denied-by-sdk-callback-{NewNonce()}";
        var calls = new ConcurrentQueue<Call>();
        var options = WriteOptions(calls, _ => new PermissionResultDeny(reason));

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {target} containing the text hello. " +
                                "If permission is denied, do not retry; reply DENIED.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Equal("Write", c.ToolName));
        Assert.False(File.Exists(target), "denied Write must not create the file");

        var writeUse = ToolUses(messages).First(t => t.Name == "Write");
        var toolResult = Assert.Single(ToolResults(messages), r => r.ToolUseId == writeUse.Id);
        Assert.True(toolResult.IsError);
        Assert.Contains(reason, ToolResultText(toolResult));
    }, Log);

    [IntegrationFact]
    public Task AllowWithUpdatedInput_RedirectsWriteToNewPath() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var requested = Path.Combine(Cwd, $"requested-{attempt}.txt");
        var redirected = Path.Combine(Cwd, $"redirected-{attempt}.txt");
        var calls = new ConcurrentQueue<Call>();
        var options = WriteOptions(calls, call =>
        {
            // Rewrite file_path, keep everything else the model asked for.
            var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.Input.GetRawText())!;
            input["file_path"] = JsonSerializer.SerializeToElement(redirected);
            return new PermissionResultAllow(UpdatedInput: JsonSerializer.SerializeToElement(input));
        });

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {requested} containing exactly the text moved.",
            cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");
        Assert.Equal(requested, calls.First().Input.GetProperty("file_path").GetString());

        // The CLI executed the SDK-supplied input, not the model's.
        Assert.False(File.Exists(requested), "original path must not be written");
        Assert.True(File.Exists(redirected), "the updatedInput path should have been written");
        Assert.Equal("moved", File.ReadAllText(redirected).Trim());
    }, Log);

    [IntegrationFact]
    public Task Context_CarriesPermissionSuggestions() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var target = Path.Combine(Cwd, $"suggest-{attempt}.txt");
        var calls = new ConcurrentQueue<Call>();
        var options = WriteOptions(calls, _ => new PermissionResultAllow());

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {target} containing the text x.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");
        var call = calls.First();
        Log("suggestions: " + JsonSerializer.Serialize(call.Context.Suggestions?.Select(s => s.ToDictionary())));

        // The CLI proposes permission updates (e.g. "switch to acceptEdits for this
        // session") and the SDK parses them from the wire format into PermissionUpdate.
        Assert.NotNull(call.Context.Suggestions);
        Assert.NotEmpty(call.Context.Suggestions!);
        Assert.All(call.Context.Suggestions!, s =>
        {
            Assert.True(Enum.IsDefined(s.Type));
            Assert.NotNull(s.Destination);
            if (s.Type == PermissionUpdateType.SetMode) Assert.NotNull(s.Mode);
            if (s.Type is PermissionUpdateType.AddRules or PermissionUpdateType.ReplaceRules)
            {
                Assert.NotNull(s.Rules);
                Assert.NotNull(s.Behavior);
            }
        });
    }, Log);

    [IntegrationFact]
    public Task AllowWithUpdatedPermissions_SessionRuleSkipsSecondPrompt() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        // Returning the CLI's own suggestion as updatedPermissions (destination=session)
        // must stick: the second Write in the same session is not asked about again.
        var first = Path.Combine(Cwd, $"first-{attempt}.txt");
        var second = Path.Combine(Cwd, $"second-{attempt}.txt");
        var calls = new ConcurrentQueue<Call>();
        var options = WriteOptions(calls, call => new PermissionResultAllow(
            UpdatedPermissions: call.Context.Suggestions));

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {first} containing the text 1. " +
                                $"Then use the Write tool again to create the file {second} containing the text 2.",
            cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Count(t => t.Name == "Write") >= 2, "model did not call Write twice");
        Assert.NotEmpty(calls.First().Context.Suggestions ?? []);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Single(calls);
    }, Log);

    [IntegrationFact(Skip = "SDK bug: PermissionMode.DontAsk (and .Auto) are public enum values but " +
                            "SubprocessTransport.PermissionModeToCliValue has no case for them, so ConnectAsync throws " +
                            "ArgumentOutOfRangeException(\"Unsupported permission mode\") before the CLI is spawned.")]
    public async Task PermissionModeOption_DontAsk_DeniesWithoutPrompting()
    {
        // PermissionMode.DontAsk is a public enum value (Python "dontAsk"): tools that are
        // not pre-approved are denied instead of prompting.
        var target = Path.Combine(Cwd, "dontask.txt");
        var options = Options() with { Tools = ["Write"], PermissionMode = PermissionMode.DontAsk };

        var messages = await CollectAsync(Claude.QueryAsync(
            $"Use the Write tool to create the file {target} containing the text x. If it fails, reply FAILED.",
            options, Transport(options), Ct));

        Assert.Equal("dontAsk", InitMessage(messages).Data.GetProperty("permissionMode").GetString());
        Assert.False(File.Exists(target));
    }
}
