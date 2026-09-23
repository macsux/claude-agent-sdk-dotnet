using System.Collections.Concurrent;
using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>SDK hook callbacks invoked by the CLI over the control protocol.</summary>
[Trait("Category", "Integration")]
public class HookTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    private static readonly JsonSerializerOptions OmitNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [IntegrationFact]
    public async Task TypedPreToolUseOutput_DefaultSerialization_DenyIsEnforced()
    {
        var target = Path.Combine(Cwd, "typed-deny.txt");
        var options = Options() with
        {
            Tools = ["Write"],
            PermissionMode = PermissionMode.AcceptEdits,
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [Matcher("Write", (_, _, _, _) => Task.FromResult(new HookOutput
                {
                    // The natural way to use the SDK's typed output record.
                    HookSpecificOutput = JsonSerializer.SerializeToElement(new PreToolUseHookSpecificOutput
                    {
                        PermissionDecision = "deny",
                        PermissionDecisionReason = "no writes",
                    }),
                }))],
            },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {target} containing the text hello. " +
                                "If the tool is blocked, do not retry.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");
        Assert.False(File.Exists(target), "PreToolUse deny must prevent the write");
    }

    private static HookMatcher Matcher(string? matcher, HookCallback callback) =>
        new(Matcher: matcher, Hooks: [callback]);

    [IntegrationFact]
    public Task PreToolUse_Deny_PreventsFileWrite() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var target = Path.Combine(Cwd, $"blocked-{attempt}.txt");
        var reason = $"blocked-by-sdk-hook-{NewNonce()}";
        var invocations = new ConcurrentQueue<(JsonElement Input, string? ToolUseId)>();

        var options = Options() with
        {
            Tools = ["Write"],
            // acceptEdits would auto-approve the Write; the hook is the only gate.
            PermissionMode = PermissionMode.AcceptEdits,
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [Matcher("Write", (input, toolUseId, _, _) =>
                {
                    invocations.Enqueue((input.Clone(), toolUseId));
                    return Task.FromResult(new HookOutput
                    {
                        // Nulls omitted: see TypedPreToolUseOutput_DefaultSerialization_DenyIsEnforced.
                        HookSpecificOutput = JsonSerializer.SerializeToElement(new PreToolUseHookSpecificOutput
                        {
                            PermissionDecision = "deny",
                            PermissionDecisionReason = reason,
                        }, OmitNulls),
                    });
                })],
            },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {target} containing the text hello. " +
                                "If the tool is blocked, do not retry; just reply BLOCKED.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");

        // The hook ran with the real tool call's name, input and id...
        // (The model may retry after the denial; every attempt must be denied.)
        Assert.NotEmpty(invocations);
        var (input, toolUseId) = invocations.First();
        Assert.Equal("PreToolUse", input.GetProperty("hook_event_name").GetString());
        Assert.Equal("Write", input.GetProperty("tool_name").GetString());
        Assert.Equal(target, input.GetProperty("tool_input").GetProperty("file_path").GetString());
        var toolUse = ToolUses(messages).First(t => t.Name == "Write");
        Assert.Equal(toolUse.Id, toolUseId);
        Assert.Equal(toolUse.Id, input.GetProperty("tool_use_id").GetString());
        Assert.Equal(Cwd, input.GetProperty("cwd").GetString());
        Assert.False(string.IsNullOrEmpty(input.GetProperty("session_id").GetString()));

        // ...and its deny decision was enforced: nothing on disk, error tool result
        // carrying the hook's reason back to the model.
        Assert.False(File.Exists(target), "PreToolUse deny must prevent the write");
        Assert.All(invocations, i => Assert.Equal("Write", i.Input.GetProperty("tool_name").GetString()));
        var toolResult = Assert.Single(ToolResults(messages), r => r.ToolUseId == toolUse.Id);
        Assert.True(toolResult.IsError);
        Assert.Contains(reason, ToolResultText(toolResult));
    }, Log);

    [IntegrationFact]
    public Task PostToolUse_ReceivesToolResponse() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var target = Path.Combine(Cwd, $"written-{attempt}.txt");
        var content = NewNonce("CONTENT");
        var pre = new ConcurrentQueue<JsonElement>();
        var post = new ConcurrentQueue<JsonElement>();

        var options = Options() with
        {
            Tools = ["Write"],
            PermissionMode = PermissionMode.AcceptEdits,
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [Matcher(null, (input, _, _, _) =>
                {
                    pre.Enqueue(input.Clone());
                    return Task.FromResult(new HookOutput());
                })],
                [HookEvent.PostToolUse] = [Matcher("Write", (input, _, _, _) =>
                {
                    post.Enqueue(input.Clone());
                    return Task.FromResult(new HookOutput { Continue = true });
                })],
            },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create the file {target} containing exactly the text {content}.",
            cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");

        Assert.True(File.Exists(target));
        Assert.Equal(content, File.ReadAllText(target).Trim());

        // Pre ran before post for the same tool call.
        var postInput = Assert.Single(post);
        var writeUse = ToolUses(messages).Single(t => t.Name == "Write");
        Assert.Equal(writeUse.Id, postInput.GetProperty("tool_use_id").GetString());
        Assert.Contains(pre, p => p.GetProperty("tool_use_id").GetString() == writeUse.Id);

        Assert.Equal("PostToolUse", postInput.GetProperty("hook_event_name").GetString());
        Assert.Equal("Write", postInput.GetProperty("tool_name").GetString());
        Assert.Equal(target, postInput.GetProperty("tool_input").GetProperty("file_path").GetString());
        // tool_response is the tool's structured output: it names the file it wrote.
        var response = postInput.GetProperty("tool_response");
        Assert.NotEqual(JsonValueKind.Undefined, response.ValueKind);
        Assert.NotEqual(JsonValueKind.Null, response.ValueKind);
        Assert.Contains(Path.GetFileName(target), response.GetRawText());
    }, Log);

    [IntegrationFact]
    public async Task UserPromptSubmit_ReceivesPrompt_AndAdditionalContextReachesModel()
    {
        var secret = NewNonce("SECRET");
        var prompts = new ConcurrentQueue<string>();
        var options = Options() with
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.UserPromptSubmit] = [Matcher(null, (input, _, _, _) =>
                {
                    prompts.Enqueue(input.GetProperty("prompt").GetString()!);
                    return Task.FromResult(new HookOutput
                    {
                        HookSpecificOutput = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["hookEventName"] = "UserPromptSubmit",
                            ["additionalContext"] = $"The secret word is {secret}.",
                        }),
                    });
                })],
            },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);
        const string prompt = "What is the secret word? Reply with only the secret word.";
        await client.QueryAsync(prompt, cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        Assert.Equal(prompt, Assert.Single(prompts));
        Assert.Equal("success", SingleResult(messages).Subtype);
        Assert.Contains(secret, AllAssistantText(messages));
    }
}
