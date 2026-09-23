using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Claude.AgentSdk.Mcp;
using Xunit;

namespace Claude.AgentSdk.Tests.Replay;

/// <summary>
/// Drives the real SDK code paths (Claude.QueryAsync / ClaudeSDKClient → QueryHandler →
/// MessageParser, hook / permission / SDK-MCP control handlers) against recorded real
/// CLI sessions. The <see cref="ReplayTransport"/> fails the test if the SDK does not
/// write what the SDK wrote during the live recording (e.g. a missing control response),
/// so these check both directions of the protocol, deterministically and for free.
/// Recording scenarios mirror tests in Claude.AgentSdk.IntegrationTests.
/// </summary>
public class RecordedSessionReplayTests
{
    private readonly CancellationToken Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static readonly JsonSerializerOptions OmitNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task<List<Message>> Drain(IAsyncEnumerable<Message> stream)
    {
        var list = new List<Message>();
        await foreach (var m in stream.WithCancellation(Timeout)) list.Add(m);
        return list;
    }

    private static JsonElement ControlRequest(Fixture f, string subtype) => f.Incoming.Single(m =>
        m.GetProperty("type").GetString() == "control_request" &&
        m.GetProperty("request").GetProperty("subtype").GetString() == subtype);

    private static IEnumerable<JsonElement> ControlRequests(Fixture f, string subtype) => f.Incoming.Where(m =>
        m.GetProperty("type").GetString() == "control_request" &&
        m.GetProperty("request").GetProperty("subtype").GetString() == subtype);

    private static string Rid(JsonElement controlRequest) => controlRequest.GetProperty("request_id").GetString()!;

    private static IEnumerable<JsonElement> RawToolResults(IEnumerable<Message> messages) =>
        messages.OfType<UserMessage>()
            .Where(u => u.Content.ValueKind == JsonValueKind.Array)
            .SelectMany(u => u.Content.EnumerateArray())
            .Where(b => b.GetProperty("type").GetString() == "tool_result");

    [Fact]
    public async Task QueryStringPrompt_InitializeThenPrompt_AllMessageShapesParse()
    {
        var fixture = Fixture.Load("query_string_prompt");
        var transport = new ReplayTransport(fixture);

        var messages = await Drain(Claude.QueryAsync("Reply with exactly the single word PONG and nothing else.",
            new ClaudeAgentOptions(), transport));

        // SDK → CLI: initialize first, then the prompt as a stream-json user message, then EOF.
        Assert.Equal("initialize", transport.Written[0].GetProperty("request").GetProperty("subtype").GetString());
        var user = transport.Written[1];
        Assert.Equal("user", user.GetProperty("type").GetString());
        Assert.Equal("Reply with exactly the single word PONG and nothing else.",
            user.GetProperty("message").GetProperty("content").GetString());
        Assert.True(transport.InputEnded);

        // CLI → SDK: init, rate limit, assistant (thinking + text), result.
        var init = Assert.IsType<SystemMessage>(messages[0]);
        Assert.Equal("init", init.Subtype);
        var sessionId = init.Data.GetProperty("session_id").GetString();
        var rate = Assert.Single(messages.OfType<RateLimitEvent>());
        Assert.Equal(RateLimitStatus.Allowed, rate.RateLimitInfo.Status);
        Assert.Equal(RateLimitType.FiveHour, rate.RateLimitInfo.RateLimitType);
        Assert.Equal(sessionId, rate.SessionId);

        var blocks = messages.OfType<AssistantMessage>().SelectMany(a => a.Content).ToList();
        Assert.Contains(blocks, b => b is ThinkingBlock { Signature.Length: > 0 });
        Assert.Equal("PONG", Assert.Single(blocks.OfType<TextBlock>()).Text);

        var result = Assert.IsType<ResultMessage>(messages[^1]);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal("PONG", result.Result);
        Assert.Equal("end_turn", result.StopReason);
        Assert.True(result.TotalCostUsd > 0);
        Assert.True(result.Usage!.Value.GetProperty("output_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task ClientMultiTurn_TwoResultsSameSession_SecondRecallsCodename()
    {
        var fixture = Fixture.Load("client_multi_turn");
        var codename = fixture.FirstPrompt.Split(' ').Single(w => w.StartsWith("CODE", StringComparison.Ordinal)).TrimEnd('.');
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions(), new ReplayTransport(fixture));
        await client.ConnectAsync(cancellationToken: Timeout);

        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var first = await Drain(client.ReceiveResponseAsync(Timeout));
        await client.QueryAsync("What is the codename of my project?", cancellationToken: Timeout);
        var second = await Drain(client.ReceiveResponseAsync(Timeout));

        var r1 = Assert.IsType<ResultMessage>(first[^1]);
        var r2 = Assert.IsType<ResultMessage>(second[^1]);
        Assert.Equal(r1.SessionId, r2.SessionId);
        Assert.Equal(codename, r2.Result!.Trim());
    }

    [Fact]
    public async Task ClientInterrupt_RoundTrips_AndNextTurnSucceeds()
    {
        var fixture = Fixture.Load("client_interrupt");
        var transport = new ReplayTransport(fixture);
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions { IncludePartialMessages = true }, transport);
        await client.ConnectAsync(cancellationToken: Timeout);

        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var messages = new List<Message>();
        var interrupted = false;
        await foreach (var m in client.ReceiveResponseAsync(Timeout))
        {
            messages.Add(m);
            if (!interrupted && m is StreamEvent se && se.Event.GetProperty("type").GetString() == "content_block_delta")
            {
                interrupted = true;
                await client.InterruptAsync(Timeout); // must complete: the replayed control_response is re-keyed to our id
            }
        }

        Assert.True(interrupted);
        Assert.Single(transport.RequestsOf("interrupt"));
        Assert.Contains(messages.OfType<UserMessage>(), u => u.Content.GetRawText().Contains("[Request interrupted by user]"));
        var result = Assert.IsType<ResultMessage>(messages[^1]);
        Assert.Null(result.StopReason);

        await client.QueryAsync("Reply with just the word READY.", cancellationToken: Timeout);
        var after = await Drain(client.ReceiveResponseAsync(Timeout));
        var r2 = Assert.IsType<ResultMessage>(after[^1]);
        Assert.Equal("success", r2.Subtype);
        Assert.Equal(result.SessionId, r2.SessionId);
    }

    [Fact]
    public async Task HookPreToolUseDeny_CallbackGetsRealInput_AndDenyDecisionIsSent()
    {
        var fixture = Fixture.Load("hook_pre_tool_use_deny");
        var transport = new ReplayTransport(fixture);
        var inputs = new ConcurrentQueue<(JsonElement Input, string? ToolUseId)>();
        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher("Write", [(input, id, _, _) =>
                {
                    inputs.Enqueue((input.Clone(), id));
                    return Task.FromResult(new HookOutput
                    {
                        HookSpecificOutput = JsonSerializer.SerializeToElement(new PreToolUseHookSpecificOutput
                        {
                            PermissionDecision = "deny",
                            PermissionDecisionReason = "blocked in replay",
                        }, OmitNulls),
                    });
                }])],
            },
        };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var messages = await Drain(client.ReceiveResponseAsync(Timeout));

        // initialize registered the hook exactly as the CLI expects.
        var hooks = transport.RequestsOf("initialize").Single().GetProperty("request").GetProperty("hooks");
        Assert.Equal("Write", hooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        Assert.Equal("hook_0", hooks.GetProperty("PreToolUse")[0].GetProperty("hookCallbackIds")[0].GetString());

        // The callback saw the CLI's real hook input, typed fields included.
        var request = ControlRequest(fixture, "hook_callback");
        var (input, toolUseId) = Assert.Single(inputs);
        var typed = input.Deserialize<PreToolUseHookInput>()!;
        Assert.Equal("Write", typed.ToolName);
        Assert.Equal("{CWD}/blocked-1.txt", typed.ToolInput.GetProperty("file_path").GetString());
        Assert.Equal("acceptEdits", typed.PermissionMode);
        Assert.Equal(request.GetProperty("request").GetProperty("tool_use_id").GetString(), toolUseId);
        Assert.Equal(typed.ToolUseId, toolUseId);

        // The SDK answered that request with the deny decision.
        var output = transport.ResponseTo(Rid(request)).GetProperty("response").GetProperty("response")
            .GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("blocked in replay", output.GetProperty("permissionDecisionReason").GetString());
        Assert.IsType<ResultMessage>(messages[^1]);
    }

    [Fact]
    public async Task Replay_FailsWhenTheSdkDoesNotAnswerLikeTheRecording()
    {
        // Meta-test for the harness: without the hook the recording relied on, the SDK
        // answers the hook_callback with an error instead of success, and replay must
        // surface that as a failure instead of carrying on.
        var fixture = Fixture.Load("hook_pre_tool_use_deny");
        var transport = new ReplayTransport(fixture) { StepTimeout = TimeSpan.FromSeconds(1) };
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions(), transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);

        var ex = await Assert.ThrowsAsync<ReplayMismatchException>(() => Drain(client.ReceiveResponseAsync(Timeout)));
        Assert.Contains("control_response", ex.Message);
    }

    [Fact]
    public async Task HookPostToolUse_TypedInputCarriesToolResponse()
    {
        var fixture = Fixture.Load("hook_post_tool_use");
        var transport = new ReplayTransport(fixture);
        var post = new ConcurrentQueue<JsonElement>();
        var options = new ClaudeAgentOptions
        {
            // Same registration order as the recording: PreToolUse => hook_0, PostToolUse => hook_1.
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher(null, [(_, _, _, _) => Task.FromResult(new HookOutput())])],
                [HookEvent.PostToolUse] = [new HookMatcher("Write", [(input, _, _, _) =>
                {
                    post.Enqueue(input.Clone());
                    return Task.FromResult(new HookOutput { Continue = true });
                }])],
            },
        };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        await Drain(client.ReceiveResponseAsync(Timeout));

        var typed = Assert.Single(post).Deserialize<PostToolUseHookInput>()!;
        Assert.Equal("Write", typed.ToolName);
        Assert.Equal("create", typed.ToolResponse.GetProperty("type").GetString());
        Assert.Equal(typed.ToolInput.GetProperty("file_path").GetString(), typed.ToolResponse.GetProperty("filePath").GetString());
        Assert.Equal(typed.ToolInput.GetProperty("content").GetString(), typed.ToolResponse.GetProperty("content").GetString());

        // Empty HookOutput => {} and Continue=true => {"continue": true} on the wire.
        var requests = ControlRequests(fixture, "hook_callback").ToList();
        Assert.Equal("{}", transport.ResponseTo(Rid(requests[0])).GetProperty("response").GetProperty("response").GetRawText());
        Assert.True(transport.ResponseTo(Rid(requests[1])).GetProperty("response").GetProperty("response")
            .GetProperty("continue").GetBoolean());
    }

    [Fact]
    public async Task CanUseTool_AllowWithUpdatedInput_ContextParsed_AndUpdatedInputSent()
    {
        var fixture = Fixture.Load("permission_allow_updated_input");
        var transport = new ReplayTransport(fixture);
        ToolPermissionContext? seen = null;
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (tool, input, ctx, _) =>
            {
                seen = ctx;
                var updated = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.GetRawText())!;
                updated["file_path"] = JsonSerializer.SerializeToElement("{CWD}/redirected-1.txt");
                return Task.FromResult<PermissionResult>(new PermissionResultAllow(JsonSerializer.SerializeToElement(updated)));
            },
        };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var messages = await Drain(client.ReceiveResponseAsync(Timeout));

        var request = ControlRequest(fixture, "can_use_tool");
        Assert.NotNull(seen);
        Assert.Equal(request.GetProperty("request").GetProperty("tool_use_id").GetString(), seen!.ToolUseId);
        Assert.Equal("Write", seen.DisplayName);
        Assert.Equal("requested-1.txt", seen.Description);
        // permission_suggestions: [{"type":"setMode","mode":"acceptEdits","destination":"session"}]
        var suggestion = Assert.Single(seen.Suggestions!);
        Assert.Equal(PermissionUpdateType.SetMode, suggestion.Type);
        Assert.Equal(PermissionMode.AcceptEdits, suggestion.Mode);
        Assert.Equal(PermissionUpdateDestination.Session, suggestion.Destination);

        var response = transport.ResponseTo(Rid(request)).GetProperty("response").GetProperty("response");
        Assert.Equal("allow", response.GetProperty("behavior").GetString());
        Assert.Equal("{CWD}/redirected-1.txt", response.GetProperty("updatedInput").GetProperty("file_path").GetString());
        Assert.Equal("moved", response.GetProperty("updatedInput").GetProperty("content").GetString());

        // The recorded CLI then wrote the redirected file.
        Assert.Contains(RawToolResults(messages), r => r.GetProperty("content").GetString()!.Contains("redirected-1.txt"));
    }

    [Fact]
    public async Task CanUseTool_Deny_SendsDenyWithMessage()
    {
        var fixture = Fixture.Load("permission_deny");
        var transport = new ReplayTransport(fixture);
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultDeny("nope")),
        };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var messages = await Drain(client.ReceiveResponseAsync(Timeout));

        var response = transport.ResponseTo(Rid(ControlRequest(fixture, "can_use_tool")))
            .GetProperty("response").GetProperty("response");
        Assert.Equal("deny", response.GetProperty("behavior").GetString());
        Assert.Equal("nope", response.GetProperty("message").GetString());
        Assert.False(response.TryGetProperty("interrupt", out _));
        Assert.Contains(RawToolResults(messages), r => r.GetProperty("is_error").GetBoolean());
    }

    private static McpServerRegistry CalcServer(ConcurrentQueue<(int, int)> adds, string failure) =>
        McpServers.Sdk("calc", s => s
            .Tool("add", (int a, int b) =>
            {
                adds.Enqueue((a, b));
                return (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }, "Add two integers and return the sum.")
            .Tool("explode", (string label) =>
            {
                throw new InvalidOperationException(failure);
#pragma warning disable CS0162
                return "";
#pragma warning restore CS0162
            }, "A tool that always fails. Call it only when asked to."));

    [Fact]
    public async Task SdkMcpServer_HandshakeListAndCall_AnswerRealCliRequests()
    {
        var fixture = Fixture.Load("mcp_tool_call");
        var transport = new ReplayTransport(fixture);
        var adds = new ConcurrentQueue<(int, int)>();
        var options = new ClaudeAgentOptions { McpServers = CalcServer(adds, "unused") };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var messages = await Drain(client.ReceiveResponseAsync(Timeout));

        var mcp = ControlRequests(fixture, "mcp_message").ToDictionary(
            r => r.GetProperty("request").GetProperty("message").GetProperty("method").GetString()!,
            r => transport.ResponseTo(Rid(r)).GetProperty("response").GetProperty("response").GetProperty("mcp_response"));

        Assert.Equal("calc", mcp["initialize"].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        var tools = mcp["tools/list"].GetProperty("result").GetProperty("tools");
        var add = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "add");
        Assert.Equal("integer", add.GetProperty("inputSchema").GetProperty("properties").GetProperty("a").GetProperty("type").GetString());

        // tools/call {"name":"add","arguments":{"a":1729,"b":2718}} reached the delegate by name.
        Assert.Equal((1729, 2718), Assert.Single(adds));
        var call = mcp["tools/call"].GetProperty("result");
        Assert.False(call.GetProperty("isError").GetBoolean());
        Assert.Equal("4447", call.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(2, mcp["tools/call"].GetProperty("id").GetInt32());
        Assert.Equal("4447", Assert.IsType<ResultMessage>(messages[^1]).Result!.Trim());
    }

    [Fact]
    public async Task SdkMcpServer_ToolException_IsReturnedAsIsErrorResult()
    {
        var fixture = Fixture.Load("mcp_tool_error");
        var transport = new ReplayTransport(fixture);
        var options = new ClaudeAgentOptions { McpServers = CalcServer(new(), "kaboom-in-replay") };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        await Drain(client.ReceiveResponseAsync(Timeout));

        var callRequest = ControlRequests(fixture, "mcp_message").Single(r =>
            r.GetProperty("request").GetProperty("message").GetProperty("method").GetString() == "tools/call");
        var result = transport.ResponseTo(Rid(callRequest)).GetProperty("response").GetProperty("response")
            .GetProperty("mcp_response").GetProperty("result");
        // A tool failure is an MCP result with isError, not a JSON-RPC protocol error.
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("kaboom-in-replay", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetMcpStatus_ReturnsRecordedServerStatus()
    {
        var fixture = Fixture.Load("mcp_status");
        var options = new ClaudeAgentOptions { McpServers = CalcServer(new(), "unused") };
        await using var client = new ClaudeSDKClient(options, new ReplayTransport(fixture));
        await client.ConnectAsync(cancellationToken: Timeout);

        var status = await client.GetMcpStatusAsync(Timeout);

        var server = Assert.Single(status.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("connected", server.GetProperty("status").GetString());
        Assert.Equal("sdk", server.GetProperty("source").GetString());
    }

    [Fact(Skip = "SDK bug: ListMcpServersAsync deserializes mcp_status into McpStatusResponse, but " +
                 "McpServerConnectionStatus has no JSON string mapping, so the CLI's \"status\":\"connected\" throws " +
                 "JsonException (Path: $.mcpServers[0].status).")]
    public async Task ListMcpServers_ParsesRecordedServerStatus()
    {
        var fixture = Fixture.Load("mcp_status");
        var options = new ClaudeAgentOptions { McpServers = CalcServer(new(), "unused") };
        await using var client = new ClaudeSDKClient(options, new ReplayTransport(fixture));
        await client.ConnectAsync(cancellationToken: Timeout);

        var status = await client.ListMcpServersAsync(Timeout);

        var server = Assert.Single(status.McpServers);
        Assert.Equal(McpServerConnectionStatus.Connected, server.Status);
        Assert.Equal(["add", "explode"], server.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task GetContextUsage_DeserializesRealResponse()
    {
        var fixture = Fixture.Load("client_context_usage");
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions(), new ReplayTransport(fixture));
        await client.ConnectAsync(cancellationToken: Timeout);

        var usage = await client.GetContextUsageAsync(Timeout);

        Assert.Equal("claude-haiku-4-5-20251001", usage.Model);
        Assert.Equal(200000, usage.MaxTokens);
        Assert.Contains(usage.Categories, c => c.Name == "Free space");
        Assert.Equal(usage.MaxTokens, usage.Categories.Sum(c => c.Tokens));
        Assert.NotEmpty(usage.GridRows);
    }

    [Fact]
    public async Task SetModel_SendsSetModelRequest_AndNextTurnUsesIt()
    {
        var fixture = Fixture.Load("client_set_model");
        var transport = new ReplayTransport(fixture);
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions(), transport);
        await client.ConnectAsync(cancellationToken: Timeout);

        await client.SetModelAsync("claude-haiku-4-5-20251001", Timeout);
        await client.QueryAsync("Reply with just OK.", cancellationToken: Timeout);
        var messages = await Drain(client.ReceiveResponseAsync(Timeout));

        Assert.Equal("claude-haiku-4-5-20251001",
            transport.RequestsOf("set_model").Single().GetProperty("request").GetProperty("model").GetString());
        Assert.All(messages.OfType<AssistantMessage>(), a => Assert.Equal("claude-haiku-4-5-20251001", a.Model));
    }

    [Fact]
    public async Task SetPermissionMode_BetweenTurns_ChangesToolOutcome()
    {
        var fixture = Fixture.Load("client_set_permission_mode");
        var transport = new ReplayTransport(fixture);
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions(), transport);
        await client.ConnectAsync(cancellationToken: Timeout);

        var prompts = fixture.Outgoing.Where(m => m.GetProperty("type").GetString() == "user")
            .Select(m => m.GetProperty("message").GetProperty("content").GetString()!).ToList();
        await client.QueryAsync(prompts[0], cancellationToken: Timeout);
        var first = await Drain(client.ReceiveResponseAsync(Timeout));
        await client.SetPermissionModeAsync("acceptEdits", Timeout);
        await client.QueryAsync(prompts[1], cancellationToken: Timeout);
        var second = await Drain(client.ReceiveResponseAsync(Timeout));

        Assert.Equal("acceptEdits",
            transport.RequestsOf("set_permission_mode").Single().GetProperty("request").GetProperty("mode").GetString());
        // Default mode without a callback: the CLI refused the Write (and said so as a system message).
        Assert.Contains(first.OfType<SystemMessage>(), s => s.Subtype == "permission_denied");
        Assert.Contains(RawToolResults(first), r => r.GetProperty("is_error").GetBoolean());
        Assert.Contains(RawToolResults(second), r => r.GetProperty("content").GetString()!.StartsWith("File created successfully"));
    }

    [Fact]
    public async Task StructuredOutput_ResultCarriesSchemaObject()
    {
        var fixture = Fixture.Load("structured_output");
        var messages = await Drain(Claude.QueryAsync("q", new ClaudeAgentOptions(), new ReplayTransport(fixture)));

        var result = Assert.IsType<ResultMessage>(messages[^1]);
        Assert.Equal("success", result.Subtype);
        var so = result.StructuredOutput!.Value;
        Assert.Equal("Paris", so.GetProperty("capital").GetString());
        Assert.Equal(JsonValueKind.Number, so.GetProperty("population_millions").ValueKind);
        // The CLI obtains it through its StructuredOutput tool.
        Assert.Contains(messages.OfType<AssistantMessage>().SelectMany(a => a.Content).OfType<ToolUseBlock>(),
            t => t.Name == "StructuredOutput");
    }

    [Fact]
    public async Task MaxTurns_ErrorResultIsYielded_ThenExitSurfacesTheResultText()
    {
        var fixture = Fixture.Load("max_turns_error");
        var messages = new List<Message>();

        var ex = await Assert.ThrowsAnyAsync<ProcessException>(async () =>
        {
            await foreach (var m in Claude.QueryAsync("q", new ClaudeAgentOptions(), new ReplayTransport(fixture))
                               .WithCancellation(Timeout))
                messages.Add(m);
        });

        var result = Assert.IsType<ResultMessage>(messages[^1]);
        Assert.Equal("error_max_turns", result.Subtype);
        Assert.True(result.IsError);
        Assert.NotEmpty(result.Errors!);
        // The generic "Command failed with exit code 1" is replaced by the CLI's own reason.
        Assert.StartsWith("Claude Code returned an error result: ", ex.Message);
        Assert.Contains(result.Errors![0], ex.Message);
        Assert.Equal(1, ex.ExitCode);
    }

    [Fact]
    public async Task PartialMessages_TextDeltasReassembleToFinalText()
    {
        var fixture = Fixture.Load("partial_messages");
        var messages = await Drain(Claude.QueryAsync("q", new ClaudeAgentOptions(), new ReplayTransport(fixture)));

        var events = messages.OfType<StreamEvent>().ToList();
        Assert.NotEmpty(events);
        var text = new StringBuilder();
        foreach (var e in events.Where(e => e.Event.GetProperty("type").GetString() == "content_block_delta"))
        {
            var delta = e.Event.GetProperty("delta");
            if (delta.GetProperty("type").GetString() == "text_delta")
                text.Append(delta.GetProperty("text").GetString());
        }
        var final = string.Concat(messages.OfType<AssistantMessage>().SelectMany(a => a.Content).OfType<TextBlock>().Select(t => t.Text));
        Assert.Equal(final, text.ToString());
        Assert.Equal("message_start", events[0].Event.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Subagent_AgentsSentInInitialize_TaskLifecycleLinkedToToolUse()
    {
        var fixture = Fixture.Load("subagent");
        var transport = new ReplayTransport(fixture);
        var options = new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["nonce-reporter"] = new("Reports the code.", "Reply with the code.", Tools: [], Model: "haiku", MaxTurns: 1),
            },
        };

        await using var client = new ClaudeSDKClient(options, transport);
        await client.ConnectAsync(cancellationToken: Timeout);
        await client.QueryAsync(fixture.FirstPrompt, cancellationToken: Timeout);
        var messages = await Drain(client.ReceiveResponseAsync(Timeout));

        var agents = transport.RequestsOf("initialize").Single().GetProperty("request").GetProperty("agents");
        Assert.Equal("haiku", agents.GetProperty("nonce-reporter").GetProperty("model").GetString());
        Assert.Equal(1, agents.GetProperty("nonce-reporter").GetProperty("maxTurns").GetInt32());

        var delegation = messages.OfType<AssistantMessage>().SelectMany(a => a.Content).OfType<ToolUseBlock>()
            .Single(t => t.Name is "Agent" or "Task");
        Assert.Equal("nonce-reporter", delegation.Input.GetProperty("subagent_type").GetString());

        var started = Assert.Single(messages.OfType<TaskStartedMessage>());
        Assert.Equal(delegation.Id, started.ToolUseId);
        Assert.Equal("local_agent", started.TaskType);
        var done = Assert.Single(messages.OfType<TaskNotificationMessage>());
        Assert.Equal(started.TaskId, done.TaskId);
        Assert.Equal(TaskNotificationStatus.Completed, done.Status);
        // The subagent's own transcript messages are tagged with the parent tool_use id.
        Assert.Contains(messages.OfType<UserMessage>(), u => u.ParentToolUseId == delegation.Id);
        Assert.Contains(done.Summary, Assert.IsType<ResultMessage>(messages[^1]).Result);
    }
}
