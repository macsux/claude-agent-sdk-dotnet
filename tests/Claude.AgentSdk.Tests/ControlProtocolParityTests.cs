// Python parity for the control protocol (claude-agent-sdk-python/_internal/query.py).

using System.Text.Json;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Sessions;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class ControlProtocolParityTests
{
    private static async Task<QueryHandler> StartAsync(FakeTransport transport, ClaudeAgentOptions options)
    {
        var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();
        return handler;
    }

    private static JsonElement LastRequest(FakeTransport t, string subtype) => t.Written
        .Where(w => w.GetProperty("type").GetString() == "control_request" &&
                    w.GetProperty("request").GetProperty("subtype").GetString() == subtype)
        .Select(w => w.GetProperty("request"))
        .Last();

    private static object UserMessage() => new
    {
        type = "user",
        message = new { role = "user", content = "hi" },
        parent_tool_use_id = (string?)null,
        session_id = ""
    };

    private static object Result() => new
    {
        type = "result",
        subtype = "success",
        duration_ms = 1,
        duration_api_ms = 1,
        is_error = false,
        num_turns = 1,
        session_id = "s"
    };

    [Fact]
    public async Task ControlRequests_ReturnInnerResponsePayload()
    {
        var transport = new FakeTransport
        {
            ControlResponder = req => req.GetProperty("subtype").GetString() == "mcp_status"
                ? new { mcpServers = new[] { new { name = "srv", status = "connected" } } }
                : new { commands = Array.Empty<string>() }
        };
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());

        var status = await handler.GetMcpStatusAsync();
        Assert.True(status.TryGetProperty("mcpServers", out _));
        Assert.False(status.TryGetProperty("request_id", out _));

        var init = handler.GetInitializationResult();
        Assert.True(init!.Value.TryGetProperty("commands", out _));
    }

    [Fact]
    public async Task Initialize_SendsAgentsAndSkillsLikePython()
    {
        var transport = new FakeTransport();
        var options = new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["reviewer"] = new("Reviews code", "You review code.",
                    PermissionMode: PermissionMode.AcceptEdits, Effort: EffortLevel.High)
            },
            Skills = new List<string> { "pdf" },
            SystemPrompt = SystemPromptPreset.ClaudeCode() with { ExcludeDynamicSections = true }
        };
        await using var handler = await StartAsync(transport, options);

        var init = LastRequest(transport, "initialize");
        var agent = init.GetProperty("agents").GetProperty("reviewer");
        Assert.Equal("acceptEdits", agent.GetProperty("permissionMode").GetString());
        Assert.Equal("high", agent.GetProperty("effort").GetString());
        Assert.False(agent.TryGetProperty("tools", out _)); // None values dropped
        Assert.Equal("pdf", init.GetProperty("skills")[0].GetString());
        Assert.True(init.GetProperty("excludeDynamicSections").GetBoolean());
    }

    [Fact]
    public async Task CancelledControlRequest_WritesNoResponse()
    {
        var entered = new TaskCompletionSource();
        var options = new ClaudeAgentOptions
        {
            CanUseTool = async (_, _, _, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new PermissionResultAllow();
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        transport.Send(new
        {
            type = "control_request",
            request_id = "cli_1",
            request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { command = "ls" } }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Send(new { type = "control_cancel_request", request_id = "cli_1" });

        await Task.Delay(200);
        Assert.Empty(transport.ResponsesFor("cli_1"));
    }

    [Fact]
    public async Task CanUseTool_ReceivesFullPermissionContext()
    {
        ToolPermissionContext? seen = null;
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, ctx, _) =>
            {
                seen = ctx;
                return Task.FromResult<PermissionResult>(new PermissionResultDeny("no"));
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        transport.Send(new
        {
            type = "control_request",
            request_id = "cli_2",
            request = new
            {
                subtype = "can_use_tool",
                tool_name = "Read",
                input = new { file_path = "/etc/passwd" },
                tool_use_id = "toolu_1",
                agent_id = "agent_1",
                blocked_path = "/etc/passwd",
                decision_reason = "outside cwd",
                title = "Claude wants to read /etc/passwd",
                display_name = "Read file",
                description = "desc",
                permission_suggestions = new object[]
                {
                    new
                    {
                        type = "addRules",
                        rules = new[] { new { toolName = "Read", ruleContent = "/etc/**" } },
                        behavior = "allow",
                        destination = "session"
                    },
                    new { type = "someFutureUpdate" }
                }
            }
        });
        await transport.WaitForAsync(t => t.ResponsesFor("cli_2").Count > 0);

        Assert.NotNull(seen);
        Assert.Equal("toolu_1", seen!.ToolUseId);
        Assert.Equal("agent_1", seen.AgentId);
        Assert.Equal("/etc/passwd", seen.BlockedPath);
        Assert.Equal("outside cwd", seen.DecisionReason);
        Assert.Equal("Claude wants to read /etc/passwd", seen.Title);
        Assert.Equal("Read file", seen.DisplayName);
        Assert.Equal("desc", seen.Description);

        // Wire-format suggestions parse (camelCase enums, toolName/ruleContent);
        // unknown update types are skipped instead of failing the request.
        var suggestion = Assert.Single(seen.Suggestions!);
        Assert.Equal(PermissionUpdateType.AddRules, suggestion.Type);
        Assert.Equal(PermissionBehavior.Allow, suggestion.Behavior);
        Assert.Equal(PermissionUpdateDestination.Session, suggestion.Destination);
        Assert.Equal("Read", suggestion.Rules![0].ToolName);
        Assert.Equal("/etc/**", suggestion.Rules[0].RuleContent);

        var response = transport.ResponsesFor("cli_2").Single().GetProperty("response");
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal("deny", response.GetProperty("response").GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task StreamInput_KeepsStdinOpenWhileAgentTaskInFlight()
    {
        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher(Hooks: [(_, _, _, _) => Task.FromResult(new HookOutput())])]
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        async IAsyncEnumerable<Dictionary<string, object?>> OneMessage()
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["type"] = "user", ["message"] = new { role = "user", content = "hi" } };
        }
        var input = handler.StreamInputAsync(OneMessage());

        transport.Send(new { type = "system", subtype = "task_started", task_id = "t1", task_type = "local_agent", description = "d", uuid = "u", session_id = "s" });
        transport.Send(Result());
        await Task.Delay(200);
        Assert.False(transport.InputEnded.Task.IsCompleted, "stdin closed while a background agent was still running");

        transport.Send(new { type = "system", subtype = "task_updated", task_id = "t1", patch = new { status = "completed" }, uuid = "u", session_id = "s" });
        transport.Send(Result());
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Fact]
    public async Task StreamInput_CanUseToolCountsAsBidirectionalNeed()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow())
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        async IAsyncEnumerable<Dictionary<string, object?>> OneMessage()
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["type"] = "user" };
        }
        var input = handler.StreamInputAsync(OneMessage());
        await Task.Delay(200);
        Assert.False(transport.InputEnded.Task.IsCompleted);

        transport.Send(Result());
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Fact]
    public async Task TranscriptMirrorFrames_ReachTheSessionStore()
    {
        var configDir = Directory.CreateTempSubdirectory("sdk-mirror").FullName;
        try
        {
            var store = new InMemorySessionStore();
            var options = new ClaudeAgentOptions
            {
                SessionStore = store,
                Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configDir }
            };
            var transport = new FakeTransport();
            await using var handler = new QueryHandler(transport, options);
            SessionStoreSupport.AttachMirrorBatcher(handler, options, materialized: null);
            await handler.StartAsync();
            await handler.InitializeAsync();

            var sessionId = "0b9f7c4e-1f0a-4f5e-9d3c-2a1b3c4d5e6f";
            var filePath = Path.Combine(configDir, "projects", "my-project", sessionId + ".jsonl");
            transport.Send(new
            {
                type = "transcript_mirror",
                filePath,
                entries = new[] { new { type = "user", uuid = "e1" } }
            });
            transport.Send(Result());

            await foreach (var msg in handler.ReceiveMessagesAsync())
            {
                if (msg is ResultMessage) break;
            }

            var loaded = await store.LoadAsync(new SessionKey { ProjectKey = "my-project", SessionId = sessionId });
            Assert.NotNull(loaded);
            Assert.Equal("e1", Assert.Single(loaded!).Uuid);
        }
        finally
        {
            Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public async Task QueryAsync_StringPromptIsWrittenToStdin()
    {
        var transport = new FakeTransport();
        var messages = Claude.QueryAsync("what is 2+2", new ClaudeAgentOptions(), transport);
        var enumerator = messages.GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();

        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));
        var user = transport.Written.First(w => w.GetProperty("type").GetString() == "user");
        Assert.Equal("what is 2+2", user.GetProperty("message").GetProperty("content").GetString());

        transport.Send(Result());
        Assert.True(await moveNext);
        Assert.IsType<ResultMessage>(enumerator.Current);
        transport.Complete();
        await enumerator.DisposeAsync();
    }
}

public sealed class MessageParserForwardCompatTests
{
    [Fact]
    public void UnknownContentBlocksAreSkipped()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            type = "assistant",
            message = new
            {
                model = "m",
                content = new object[]
                {
                    new { type = "some_future_block", data = 1 },
                    new { type = "text", text = "hello" }
                }
            }
        });

        var msg = Assert.IsType<AssistantMessage>(MessageParser.Parse(json));
        Assert.IsType<TextBlock>(Assert.Single(msg.Content));
    }

    [Theory]
    [InlineData("killed", TaskNotificationStatus.Killed)]
    [InlineData("brand_new_status", TaskNotificationStatus.Unknown)]
    public void TaskNotificationStatusNeverThrows(string status, TaskNotificationStatus expected)
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            type = "system",
            subtype = "task_notification",
            task_id = "t",
            status,
            output_file = "f",
            summary = "s",
            uuid = "u",
            session_id = "s"
        });

        var msg = Assert.IsType<TaskNotificationMessage>(MessageParser.Parse(json));
        Assert.Equal(expected, msg.Status);
    }

    private static async Task<QueryHandler> StartHandlerAsync(FakeTransport transport, ClaudeAgentOptions options)
    {
        var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();
        return handler;
    }

    private static async Task<JsonElement> SendMcpMessageAsync(FakeTransport transport, string requestId, object request)
    {
        transport.Send(new { type = "control_request", request_id = requestId, request });
        await transport.WaitForAsync(t => t.Written.Any(w =>
            w.GetProperty("type").GetString() == "control_response" &&
            w.GetProperty("response").GetProperty("request_id").GetString() == requestId));
        return transport.Written.Single(w =>
            w.GetProperty("type").GetString() == "control_response" &&
            w.GetProperty("response").GetProperty("request_id").GetString() == requestId).GetProperty("response");
    }

    [Fact]
    public async Task McpMessage_UnknownServer_UsesPythonErrorText()
    {
        var transport = new FakeTransport();
        await using var handler = await StartHandlerAsync(transport, new ClaudeAgentOptions());

        var response = await SendMcpMessageAsync(transport, "mcp-1", new
        {
            subtype = "mcp_message",
            server_name = "nope",
            message = new { jsonrpc = "2.0", id = 7, method = "tools/list" }
        });

        Assert.Equal("success", response.GetProperty("subtype").GetString());
        var mcp = response.GetProperty("response").GetProperty("mcp_response");
        Assert.Equal(7, mcp.GetProperty("id").GetInt32());
        Assert.Equal(-32601, mcp.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Server 'nope' not found", mcp.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task McpMessage_Notification_IsAckedWithEmptyResult()
    {
        var transport = new FakeTransport();
        await using var handler = await StartHandlerAsync(transport, new ClaudeAgentOptions());
        handler.RegisterSdkMcpBridge("srv", new Mcp.SdkMcpBridge(new Mcp.McpServerHandlers(), "srv"));

        var response = await SendMcpMessageAsync(transport, "mcp-2", new
        {
            subtype = "mcp_message",
            server_name = "srv",
            message = new { jsonrpc = "2.0", method = "notifications/initialized" }
        });

        Assert.Equal(
            """{"jsonrpc":"2.0","result":{}}""",
            response.GetProperty("response").GetProperty("mcp_response").GetRawText());
    }

    [Fact]
    public async Task McpMessage_MissingMessage_IsAControlError()
    {
        var transport = new FakeTransport();
        await using var handler = await StartHandlerAsync(transport, new ClaudeAgentOptions());

        var response = await SendMcpMessageAsync(transport, "mcp-3", new { subtype = "mcp_message", server_name = "srv" });

        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.Equal("Missing server_name or message for MCP request", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SdkMcpBridge_HandleAsync_ReturnsNullForNotifications()
    {
        var bridge = new Mcp.SdkMcpBridge(new Mcp.McpServerHandlers(), "srv");
        var reply = await bridge.HandleAsync(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        Assert.Null(reply);
    }
}
