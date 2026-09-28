// TypeScript SDK (0.3.283) parity for the control protocol: inbound handlers
// (elicitation, user dialogs, auth refresh, silent subtypes, dedupe, prompt
// redelivery), outbound cancellation, exact wire shapes of the TS-only Query
// methods, typed initialize helpers, the raw frame tap, and the Python 0.2.160
// session-state handshake.

#pragma warning disable CLAUDESDK001, CLAUDESDK002

using System.Text.Json;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Mcp;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class TsControlParityTests
{
    private static async Task<QueryHandler> StartAsync(FakeTransport transport, ClaudeAgentOptions options)
    {
        var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();
        return handler;
    }

    private static async Task<ClaudeSDKClient> ConnectAsync(FakeTransport transport, ClaudeAgentOptions? options = null)
    {
        var client = new ClaudeSDKClient(options ?? new ClaudeAgentOptions(), transport);
        await client.ConnectAsync();
        return client;
    }

    private static string Raw(JsonElement e) => e.GetRawText();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void SendRequest(FakeTransport t, string id, object request) =>
        t.Send(new { type = "control_request", request_id = id, request });

    private static JsonElement InnerResponse(FakeTransport t, string id) =>
        Assert.Single(t.ResponsesFor(id)).GetProperty("response").GetProperty("response");

    #region Inbound: elicitation

    [Fact]
    public async Task Elicitation_WithoutHandler_Declines()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());

        SendRequest(transport, "e1", new { subtype = "elicitation", mcp_server_name = "srv", message = "Need input" });
        await transport.WaitForAsync(t => t.ResponsesFor("e1").Count > 0);

        var envelope = transport.ResponsesFor("e1")[0].GetProperty("response");
        Assert.Equal("success", envelope.GetProperty("subtype").GetString());
        Assert.Equal("""{"action":"decline"}""", Raw(envelope.GetProperty("response")));
    }

    [Fact]
    public async Task Elicitation_MapsRequestAndReturnsResult()
    {
        ElicitationRequest? seen = null;
        ControlRequestContext? seenCtx = null;
        var options = new ClaudeAgentOptions
        {
            OnElicitation = (req, ctx, _) =>
            {
                seen = req;
                seenCtx = ctx;
                return Task.FromResult<ElicitationResult?>(
                    new ElicitationResult(ElicitationAction.Accept, Json("""{"name":"x"}""")));
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "e2", new
        {
            subtype = "elicitation",
            mcp_server_name = "srv",
            message = "Who?",
            mode = "form",
            url = "https://u",
            elicitation_id = "el1",
            requested_schema = new { type = "object" },
            title = "T",
            display_name = "D",
            description = "Desc"
        });
        await transport.WaitForAsync(t => t.ResponsesFor("e2").Count > 0);

        Assert.Equal("""{"action":"accept","content":{"name":"x"}}""", Raw(InnerResponse(transport, "e2")));
        Assert.Equal("srv", seen!.ServerName);
        Assert.Equal("Who?", seen.Message);
        Assert.Equal("form", seen.Mode);
        Assert.Equal("https://u", seen.Url);
        Assert.Equal("el1", seen.ElicitationId);
        Assert.Equal("object", seen.RequestedSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(("T", "D", "Desc"), (seen.Title, seen.DisplayName, seen.Description));
        Assert.Equal("e2", seenCtx!.RequestId);
    }

    [Fact]
    public async Task Elicitation_NullResultWritesNothing()
    {
        var called = new TaskCompletionSource();
        var options = new ClaudeAgentOptions
        {
            OnElicitation = (_, _, _) =>
            {
                called.TrySetResult();
                return Task.FromResult<ElicitationResult?>(null);
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "e3", new { subtype = "elicitation", mcp_server_name = "s", message = "m" });
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Empty(transport.ResponsesFor("e3"));
    }

    [Theory]
    [InlineData(ElicitationAction.Decline, """{"action":"decline"}""")]
    [InlineData(ElicitationAction.Cancel, """{"action":"cancel"}""")]
    public async Task Elicitation_DeclineAndCancelHaveNoContent(ElicitationAction action, string expected)
    {
        var options = new ClaudeAgentOptions
        {
            OnElicitation = (_, _, _) => Task.FromResult<ElicitationResult?>(new ElicitationResult(action))
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "e4", new { subtype = "elicitation", mcp_server_name = "s", message = "m" });
        await transport.WaitForAsync(t => t.ResponsesFor("e4").Count > 0);
        Assert.Equal(expected, Raw(InnerResponse(transport, "e4")));
    }

    #endregion

    #region Inbound: silent subtypes, user dialogs, auth refresh

    [Theory]
    [InlineData("remote_tool_call")]
    [InlineData("remote_plumbing_call")]
    [InlineData("remote_tools_probe")]
    [InlineData("remote_tools_reannounce")]
    public async Task RemoteSubtypes_AreLeftUnanswered(string subtype)
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());

        SendRequest(transport, "silent", new { subtype });
        // A later request is answered, so the silent one was processed first.
        SendRequest(transport, "after", new { subtype = "elicitation", mcp_server_name = "s", message = "m" });
        await transport.WaitForAsync(t => t.ResponsesFor("after").Count > 0);
        await Task.Delay(50);

        Assert.Empty(transport.ResponsesFor("silent"));
    }

    [Fact]
    public async Task UnknownSubtype_StillRepliesWithError()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());

        SendRequest(transport, "u1", new { subtype = "no_such_thing" });
        await transport.WaitForAsync(t => t.ResponsesFor("u1").Count > 0);
        var envelope = transport.ResponsesFor("u1")[0].GetProperty("response");
        Assert.Equal("error", envelope.GetProperty("subtype").GetString());
        Assert.Contains("no_such_thing", envelope.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UserDialog_WithoutHandler_StaysSilent()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());

        SendRequest(transport, "d1", new { subtype = "request_user_dialog", dialog_kind = "k", payload = new { } });
        SendRequest(transport, "after", new { subtype = "elicitation", mcp_server_name = "s", message = "m" });
        await transport.WaitForAsync(t => t.ResponsesFor("after").Count > 0);
        await Task.Delay(50);

        Assert.Empty(transport.ResponsesFor("d1"));
    }

    [Fact]
    public async Task UserDialog_CompletedAndCancelled()
    {
        UserDialogRequest? seen = null;
        var options = new ClaudeAgentOptions
        {
            OnUserDialog = (req, _, _) =>
            {
                seen = req;
                return Task.FromResult<UserDialogResult?>(req.DialogKind == "known"
                    ? UserDialogResult.Complete(Json("""{"choice":2}"""))
                    : UserDialogResult.Cancel());
            },
            SupportedDialogKinds = ["known"]
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "d2", new { subtype = "request_user_dialog", dialog_kind = "known", payload = new { q = 1 }, tool_use_id = "tu9" });
        await transport.WaitForAsync(t => t.ResponsesFor("d2").Count > 0);
        Assert.Equal("""{"behavior":"completed","result":{"choice":2}}""", Raw(InnerResponse(transport, "d2")));
        Assert.Equal("known", seen!.DialogKind);
        Assert.Equal(1, seen.Payload.GetProperty("q").GetInt32());
        Assert.Equal("tu9", seen.ToolUseId);

        SendRequest(transport, "d3", new { subtype = "request_user_dialog", dialog_kind = "other", payload = new { } });
        await transport.WaitForAsync(t => t.ResponsesFor("d3").Count > 0);
        Assert.Equal("""{"behavior":"cancelled"}""", Raw(InnerResponse(transport, "d3")));
    }

    [Fact]
    public async Task OAuthTokenRefresh_WithoutCallback_RepliesError()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());

        SendRequest(transport, "o1", new { subtype = "oauth_token_refresh" });
        await transport.WaitForAsync(t => t.ResponsesFor("o1").Count > 0);
        var envelope = transport.ResponsesFor("o1")[0].GetProperty("response");
        Assert.Equal("error", envelope.GetProperty("subtype").GetString());
        Assert.Contains("GetOAuthToken", envelope.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("tok", null, """{"accessToken":"tok"}""")]
    [InlineData(null, "signed_out", """{"accessToken":null,"reason":"signed_out"}""")]
    [InlineData(null, "bogus", """{"accessToken":null}""")]
    [InlineData("tok", "signed_out", """{"accessToken":"tok"}""")]
    public async Task OAuthTokenRefresh_ShapesResponse(string? token, string? reason, string expected)
    {
        var options = new ClaudeAgentOptions
        {
            GetOAuthToken = _ => Task.FromResult<OAuthTokenResult?>(new OAuthTokenResult(token, reason))
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "o2", new { subtype = "oauth_token_refresh" });
        await transport.WaitForAsync(t => t.ResponsesFor("o2").Count > 0);
        Assert.Equal(expected, Raw(InnerResponse(transport, "o2")));
    }

    [Theory]
    [InlineData("h", """{"authToken":"h"}""")]
    [InlineData(null, """{"authToken":null}""")]
    public async Task HostAuthTokenRefresh_ShapesResponse(string? token, string expected)
    {
        var options = new ClaudeAgentOptions { GetHostAuthToken = _ => Task.FromResult(token) };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "h1", new { subtype = "host_auth_token_refresh" });
        await transport.WaitForAsync(t => t.ResponsesFor("h1").Count > 0);
        Assert.Equal(expected, Raw(InnerResponse(transport, "h1")));
    }

    #endregion

    #region Inbound: can_use_tool fidelity, dedupe, redelivery

    [Fact]
    public async Task CanUseTool_EchoesToolUseIdAndDecisionClassification()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (name, _, _, _) => Task.FromResult<PermissionResult>(name == "Read"
                ? new PermissionResultAllow(Json("""{"x":1}""")) { DecisionClassification = PermissionDecisionClassification.UserPermanent }
                : new PermissionResultDeny("no", Interrupt: true) { DecisionClassification = PermissionDecisionClassification.UserReject })
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "c1", new { subtype = "can_use_tool", tool_name = "Read", input = new { }, tool_use_id = "tu1" });
        SendRequest(transport, "c2", new { subtype = "can_use_tool", tool_name = "Bash", input = new { }, tool_use_id = "tu2" });
        await transport.WaitForAsync(t => t.ResponsesFor("c1").Count > 0 && t.ResponsesFor("c2").Count > 0);

        Assert.Equal(
            """{"behavior":"allow","updatedInput":{"x":1},"decisionClassification":"user_permanent","toolUseID":"tu1"}""",
            Raw(InnerResponse(transport, "c1")));
        Assert.Equal(
            """{"behavior":"deny","message":"no","interrupt":true,"decisionClassification":"user_reject","toolUseID":"tu2"}""",
            Raw(InnerResponse(transport, "c2")));
    }

    [Fact]
    public async Task CanUseTool_ResultToolUseIdUsedOnlyWhenRequestHasNone()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultDeny("no") { ToolUseId = "mine" })
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "c3", new { subtype = "can_use_tool", tool_name = "Bash", input = new { } });
        SendRequest(transport, "c4", new { subtype = "can_use_tool", tool_name = "Bash", input = new { }, tool_use_id = "req" });
        await transport.WaitForAsync(t => t.ResponsesFor("c3").Count > 0 && t.ResponsesFor("c4").Count > 0);

        Assert.Equal("mine", InnerResponse(transport, "c3").GetProperty("toolUseID").GetString());
        Assert.Equal("req", InnerResponse(transport, "c4").GetProperty("toolUseID").GetString());
    }

    [Fact]
    public async Task CanUseTool_NullResultWritesNothing()
    {
        var called = new TaskCompletionSource();
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _, _) =>
            {
                called.TrySetResult();
                return Task.FromResult<PermissionResult>(null!);
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "c5", new { subtype = "can_use_tool", tool_name = "Bash", input = new { } });
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Empty(transport.ResponsesFor("c5"));
    }

    [Fact]
    public async Task CanUseTool_ContextCarriesTsFields()
    {
        ToolPermissionContext? ctx = null;
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, c, _) =>
            {
                ctx = c;
                return Task.FromResult<PermissionResult>(new PermissionResultAllow());
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "c6", new
        {
            subtype = "can_use_tool",
            tool_name = "mcp__srv__x",
            input = new { },
            tool_use_id = "tu",
            mcp_server = new { name = "srv", source = "project" },
            default_to_no = true,
            suppress_always_allow_rule = false,
            matched_ask_rule = new { source = "userSettings", tool_name = "Bash", rule_content = "rm:*" },
            requires_user_interaction = true,
            decision_reason_type = "rule",
            classifier_approvable = false,
            server_prompt = "sp",
            computer_folder = new { path = "/x", computer_name = "box" }
        });
        await transport.WaitForAsync(t => t.ResponsesFor("c6").Count > 0);

        Assert.Equal("c6", ctx!.RequestId);
        Assert.Equal(new McpServerProvenance("srv", "project"), ctx.McpServer);
        Assert.True(ctx.DefaultToNo);
        Assert.False(ctx.SuppressAlwaysAllowRule);
        Assert.Equal(new MatchedAskRule("userSettings", "Bash", "rm:*"), ctx.MatchedAskRule);
        Assert.True(ctx.RequiresUserInteraction);
        Assert.Equal("rule", ctx.DecisionReasonType);
        Assert.False(ctx.ClassifierApprovable);
        Assert.Equal("sp", ctx.ServerPrompt);
        Assert.Equal(new ComputerFolder("/x", "box"), ctx.ComputerFolder);
    }

    [Fact]
    public async Task CanUseTool_AbsentTsFieldsAreNull()
    {
        ToolPermissionContext? ctx = null;
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, c, _) =>
            {
                ctx = c;
                return Task.FromResult<PermissionResult>(new PermissionResultAllow());
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "c7", new { subtype = "can_use_tool", tool_name = "Bash", input = new { }, computer_folder = new { path = "/x" } });
        await transport.WaitForAsync(t => t.ResponsesFor("c7").Count > 0);

        Assert.Null(ctx!.McpServer);
        Assert.Null(ctx.DefaultToNo);
        Assert.Null(ctx.MatchedAskRule);
        Assert.Null(ctx.ComputerFolder); // computer_name missing
        Assert.False(InnerResponse(transport, "c7").TryGetProperty("toolUseID", out _));
        Assert.False(InnerResponse(transport, "c7").TryGetProperty("decisionClassification", out _));
    }

    [Fact]
    public async Task DuplicateInFlightRequestId_IsSkipped()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var options = new ClaudeAgentOptions
        {
            CanUseTool = async (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return new PermissionResultAllow();
            }
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        SendRequest(transport, "dup", new { subtype = "can_use_tool", tool_name = "Bash", input = new { } });
        await transport.WaitForAsync(_ => Volatile.Read(ref calls) == 1);
        SendRequest(transport, "dup", new { subtype = "can_use_tool", tool_name = "Bash", input = new { } });
        await Task.Delay(100);
        release.SetResult();
        await transport.WaitForAsync(t => t.ResponsesFor("dup").Count > 0);
        await Task.Delay(50);

        Assert.Equal(1, calls);
        Assert.Single(transport.ResponsesFor("dup"));
    }

    [Fact]
    public async Task Reinitialize_RedeliversPendingPromptsDeduped()
    {
        var initCount = 0;
        var permissionCalls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var release = new TaskCompletionSource();
        var dialogs = new System.Collections.Concurrent.ConcurrentBag<string>();
        var options = new ClaudeAgentOptions
        {
            CanUseTool = async (_, _, ctx, _) =>
            {
                permissionCalls.Add(ctx.RequestId!);
                if (ctx.RequestId == "inflight")
                    await release.Task;
                return new PermissionResultAllow();
            },
            OnUserDialog = (_, ctx, _) =>
            {
                dialogs.Add(ctx.RequestId);
                return Task.FromResult<UserDialogResult?>(UserDialogResult.Cancel());
            },
            SupportedDialogKinds = ["k"]
        };
        var transport = new FakeTransport
        {
            EnvelopeExtras = body =>
            {
                if (body.GetProperty("subtype").GetString() != "initialize" || Interlocked.Increment(ref initCount) < 2)
                    return null;
                return new Dictionary<string, object?>
                {
                    ["pending_permission_requests"] = new object[]
                    {
                        new { type = "control_request", request_id = "p1", request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { } } },
                        new { type = "control_request", request_id = "inflight", request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { } } },
                        // Wrong subtype for this list: ignored.
                        new { type = "control_request", request_id = "x1", request = new { subtype = "hook_callback", callback_id = "nope" } }
                    },
                    ["pending_user_dialog_requests"] = new object[]
                    {
                        new { type = "control_request", request_id = "dlg1", request = new { subtype = "request_user_dialog", dialog_kind = "k", payload = new { } } }
                    }
                };
            }
        };
        await using var client = await ConnectAsync(transport, options);

        // A permission prompt the CLI already delivered is still being answered.
        SendRequest(transport, "inflight", new { subtype = "can_use_tool", tool_name = "Bash", input = new { } });
        await transport.WaitForAsync(_ => permissionCalls.Contains("inflight"));

        var fresh = await client.ReinitializeAsync();
        Assert.NotNull(fresh);
        await transport.WaitForAsync(t => t.ResponsesFor("p1").Count > 0 && t.ResponsesFor("dlg1").Count > 0);
        release.SetResult();
        await transport.WaitForAsync(t => t.ResponsesFor("inflight").Count > 0);
        await Task.Delay(50);

        Assert.Equal(1, permissionCalls.Count(id => id == "inflight"));
        Assert.Single(transport.ResponsesFor("inflight"));
        Assert.Contains("dlg1", dialogs);
        Assert.Empty(transport.ResponsesFor("x1"));
        Assert.Equal(2, transport.RequestsOf("initialize").Count);
        // Same hook ids / payload re-sent.
        Assert.Equal(Raw(transport.RequestsOf("initialize")[0]), Raw(transport.RequestsOf("initialize")[1]));
    }

    [Fact]
    public async Task PendingRequests_OnNonInitializeResponse_AreIgnored()
    {
        var calls = 0;
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<PermissionResult>(new PermissionResultAllow());
            }
        };
        var transport = new FakeTransport
        {
            EnvelopeExtras = body => body.GetProperty("subtype").GetString() == "mcp_status"
                ? new Dictionary<string, object?>
                {
                    ["pending_permission_requests"] = new object[]
                    {
                        new { type = "control_request", request_id = "p9", request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { } } }
                    }
                }
                : null
        };
        await using var client = await ConnectAsync(transport, options);
        await client.GetMcpStatusAsync();
        await Task.Delay(100);
        Assert.Equal(0, calls);
        Assert.Empty(transport.ResponsesFor("p9"));
    }

    [Fact]
    public async Task InitialInitialize_AlsoRedeliversPendingPrompts()
    {
        var seen = new TaskCompletionSource<string>();
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, ctx, _) =>
            {
                seen.TrySetResult(ctx.RequestId!);
                return Task.FromResult<PermissionResult>(new PermissionResultAllow());
            }
        };
        var transport = new FakeTransport
        {
            EnvelopeExtras = body => body.GetProperty("subtype").GetString() == "initialize"
                ? new Dictionary<string, object?>
                {
                    ["pending_permission_requests"] = new object[]
                    {
                        new { type = "control_request", request_id = "p0", request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { } } }
                    }
                }
                : null
        };
        await using var client = await ConnectAsync(transport, options);
        Assert.Equal("p0", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await transport.WaitForAsync(t => t.ResponsesFor("p0").Count > 0);
    }

    #endregion

    #region Outbound cancellation vs timeout

    [Fact]
    public async Task CancelAfterWrite_SendsControlCancelRequest()
    {
        var transport = new FakeTransport { HoldResponse = b => b.GetProperty("subtype").GetString() == "get_settings" };
        await using var client = await ConnectAsync(transport);

        using var cts = new CancellationTokenSource();
        var task = client.SendControlRequestAsync("get_settings", cancellationToken: cts.Token);
        await transport.WaitForAsync(t => t.RequestsOf("get_settings").Count == 1);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var requestId = transport.Written
            .First(w => w.GetProperty("type").GetString() == "control_request" &&
                        w.GetProperty("request").GetProperty("subtype").GetString() == "get_settings")
            .GetProperty("request_id").GetString();
        var cancel = Assert.Single(transport.Written, w => w.GetProperty("type").GetString() == "control_cancel_request");
        Assert.Equal($$"""{"type":"control_cancel_request","request_id":"{{requestId}}"}""", Raw(cancel));
    }

    [Fact]
    public async Task Timeout_ThrowsTimeoutMessageAndSendsNoCancel()
    {
        var transport = new FakeTransport { HoldResponse = b => b.GetProperty("subtype").GetString() == "get_settings" };
        await using var client = await ConnectAsync(transport);

        var ex = await Assert.ThrowsAsync<ClaudeSDKException>(() =>
            client.SendControlRequestAsync("get_settings", timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal("Control request timeout: get_settings", ex.Message);
        Assert.DoesNotContain(transport.Written, w => w.GetProperty("type").GetString() == "control_cancel_request");
    }

    [Fact]
    public async Task CancelBeforeWrite_SendsNothing()
    {
        var transport = new FakeTransport();
        await using var client = await ConnectAsync(transport);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendControlRequestAsync("get_settings", cancellationToken: cts.Token));
        Assert.Empty(transport.RequestsOf("get_settings"));
        Assert.DoesNotContain(transport.Written, w => w.GetProperty("type").GetString() == "control_cancel_request");
    }

    [Fact]
    public async Task ExistingMethods_CancelWithOperationCanceled_NotTimeoutMessage()
    {
        var transport = new FakeTransport { HoldResponse = b => b.GetProperty("subtype").GetString() == "set_model" };
        await using var client = await ConnectAsync(transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetModelAsync("x", cts.Token));
        Assert.Single(transport.Written, w => w.GetProperty("type").GetString() == "control_cancel_request");
    }

    #endregion

    #region Outbound wire shapes

    private static async Task<(FakeTransport Transport, ClaudeSDKClient Client)> ClientWith(
        Func<JsonElement, object?>? responder = null, ClaudeAgentOptions? options = null)
    {
        var transport = new FakeTransport();
        if (responder != null)
            transport.ControlResponder = responder;
        var client = await ConnectAsync(transport, options);
        return (transport, client);
    }

    private static string LastRequest(FakeTransport t, string subtype) => Raw(t.RequestsOf(subtype).Last());

    [Fact]
    public async Task Interrupt_CancelQueuedAndReceipt()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "interrupt"
            ? new { still_queued = new[] { "a", "b" }, cancelled = new[] { "c" } }
            : new { });
        await using var _ = client;

        var receipt = await client.InterruptAsync(cancelQueued: true);
        Assert.Equal("""{"subtype":"interrupt","cancel_queued":true}""", LastRequest(t, "interrupt"));
        Assert.Equal(["a", "b"], receipt!.StillQueued);
        Assert.Equal(["c"], receipt.Cancelled!);

        await client.InterruptAsync(cancelQueued: false);
        Assert.Equal("""{"subtype":"interrupt"}""", LastRequest(t, "interrupt"));

        await client.InterruptAsync(); // Python-parity overload
        Assert.Equal("""{"subtype":"interrupt"}""", LastRequest(t, "interrupt"));
    }

    [Fact]
    public async Task Interrupt_OldCliHasNoReceipt()
    {
        var (_, client) = await ClientWith();
        await using var __ = client;
        Assert.Null(await client.InterruptAsync(cancelQueued: false));
        Assert.Null(QueryHandler.ParseInterruptReceipt(Json("""{"still_queued":"x"}""")));
        Assert.Null(QueryHandler.ParseInterruptReceipt(Json("""{"still_queued":["a"]}"""))!.Cancelled);
    }

    [Fact]
    public async Task RewindFiles_DryRunAndResult()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "rewind_files"
            ? new { canRewind = true, filesChanged = new[] { "a.cs" }, insertions = 3, deletions = 1, skippedLinks = 0 }
            : new { });
        await using var _ = client;

        var result = await client.RewindFilesAsync("u1", dryRun: true);
        Assert.Equal("""{"subtype":"rewind_files","user_message_id":"u1","dry_run":true}""", LastRequest(t, "rewind_files"));
        Assert.True(result.CanRewind);
        Assert.Equal(["a.cs"], result.FilesChanged!);
        Assert.Equal((3, 1, 0), (result.Insertions, result.Deletions, result.SkippedLinks));

        await client.RewindFilesAsync("u2", dryRun: false);
        Assert.Equal("""{"subtype":"rewind_files","user_message_id":"u2","dry_run":false}""", LastRequest(t, "rewind_files"));

        await client.RewindFilesAsync("u3"); // Python-parity overload: no dry_run
        Assert.Equal("""{"subtype":"rewind_files","user_message_id":"u3"}""", LastRequest(t, "rewind_files"));
    }

    [Fact]
    public async Task GetContextUsage_SendsDetail()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "get_context_usage"
            ? new
            {
                categories = Array.Empty<object>(), totalTokens = 1, maxTokens = 2, rawMaxTokens = 2,
                percentage = 50.0, model = "m", isAutoCompactEnabled = true
            }
            : new { });
        await using var _ = client;

        var usage = await client.GetContextUsageAsync(ContextUsageDetail.Summary);
        Assert.Equal("""{"subtype":"get_context_usage","detail":"summary"}""", LastRequest(t, "get_context_usage"));
        Assert.Equal(1, usage.TotalTokens);
        await client.GetContextUsageAsync(ContextUsageDetail.Full);
        Assert.Equal("""{"subtype":"get_context_usage","detail":"full"}""", LastRequest(t, "get_context_usage"));
    }

    [Fact]
    public async Task SetMaxThinkingTokens_DisplayOmittedSetOrCleared()
    {
        var (t, client) = await ClientWith();
        await using var _ = client;

        await client.SetMaxThinkingTokensAsync(1000);
        Assert.Equal("""{"subtype":"set_max_thinking_tokens","max_thinking_tokens":1000}""", LastRequest(t, "set_max_thinking_tokens"));
        await client.SetMaxThinkingTokensAsync(null, ThinkingDisplayMode.Highlights);
        Assert.Equal("""{"subtype":"set_max_thinking_tokens","max_thinking_tokens":null,"thinking_display":"highlights"}""", LastRequest(t, "set_max_thinking_tokens"));
        await client.SetMaxThinkingTokensAsync(5, clearThinkingDisplay: true);
        Assert.Equal("""{"subtype":"set_max_thinking_tokens","max_thinking_tokens":5,"thinking_display":null}""", LastRequest(t, "set_max_thinking_tokens"));
    }

    [Fact]
    public async Task SetMcpPermissionModeOverride_ModeAndWarning()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "set_mcp_permission_mode_override"
            ? new { warning = "unknown server" }
            : new { });
        await using var _ = client;

        Assert.Equal("unknown server", await client.SetMcpPermissionModeOverrideAsync("srv", McpPermissionModeOverride.Auto));
        Assert.Equal("""{"subtype":"set_mcp_permission_mode_override","serverName":"srv","mode":"auto"}""", LastRequest(t, "set_mcp_permission_mode_override"));
        await client.SetMcpPermissionModeOverrideAsync("srv", null);
        Assert.Equal("""{"subtype":"set_mcp_permission_mode_override","serverName":"srv","mode":null}""", LastRequest(t, "set_mcp_permission_mode_override"));
        await client.SetMcpPermissionModeOverrideAsync("srv", McpPermissionModeOverride.Default);
        Assert.Equal("""{"subtype":"set_mcp_permission_mode_override","serverName":"srv","mode":"default"}""", LastRequest(t, "set_mcp_permission_mode_override"));
    }

    [Fact]
    public async Task ApplyFlagSettings_KeepsExplicitNulls()
    {
        var (t, client) = await ClientWith();
        await using var _ = client;

        await client.ApplyFlagSettingsAsync(new Dictionary<string, object?>
        {
            ["model"] = null,
            ["effortLevel"] = "max",
            ["permissions"] = new Dictionary<string, object?> { ["deny"] = new[] { "Bash" } }
        });
        Assert.Equal(
            """{"subtype":"apply_flag_settings","settings":{"model":null,"effortLevel":"max","permissions":{"deny":["Bash"]}}}""",
            LastRequest(t, "apply_flag_settings"));
    }

    [Fact]
    public async Task UpdateSettings_SourceAndSettings()
    {
        var (t, client) = await ClientWith();
        await using var _ = client;

        await client.UpdateSettingsAsync(SettingsFileSource.LocalSettings, new Dictionary<string, string> { ["outputStyle"] = "x" });
        Assert.Equal("""{"subtype":"update_settings","source":"localSettings","settings":{"outputStyle":"x"}}""", LastRequest(t, "update_settings"));
        await client.UpdateSettingsAsync(SettingsFileSource.UserSettings, new Dictionary<string, string> { ["effortLevel"] = "high" });
        Assert.Equal("""{"subtype":"update_settings","source":"userSettings","settings":{"effortLevel":"high"}}""", LastRequest(t, "update_settings"));
    }

    [Fact]
    public async Task BackgroundTasks_OptionalToolUseIdAndDefaultTrue()
    {
        var answer = (object)new { backgrounded = false };
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "background_tasks" ? answer : new { });
        await using var _ = client;

        Assert.False(await client.BackgroundTasksAsync("tu1"));
        Assert.Equal("""{"subtype":"background_tasks","tool_use_id":"tu1"}""", LastRequest(t, "background_tasks"));
        answer = new { };
        Assert.True(await client.BackgroundTasksAsync());
        Assert.Equal("""{"subtype":"background_tasks"}""", LastRequest(t, "background_tasks"));
    }

    [Fact]
    public async Task SeedReadState_PathAndMtime()
    {
        var (t, client) = await ClientWith();
        await using var _ = client;
        await client.SeedReadStateAsync("/a/b.cs", 1_700_000_000_123);
        Assert.Equal("""{"subtype":"seed_read_state","path":"/a/b.cs","mtime":1700000000123}""", LastRequest(t, "seed_read_state"));
    }

    [Fact]
    public async Task ReadFile_ShapesAndNullOnError()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "read_file"
            ? new { contents = "aGk=", absPath = "/abs/x.png", truncated = true, encoding = "base64" }
            : new { });
        await using var _ = client;

        var r = await client.ReadFileAsync("x.png", maxBytes: 10, encoding: ReadFileEncoding.Base64);
        Assert.Equal("""{"subtype":"read_file","path":"x.png","max_bytes":10,"encoding":"base64"}""", LastRequest(t, "read_file"));
        Assert.Equal(new ReadFileResult("aGk=", "/abs/x.png", true, "base64"), r);

        await client.ReadFileAsync("y.txt");
        Assert.Equal("""{"subtype":"read_file","path":"y.txt"}""", LastRequest(t, "read_file"));
        await client.ReadFileAsync("y.txt", encoding: ReadFileEncoding.Utf8);
        Assert.Equal("""{"subtype":"read_file","path":"y.txt","encoding":"utf-8"}""", LastRequest(t, "read_file"));
    }

    [Fact]
    public async Task ReadFile_ErrorReturnsNull()
    {
        var transport = new FakeTransport { HoldResponse = b => b.GetProperty("subtype").GetString() == "read_file" };
        await using var client = await ConnectAsync(transport);
        var task = client.ReadFileAsync("nope");
        await transport.WaitForAsync(t => t.RequestsOf("read_file").Count == 1);
        var id = transport.Written.Last(w => w.GetProperty("type").GetString() == "control_request").GetProperty("request_id").GetString();
        transport.Send(new { type = "control_response", response = new { subtype = "error", request_id = id, error = "denied" } });
        Assert.Null(await task);
    }

    [Fact]
    public async Task ReloadPlugins_HoldAndParse()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "reload_plugins"
            ? new
            {
                commands = new[] { new { name = "c", description = "d", argumentHint = "" } },
                agents = new[] { new { name = "a", description = "ad", model = "opus" } },
                plugins = new[] { new { name = "p", path = "/p", source = "local", version = "1.0" } },
                mcpServers = new[] { new { name = "s", status = "connected" } },
                error_count = 2,
                held = true,
                cache_impact = new { mcp_servers_added = new[] { "plugin:p:s" }, mcp_servers_removed = Array.Empty<string>(), lsp_tool_change = "adds" }
            }
            : new { });
        await using var _ = client;

        var r = await client.ReloadPluginsAsync(holdOnCacheImpact: true);
        Assert.Equal("""{"subtype":"reload_plugins","hold_on_cache_impact":true}""", LastRequest(t, "reload_plugins"));
        Assert.Equal("c", Assert.Single(r.Commands).Name);
        Assert.Equal("opus", Assert.Single(r.Agents).Model);
        Assert.Equal(new ReloadedPlugin("p", "/p", "local", "1.0"), Assert.Single(r.Plugins));
        Assert.Equal("s", Assert.Single(r.McpServers).GetProperty("name").GetString());
        Assert.Equal(2, r.ErrorCount);
        Assert.True(r.Held);
        Assert.Equal(["plugin:p:s"], r.CacheImpact!.McpServersAdded);
        Assert.Equal("adds", r.CacheImpact.LspToolChange);

        await client.ReloadPluginsAsync();
        Assert.Equal("""{"subtype":"reload_plugins"}""", LastRequest(t, "reload_plugins"));
    }

    [Fact]
    public async Task ReloadSkillsAndOutputStyles()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() switch
        {
            "reload_skills" => new { skills = new[] { new { name = "pdf", description = "PDF", argumentHint = "<file>" } } },
            "reload_output_styles" => new { available_output_styles = new[] { "default", "Explanatory" } },
            _ => new { }
        });
        await using var _ = client;

        var skills = await client.ReloadSkillsAsync();
        Assert.Equal("""{"subtype":"reload_skills"}""", LastRequest(t, "reload_skills"));
        var skill = Assert.Single(skills);
        Assert.Equal(("pdf", "PDF", "<file>"), (skill.Name, skill.Description, skill.ArgumentHint));

        Assert.Equal(["default", "Explanatory"], await client.ReloadOutputStylesAsync());
        Assert.Equal("""{"subtype":"reload_output_styles"}""", LastRequest(t, "reload_output_styles"));
    }

    [Fact]
    public async Task ReadMcpResource_ShapeAndParse()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "mcp_read_resource"
            ? new { contents = new object[] { new { uri = "ui://w", mimeType = "text/html", text = "<b/>", _meta = new { ui = 1 } }, new { uri = "ui://b", blob = "AA==" } } }
            : new { });
        await using var _ = client;

        var r = await client.ReadMcpResourceAsync("srv", "ui://w");
        Assert.Equal("""{"subtype":"mcp_read_resource","serverName":"srv","uri":"ui://w"}""", LastRequest(t, "mcp_read_resource"));
        Assert.Equal(2, r.Contents.Count);
        Assert.Equal(("ui://w", "text/html", "<b/>"), (r.Contents[0].Uri, r.Contents[0].MimeType, r.Contents[0].Text));
        Assert.Equal(1, r.Contents[0].Meta!.Value.GetProperty("ui").GetInt32());
        Assert.Equal("AA==", r.Contents[1].Blob);
        Assert.Null(r.Contents[1].Meta);
    }

    [Fact]
    public async Task SetMcpServers_SplitsSdkServersAndRegistersBridges()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "mcp_set_servers"
            ? new { added = new[] { "calc", "ext" }, removed = new[] { "old" }, errors = new { ext = "boom" } }
            : new { });
        await using var _ = client;

        var registry = McpServers.Sdk("calc", s => s.Tool(
            "add",
            Json("""{"type":"object"}"""),
            (_, _) => Task.FromResult(McpToolResults.Text("3")),
            "Add"));
        var calc = ((McpSdkServerConfig)registry["calc"]) with { Timeout = 5000 };
        var result = await client.SetMcpServersAsync(new Dictionary<string, object>
        {
            ["ext"] = new McpStdioServerConfig { Command = "srv", Timeout = 2000, AlwaysLoad = true },
            ["calc"] = calc
        });

        Assert.Equal(
            """{"subtype":"mcp_set_servers","servers":{"ext":{"type":"stdio","command":"srv","timeout":2000,"alwaysLoad":true},"calc":{"type":"sdk","name":"calc","timeout":5000}}}""",
            LastRequest(t, "mcp_set_servers"));
        Assert.Equal(["calc", "ext"], result.Added);
        Assert.Equal(["old"], result.Removed);
        Assert.Equal("boom", result.Errors["ext"]);

        // The newly registered in-process server now answers the CLI.
        SendRequest(t, "m1", new
        {
            subtype = "mcp_message",
            server_name = "calc",
            message = new { jsonrpc = "2.0", id = 1, method = "tools/list" }
        });
        await t.WaitForAsync(x => x.ResponsesFor("m1").Count > 0);
        var mcp = InnerResponse(t, "m1").GetProperty("mcp_response");
        Assert.Equal("add", mcp.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

        // Removing it unregisters the bridge.
        await client.SetMcpServersAsync(new Dictionary<string, object>());
        Assert.Equal("""{"subtype":"mcp_set_servers","servers":{}}""", LastRequest(t, "mcp_set_servers"));
        SendRequest(t, "m2", new
        {
            subtype = "mcp_message",
            server_name = "calc",
            message = new { jsonrpc = "2.0", id = 2, method = "tools/list" }
        });
        await t.WaitForAsync(x => x.ResponsesFor("m2").Count > 0);
        Assert.Equal(-32601, InnerResponse(t, "m2").GetProperty("mcp_response").GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task GetUsage_SkipBehaviorsAndParse()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "get_usage"
            ? new { session = new { total_cost_usd = 1.5 }, subscription_type = "max", rate_limits_available = true, rate_limits = new { five_hour = new { utilization = 10 } }, behaviors = (object?)null }
            : new { });
        await using var _ = client;

        var usage = await client.GetUsageAsync(skipBehaviors: true);
        Assert.Equal("""{"subtype":"get_usage","skip_behaviors":true}""", LastRequest(t, "get_usage"));
        Assert.Equal("max", usage.SubscriptionType);
        Assert.True(usage.RateLimitsAvailable);
        Assert.Equal(1.5, usage.Session!.Value.GetProperty("total_cost_usd").GetDouble());
        Assert.NotNull(usage.RateLimits);
        Assert.Null(usage.Behaviors);

        await client.GetUsageAsync();
        Assert.Equal("""{"subtype":"get_usage"}""", LastRequest(t, "get_usage"));
    }

    [Fact]
    public async Task EscapeHatch_SendsSubtypeAndFields()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "get_hooks_listing"
            ? new { events = new[] { "PreToolUse" } }
            : new { });
        await using var _ = client;

        var r = await client.SendControlRequestAsync("get_hooks_listing");
        Assert.Equal("""{"subtype":"get_hooks_listing"}""", LastRequest(t, "get_hooks_listing"));
        Assert.Equal("PreToolUse", r.GetProperty("events")[0].GetString());

        await client.SendControlRequestAsync("set_cwd", new Dictionary<string, object?>
        {
            ["path"] = "/w",
            ["trust_accepted"] = true,
            ["trusted_directory"] = null
        });
        Assert.Equal("""{"subtype":"set_cwd","path":"/w","trust_accepted":true,"trusted_directory":null}""", LastRequest(t, "set_cwd"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendControlRequestAsync("x", new Dictionary<string, object?> { ["subtype"] = "y" }));
    }

    [Fact]
    public async Task EscapeHatch_ErrorResponseThrows()
    {
        var transport = new FakeTransport { HoldResponse = b => b.GetProperty("subtype").GetString() == "boom" };
        await using var client = await ConnectAsync(transport);
        var task = client.SendControlRequestAsync("boom");
        await transport.WaitForAsync(t => t.RequestsOf("boom").Count == 1);
        var id = transport.Written.Last(w => w.GetProperty("type").GetString() == "control_request").GetProperty("request_id").GetString();
        transport.Send(new { type = "control_response", response = new { subtype = "error", request_id = id, error = "nope" } });
        var ex = await Assert.ThrowsAsync<ClaudeSDKException>(() => task);
        Assert.Equal("nope", ex.Message);
    }

    [Fact]
    public async Task InternalWrappers_WireShapes()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() switch
        {
            "generate_session_title" => new { title = "My title" },
            "cancel_async_message" => new { cancelled = true },
            "get_settings" => new { model = "opus" },
            "claude_authenticate" => new { manualUrl = "https://m" },
            _ => new { }
        });
        await using var _ = client;

        Assert.Equal("https://m", (await client.ClaudeAuthenticateAsync(true)).GetProperty("manualUrl").GetString());
        Assert.Equal("""{"subtype":"claude_authenticate","loginWithClaudeAi":true}""", LastRequest(t, "claude_authenticate"));

        await client.ClaudeOAuthCallbackAsync("code1", "state1");
        Assert.Equal("""{"subtype":"claude_oauth_callback","authorizationCode":"code1","state":"state1"}""", LastRequest(t, "claude_oauth_callback"));

        await client.ClaudeOAuthWaitForCompletionAsync();
        Assert.Equal("""{"subtype":"claude_oauth_wait_for_completion"}""", LastRequest(t, "claude_oauth_wait_for_completion"));

        Assert.Equal("opus", (await client.GetSettingsAsync()).GetProperty("model").GetString());
        Assert.Equal("""{"subtype":"get_settings"}""", LastRequest(t, "get_settings"));

        await client.RenameSessionAsync("New");
        Assert.Equal("""{"subtype":"rename_session","title":"New","source":"host"}""", LastRequest(t, "rename_session"));
        await client.RenameSessionAsync("New", "sid");
        Assert.Equal("""{"subtype":"rename_session","title":"New","source":"host","session_id":"sid"}""", LastRequest(t, "rename_session"));

        Assert.Equal("My title", await client.GenerateSessionTitleAsync("desc", persist: true));
        Assert.Equal("""{"subtype":"generate_session_title","description":"desc","persist":true}""", LastRequest(t, "generate_session_title"));
        await client.GenerateSessionTitleAsync("desc");
        Assert.Equal("""{"subtype":"generate_session_title","description":"desc"}""", LastRequest(t, "generate_session_title"));

        Assert.True(await client.CancelAsyncMessageAsync("uuid-1"));
        Assert.Equal("""{"subtype":"cancel_async_message","message_uuid":"uuid-1"}""", LastRequest(t, "cancel_async_message"));
    }

    [Fact]
    public async Task SideQuestion_ShapeAndParse()
    {
        object answer = new
        {
            response = "42",
            synthetic = true,
            refusal_fallback = new { original_model = "a", fallback_model = "b", content = new[] { "x" } }
        };
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "side_question" ? answer : new { });
        await using var _ = client;

        var r = await client.AskSideQuestionAsync("why?", [Json("""{"role":"user","content":"hi"}""")]);
        Assert.Equal("""{"subtype":"side_question","question":"why?","history":[{"role":"user","content":"hi"}]}""", LastRequest(t, "side_question"));
        Assert.Equal("42", r!.Response);
        Assert.True(r.Synthetic);
        Assert.Equal(("a", "b"), (r.RefusalFallback!.OriginalModel, r.RefusalFallback.FallbackModel));

        answer = new { response = (string?)null };
        Assert.Null(await client.AskSideQuestionAsync("again", []));
        Assert.Equal("""{"subtype":"side_question","question":"again"}""", LastRequest(t, "side_question"));
    }

    [Fact]
    public async Task McpServerMessage_IsFireAndForgetControlRequest()
    {
        var (t, client) = await ClientWith();
        await using var _ = client;

        await client.NotifyMcpToolsListChangedAsync("calc");
        var frame = t.Written.Last(w => w.GetProperty("type").GetString() == "control_request");
        Assert.True(Guid.TryParse(frame.GetProperty("request_id").GetString(), out var _));
        Assert.Equal(
            """{"subtype":"mcp_message","server_name":"calc","message":{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}}""",
            Raw(frame.GetProperty("request")));
    }

    [Fact]
    public async Task NotConnected_Throws()
    {
        var client = new ClaudeSDKClient(new ClaudeAgentOptions(), new FakeTransport());
        await Assert.ThrowsAsync<CliConnectionException>(() => client.SendControlRequestAsync("x"));
        await Assert.ThrowsAsync<CliConnectionException>(() => client.SupportedCommandsAsync());
    }

    #endregion

    #region Typed initialize helpers

    private static object InitResponse() => new
    {
        commands = new[] { new { name = "compact", description = "Compact", argumentHint = "[focus]" } },
        agents = new[] { new { name = "reviewer", description = "Reviews", model = (string?)null } },
        output_style = "default",
        available_output_styles = new[] { "default", "Learning" },
        models = new[] { new { value = "opus", displayName = "Opus", description = "Big" } },
        account = new { email = "a@b.c", organization = "Org", subscriptionType = "max", tokenSource = "oauth", apiKeySource = (string?)null, apiProvider = "firstParty" }
    };

    [Fact]
    public async Task InitializationResult_IsTyped()
    {
        var (_, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "initialize" ? InitResponse() : new { });
        await using var __ = client;

        var init = await client.InitializationResultAsync();
        Assert.Equal("default", init.OutputStyle);
        Assert.Equal(["default", "Learning"], init.AvailableOutputStyles);
        var command = Assert.Single(await client.SupportedCommandsAsync());
        Assert.Equal(("compact", "Compact", "[focus]"), (command.Name, command.Description, command.ArgumentHint));
        var agent = Assert.Single(await client.SupportedAgentsAsync());
        Assert.Equal(("reviewer", "Reviews", (string?)null), (agent.Name, agent.Description, agent.Model));
        var model = Assert.Single(await client.SupportedModelsAsync());
        Assert.Equal(("opus", "Opus", "Big"), (model.Value, model.DisplayName, model.Description));
        var account = await client.AccountInfoAsync();
        Assert.Equal(("a@b.c", "Org", "max", "oauth", null, "firstParty"),
            (account!.Email, account.Organization, account.SubscriptionType, account.TokenSource, account.ApiKeySource, account.ApiProvider));
        Assert.Equal("default", init.Raw.GetProperty("output_style").GetString());
    }

    [Fact]
    public async Task SupportedCommands_FollowCommandsChanged()
    {
        var (t, client) = await ClientWith(b => b.GetProperty("subtype").GetString() == "initialize" ? InitResponse() : new { });
        await using var _ = client;

        t.Send(new
        {
            type = "system",
            subtype = "commands_changed",
            commands = new[] { new { name = "new-skill", description = "N", argumentHint = "" } },
            uuid = "u",
            session_id = "s"
        });
        await t.WaitForAsync(_ => client.SupportedCommandsAsync().Result.Any(c => c.Name == "new-skill"));
        Assert.Equal("new-skill", Assert.Single(await client.SupportedCommandsAsync()).Name);
        // The initialize result itself is unchanged.
        Assert.Equal("compact", Assert.Single((await client.InitializationResultAsync()).Commands).Name);
    }

    [Fact]
    public void InitializeResponse_ParseToleratesMissingMembers()
    {
        var r = InitializeResponse.Parse(Json("{}"));
        Assert.Empty(r.Commands);
        Assert.Empty(r.Models);
        Assert.Null(r.Account);
        Assert.Null(r.OutputStyle);
    }

    #endregion

    #region Raw frame tap

    [Fact]
    public async Task OnRawMessage_SeesEveryFrameIncludingControl()
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var options = new ClaudeAgentOptions
        {
            OnRawMessage = f => seen.Enqueue(f.GetProperty("type").GetString()!)
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);

        transport.Send(new { type = "keep_alive" });
        SendRequest(transport, "r1", new { subtype = "elicitation", mcp_server_name = "s", message = "m" });
        transport.Send(new { type = "control_cancel_request", request_id = "zzz" });
        transport.Send(new { type = "system", subtype = "status", uuid = "u", session_id = "s" });
        await transport.WaitForAsync(_ => seen.Count >= 5);

        // control_response to initialize, then the frames above in order.
        Assert.Equal(["control_response", "keep_alive", "control_request", "control_cancel_request", "system"], seen.ToArray());
    }

    [Fact]
    public async Task OnRawMessage_ThrowingTapDoesNotBreakTheLoop()
    {
        var options = new ClaudeAgentOptions { OnRawMessage = _ => throw new InvalidOperationException("tap") };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);
        SendRequest(transport, "r2", new { subtype = "elicitation", mcp_server_name = "s", message = "m" });
        await transport.WaitForAsync(t => t.ResponsesFor("r2").Count > 0);
    }

    #endregion

    #region Python 0.2.160 session-state handshake

    private static object StateFrame(string state, bool hostOnly = true) => hostOnly
        ? new { type = "system", subtype = "session_state_changed", state, sdk_host_only = true, uuid = "u", session_id = "s" }
        : new { type = "system", subtype = "session_state_changed", state, uuid = "u", session_id = "s" };

    private static object Result() => new
    {
        type = "result", subtype = "success", duration_ms = 1, duration_api_ms = 1,
        is_error = false, num_turns = 1, session_id = "s"
    };

    private static async IAsyncEnumerable<Dictionary<string, object?>> OnePrompt()
    {
        await Task.Yield();
        yield return new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = "hi" },
            ["parent_tool_use_id"] = null,
            ["session_id"] = ""
        };
    }

    private static ClaudeAgentOptions Bidirectional(string? ceilingMs = null) => new()
    {
        CanUseTool = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        Env = ceilingMs == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [QueryHandler.RunEndCeilingEnv] = ceilingMs }
    };

    [Fact]
    public async Task ResultWithoutSessionState_EndsTheRun()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, Bidirectional());
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));

        transport.Send(Result());
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Fact]
    public async Task RunningAfterResult_WaitsForIdle_AndHostOnlyFramesAreDropped()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, Bidirectional());
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));

        transport.Send(StateFrame("running"));
        transport.Send(Result());
        await Task.Delay(300);
        Assert.False(transport.InputEnded.Task.IsCompleted);

        transport.Send(StateFrame("idle", hostOnly: false));
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
        transport.Complete();

        var subtypes = new List<string>();
        await foreach (var m in handler.ReceiveMessagesAsync())
        {
            if (m is SystemMessage sm)
                subtypes.Add(sm.Subtype);
            else
                subtypes.Add(m.GetType().Name);
        }
        // The host-only "running" frame never reaches the stream; the caller-visible idle does.
        Assert.Equal(["ResultMessage", "session_state_changed"], subtypes);
    }

    [Fact]
    public async Task RequiresAction_DisarmsCeilingUntilIdle()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, Bidirectional(ceilingMs: "100"));
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));

        transport.Send(StateFrame("requires_action"));
        transport.Send(Result());
        await Task.Delay(400);
        Assert.False(transport.InputEnded.Task.IsCompleted);

        transport.Send(StateFrame("idle"));
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Fact]
    public async Task Ceiling_EndsRunWhenIdleNeverArrives()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, Bidirectional(ceilingMs: "150"));
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));

        transport.Send(StateFrame("running"));
        transport.Send(Result());
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Fact]
    public async Task ZeroCeiling_WaitsForIdleIndefinitely()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, Bidirectional(ceilingMs: "0"));
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));

        transport.Send(StateFrame("running"));
        transport.Send(Result());
        await Task.Delay(300);
        Assert.False(transport.InputEnded.Task.IsCompleted);
        transport.Send(StateFrame("idle"));
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Fact]
    public async Task WithoutBidirectionalNeeds_ResultEndsRunEvenWhileRunning()
    {
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, new ClaudeAgentOptions());
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5)); // no hold at all
        await input;
    }

    [Fact]
    public async Task ElicitationCallbackAlone_HoldsStdinForTheRun()
    {
        var options = new ClaudeAgentOptions
        {
            OnElicitation = (_, _, _) => Task.FromResult<ElicitationResult?>(new ElicitationResult(ElicitationAction.Decline))
        };
        var transport = new FakeTransport();
        await using var handler = await StartAsync(transport, options);
        var input = handler.StreamInputAsync(OnePrompt());
        await transport.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));
        await Task.Delay(150);
        Assert.False(transport.InputEnded.Task.IsCompleted);
        transport.Send(Result());
        await transport.InputEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await input;
    }

    [Theory]
    [InlineData(null, QueryHandler.DefaultRunEndCeilingMs)]
    [InlineData("0", 0)]
    [InlineData("1500", 1500)]
    [InlineData(" 42 ", 42)]
    [InlineData("-5", QueryHandler.DefaultRunEndCeilingMs)]
    [InlineData("abc", QueryHandler.DefaultRunEndCeilingMs)]
    [InlineData("99999999999", int.MaxValue)]
    public void RunEndCeilingMs_ParsesLikePython(string? value, int expected)
    {
        var env = value == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [QueryHandler.RunEndCeilingEnv] = value };
        if (value == null && Environment.GetEnvironmentVariable(QueryHandler.RunEndCeilingEnv) != null)
            return; // inherited value would win; not this test's concern
        Assert.Equal(expected, QueryHandler.RunEndCeilingMs(env));
    }

    #endregion
}
