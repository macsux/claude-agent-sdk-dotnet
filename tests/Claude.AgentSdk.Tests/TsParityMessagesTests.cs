// TypeScript SDK (0.3.283) parity: message types, raw frame preservation,
// unknown-type passthrough, enum raw strings, content-block fallbacks and
// info types. Fixtures are shaped like the sdk.d.ts types.

using System.Text.Json;
using Claude.AgentSdk.Internal;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class TsParityMessagesTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Message? Parse(string json) => MessageParser.ParseOrNull(J(json));

    private static T ParseAs<T>(string json) where T : Message => Assert.IsType<T>(Parse(json));

    private const string Ids = "\"uuid\":\"u-1\",\"session_id\":\"s-1\"";

    #region Raw frame preservation

    public static TheoryData<string> KnownFrames => new()
    {
        """{"type":"user","message":{"role":"user","content":"hi"},"parent_tool_use_id":null,"future_field":{"a":1}}""",
        """{"type":"assistant","message":{"id":"msg_1","model":"claude","content":[{"type":"text","text":"x"}],"container":{"id":"c"}},"parent_tool_use_id":null,"uuid":"u","session_id":"s","extra":[1,2]}""",
        """{"type":"system","subtype":"init","cwd":"/w","tools":["Bash"],"model":"m","brand_new":true,"uuid":"u","session_id":"s"}""",
        """{"type":"system","subtype":"totally_new_subtype","x":1}""",
        """{"type":"result","subtype":"success","duration_ms":1,"duration_api_ms":1,"is_error":false,"num_turns":1,"session_id":"s","first_content_frame_ms":12.5}""",
        """{"type":"stream_event","uuid":"u","session_id":"s","event":{"type":"message_start"},"parent_tool_use_id":null,"ttft_ms":42}""",
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"},"uuid":"u","session_id":"s","extra":"x"}""",
        """{"type":"conversation_reset","new_conversation_id":"c","uuid":"u","session_id":"s","trigger":"clear"}""",
        """{"type":"tool_progress","tool_use_id":"t","tool_name":"Bash","parent_tool_use_id":null,"elapsed_time_seconds":1.5,"uuid":"u","session_id":"s"}""",
        """{"type":"some_future_type","payload":{"deep":[1,{"x":null}]}}"""
    };

    [Theory]
    [MemberData(nameof(KnownFrames))]
    public void EveryParsedMessage_CarriesTheFullRawFrame(string json)
    {
        var msg = Assert.IsAssignableFrom<Message>(Parse(json));
        Assert.Equal(JsonValueKind.Object, msg.Raw.ValueKind);
        Assert.True(JsonElement.DeepEquals(J(json), msg.Raw), msg.GetType().Name);
    }

    [Fact]
    public void SystemMessage_RawAndDataAreTheSameFrame()
    {
        var msg = ParseAs<SystemMessage>("""{"type":"system","subtype":"whatever","k":"v"}""");
        Assert.Equal(msg.Data.GetRawText(), msg.Raw.GetRawText());
    }

    [Fact]
    public void Raw_SurvivesTheSourceDocumentBeingDisposed()
    {
        Message msg;
        using (var doc = JsonDocument.Parse("""{"type":"prompt_suggestion","suggestion":"next?","uuid":"u","session_id":"s"}"""))
            msg = MessageParser.ParseOrNull(doc.RootElement)!;
        Assert.Equal("next?", msg.Raw.GetProperty("suggestion").GetString());
    }

    [Fact]
    public void Raw_IsUndefinedForMessagesBuiltInCode()
    {
        var msg = new UserMessage { Content = J("\"hi\"") };
        Assert.Equal(JsonValueKind.Undefined, msg.Raw.ValueKind);
    }

    #endregion

    #region Unknown top-level types

    [Fact]
    public void UnknownTopLevelType_BecomesUnknownMessage()
    {
        var msg = ParseAs<UnknownMessage>("""{"type":"brand_new","value":42}""");
        Assert.Equal("brand_new", msg.Type);
        Assert.Equal(42, msg.Raw.GetProperty("value").GetInt32());
    }

    [Fact]
    public void KeepAlive_IsSkipped()
    {
        Assert.Null(Parse("""{"type":"keep_alive"}"""));
    }

    [Fact]
    public async Task QueryHandler_YieldsUnknownMessages_AndSkipsKeepAlive()
    {
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, new ClaudeAgentOptions());
        await handler.StartAsync();

        transport.Send(new { type = "keep_alive" });
        transport.Send(new { type = "brand_new_type", n = 1 });
        transport.Send(new { type = "tool_use_summary", summary = "Ran 2 tools", preceding_tool_use_ids = new[] { "a", "b" }, uuid = "u", session_id = "s" });
        transport.Send(new
        {
            type = "result", subtype = "success", duration_ms = 1, duration_api_ms = 1,
            is_error = false, num_turns = 1, session_id = "s"
        });

        var received = new List<Message>();
        await foreach (var msg in handler.ReceiveMessagesAsync())
        {
            received.Add(msg);
            if (msg is ResultMessage) break;
        }

        Assert.Collection(received,
            m => Assert.Equal("brand_new_type", Assert.IsType<UnknownMessage>(m).Type),
            m => Assert.Equal(["a", "b"], Assert.IsType<ToolUseSummaryMessage>(m).PrecedingToolUseIds),
            m => Assert.IsType<ResultMessage>(m));
    }

    #endregion

    #region New top-level message types

    [Fact]
    public void ToolProgress_ParsesAllFields()
    {
        var msg = ParseAs<ToolProgressMessage>("""
            {"type":"tool_progress","tool_use_id":"toolu_1","tool_name":"Task","parent_tool_use_id":"toolu_0",
             "elapsed_time_seconds":12.25,"task_id":"task_9","uuid":"u-1","session_id":"s-1","heartbeat":true,
             "subagent_type":"general-purpose",
             "subagent_retry":{"agent_id":"a1","attempt":2,"max_retries":5,"retry_delay_ms":1500,"error_status":529,"error_category":"overloaded"}}
            """);
        Assert.Equal("toolu_1", msg.ToolUseId);
        Assert.Equal("Task", msg.ToolName);
        Assert.Equal("toolu_0", msg.ParentToolUseId);
        Assert.Equal(12.25, msg.ElapsedTimeSeconds);
        Assert.Equal("task_9", msg.TaskId);
        Assert.Equal("u-1", msg.Uuid);
        Assert.Equal("s-1", msg.SessionId);
        Assert.True(msg.Heartbeat);
        Assert.Equal("general-purpose", msg.SubagentType);
        var retry = Assert.IsType<SubagentRetryInfo>(msg.SubagentRetry);
        Assert.Equal(("a1", 2, 5, 1500d, 529, "overloaded"),
            (retry.AgentId, retry.Attempt, retry.MaxRetries, retry.RetryDelayMs, retry.ErrorStatus, retry.ErrorCategory));
    }

    [Fact]
    public void ToolProgress_NullErrorStatus_AndMinimalFrame()
    {
        var msg = ParseAs<ToolProgressMessage>("""
            {"type":"tool_progress","tool_use_id":"t","tool_name":"Bash","parent_tool_use_id":null,"elapsed_time_seconds":3,
             "subagent_retry":{"agent_id":"a","attempt":1,"max_retries":3,"retry_delay_ms":10,"error_status":null,"error_category":"network"}}
            """);
        Assert.Null(msg.ParentToolUseId);
        Assert.Null(msg.SubagentRetry!.ErrorStatus);
        Assert.Null(msg.Heartbeat);
        Assert.Null(msg.TaskId);
    }

    [Fact]
    public void NewTopLevelTypes_AreLenient_AndNeverThrow()
    {
        Assert.IsType<ToolProgressMessage>(Parse("""{"type":"tool_progress"}"""));
        Assert.IsType<ToolUseSummaryMessage>(Parse("""{"type":"tool_use_summary","preceding_tool_use_ids":"oops"}"""));
        Assert.IsType<AuthStatusMessage>(Parse("""{"type":"auth_status","output":null}"""));
        Assert.IsType<PromptSuggestionMessage>(Parse("""{"type":"prompt_suggestion"}"""));
        Assert.IsType<ActiveGoalMessage>(Parse("""{"type":"active_goal","value":"not-an-object"}"""));
    }

    [Fact]
    public void ToolUseSummary_ParsesAllFields()
    {
        var msg = ParseAs<ToolUseSummaryMessage>($$$"""
            {"type":"tool_use_summary","summary":"Read 3 files","preceding_tool_use_ids":["t1","t2","t3"],{{{Ids}}}}
            """);
        Assert.Equal("Read 3 files", msg.Summary);
        Assert.Equal(["t1", "t2", "t3"], msg.PrecedingToolUseIds);
        Assert.Equal(("u-1", "s-1"), (msg.Uuid, msg.SessionId));
    }

    [Fact]
    public void AuthStatus_ParsesAllFields()
    {
        var msg = ParseAs<AuthStatusMessage>($$$"""
            {"type":"auth_status","isAuthenticating":true,"output":["Opening browser","Waiting..."],"error":"timeout",{{{Ids}}}}
            """);
        Assert.True(msg.IsAuthenticating);
        Assert.Equal(["Opening browser", "Waiting..."], msg.Output);
        Assert.Equal("timeout", msg.Error);
        Assert.Equal("s-1", msg.SessionId);
    }

    [Fact]
    public void PromptSuggestion_ParsesAllFields()
    {
        var msg = ParseAs<PromptSuggestionMessage>($$$"""{"type":"prompt_suggestion","suggestion":"Run the tests",{{{Ids}}}}""");
        Assert.Equal("Run the tests", msg.Suggestion);
        Assert.Equal("u-1", msg.Uuid);
    }

    [Fact]
    public void ActiveGoal_ParsesValue_AndNullValue()
    {
        var set = ParseAs<ActiveGoalMessage>($$$"""
            {"type":"active_goal","value":{"condition":"all tests pass","iterations":3,"set_at":1767225600000,"tokens_at_start":123456,"last_reason":"2 failing"},{{{Ids}}}}
            """);
        var goal = Assert.IsType<ActiveGoal>(set.Value);
        Assert.Equal("all tests pass", goal.Condition);
        Assert.Equal(3, goal.Iterations);
        Assert.Equal(1767225600000d, goal.SetAt);
        Assert.Equal(123456L, goal.TokensAtStart);
        Assert.Equal("2 failing", goal.LastReason);

        var cleared = ParseAs<ActiveGoalMessage>($$$"""{"type":"active_goal","value":null,{{{Ids}}}}""");
        Assert.Null(cleared.Value);
    }

    #endregion

    #region AssistantMessageError / RateLimitType raw strings

    [Theory]
    [InlineData("authentication_failed", AssistantMessageError.AuthenticationFailed)]
    [InlineData("oauth_org_not_allowed", AssistantMessageError.OauthOrgNotAllowed)]
    [InlineData("account_on_hold", AssistantMessageError.AccountOnHold)]
    [InlineData("verification_required", AssistantMessageError.VerificationRequired)]
    [InlineData("billing_error", AssistantMessageError.BillingError)]
    [InlineData("rate_limit", AssistantMessageError.RateLimit)]
    [InlineData("overloaded", AssistantMessageError.Overloaded)]
    [InlineData("invalid_request", AssistantMessageError.InvalidRequest)]
    [InlineData("model_not_found", AssistantMessageError.ModelNotFound)]
    [InlineData("server_error", AssistantMessageError.ServerError)]
    [InlineData("unknown", AssistantMessageError.Unknown)]
    [InlineData("max_output_tokens", AssistantMessageError.MaxOutputTokens)]
    [InlineData("cloud_credential_error", AssistantMessageError.CloudCredentialError)]
    public void AssistantError_AllTsValuesMap_AndRawIsKept(string wire, AssistantMessageError expected)
    {
        var msg = ParseAs<AssistantMessage>($$"""{"type":"assistant","message":{"content":[],"model":"m"},"error":"{{wire}}"}""");
        Assert.Equal(expected, msg.Error);
        Assert.Equal(wire, msg.ErrorRaw);
        Assert.Equal(wire, expected.ToWireString());
    }

    [Fact]
    public void AssistantError_UnrecognizedValue_IsUnknownButRawPreserved()
    {
        var msg = ParseAs<AssistantMessage>("""{"type":"assistant","message":{"content":[],"model":"m"},"error":"quota_exceeded_v2"}""");
        Assert.Equal(AssistantMessageError.Unknown, msg.Error);
        Assert.Equal("quota_exceeded_v2", msg.ErrorRaw);
    }

    [Fact]
    public void AssistantError_ExistingNumericValuesAreStable()
    {
        Assert.Equal(0, (int)AssistantMessageError.AuthenticationFailed);
        Assert.Equal(5, (int)AssistantMessageError.Unknown);
    }

    [Fact]
    public void AssistantError_NoError_IsNull()
    {
        var msg = ParseAs<AssistantMessage>("""{"type":"assistant","message":{"content":[],"model":"m"}}""");
        Assert.Null(msg.Error);
        Assert.Null(msg.ErrorRaw);
    }

    [Fact]
    public void RateLimit_SevenDayOverageIncluded_AndExtraFields()
    {
        var msg = ParseAs<RateLimitEvent>($$$"""
            {"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1767225600,
             "rateLimitType":"seven_day_overage_included","utilization":0.91,"overageStatus":"allowed",
             "isUsingOverage":true,"overageInUse":false,"surpassedThreshold":0.9,"limitScope":"group_pool",
             "errorCode":"credits_required","canUserPurchaseCredits":true,"hasChargeableSavedPaymentMethod":false},{{{Ids}}}}
            """);
        var info = msg.RateLimitInfo;
        Assert.Equal(RateLimitType.SevenDayOverageIncluded, info.RateLimitType);
        Assert.Equal("seven_day_overage_included", info.RateLimitType!.Value.ToJsonString());
        Assert.Equal("seven_day_overage_included", info.RateLimitTypeRaw);
        Assert.True(info.IsUsingOverage);
        Assert.False(info.OverageInUse);
        Assert.Equal(0.9, info.SurpassedThreshold);
        Assert.Equal("group_pool", info.LimitScope);
        Assert.Equal("credits_required", info.ErrorCode);
        Assert.True(info.CanUserPurchaseCredits);
        Assert.False(info.HasChargeableSavedPaymentMethod);
    }

    [Fact]
    public void RateLimit_UnknownType_IsNullButRawPreserved()
    {
        var msg = ParseAs<RateLimitEvent>($$$"""
            {"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"thirty_day"},{{{Ids}}}}
            """);
        Assert.Null(msg.RateLimitInfo.RateLimitType);
        Assert.Equal("thirty_day", msg.RateLimitInfo.RateLimitTypeRaw);
    }

    #endregion

    #region Content blocks

    [Fact]
    public void RedactedThinking_IsParsed()
    {
        var msg = ParseAs<AssistantMessage>("""
            {"type":"assistant","message":{"model":"m","content":[{"type":"redacted_thinking","data":"EuYBCkQYAiJA"}]}}
            """);
        Assert.Equal("EuYBCkQYAiJA", Assert.IsType<RedactedThinkingBlock>(Assert.Single(msg.Content)).Data);
    }

    [Theory]
    [InlineData("web_search_tool_result")]
    [InlineData("web_fetch_tool_result")]
    [InlineData("code_execution_tool_result")]
    [InlineData("bash_code_execution_tool_result")]
    [InlineData("text_editor_code_execution_tool_result")]
    [InlineData("tool_search_tool_result")]
    [InlineData("advisor_tool_result")]
    public void ServerToolResultBlocks_MapToServerToolResultBlock(string type)
    {
        var msg = ParseAs<AssistantMessage>($$$"""
            {"type":"assistant","message":{"model":"m","content":[
              {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"q"}},
              {"type":"{{{type}}}","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://x","title":"X"}]}
            ]}}
            """);
        Assert.Equal(2, msg.Content.Count);
        var result = Assert.IsType<ServerToolResultBlock>(msg.Content[1]);
        Assert.Equal("srvtoolu_1", result.ToolUseId);
        Assert.Equal(type, result.ResultType);
        Assert.Equal("https://x", result.Content[0].GetProperty("url").GetString());
    }

    [Fact]
    public void ServerToolResult_ErrorContentObject_IsKept()
    {
        var msg = ParseAs<AssistantMessage>("""
            {"type":"assistant","message":{"model":"m","content":[
              {"type":"web_fetch_tool_result","tool_use_id":"s1","content":{"type":"web_fetch_tool_error","error_code":"url_not_accessible"}}]}}
            """);
        var block = Assert.IsType<ServerToolResultBlock>(Assert.Single(msg.Content));
        Assert.Equal("url_not_accessible", block.Content.GetProperty("error_code").GetString());
    }

    [Theory]
    [InlineData("""{"type":"mcp_tool_use","id":"mcptoolu_1","name":"echo","server_name":"srv","input":{}}""", "mcp_tool_use")]
    [InlineData("""{"type":"mcp_tool_result","tool_use_id":"mcptoolu_1","is_error":false,"content":[{"type":"text","text":"ok"}]}""", "mcp_tool_result")]
    [InlineData("""{"type":"container_upload","file_id":"file_1"}""", "container_upload")]
    // Known server-result type without its required members falls back to raw instead of throwing.
    [InlineData("""{"type":"web_search_tool_result","tool_use_id":"x"}""", "web_search_tool_result")]
    [InlineData("""{"type":"redacted_thinking"}""", "redacted_thinking")]
    public void UnmodelledBlocks_AreKeptAsRawContentBlock(string block, string type)
    {
        var msg = ParseAs<AssistantMessage>($$$"""{"type":"assistant","message":{"model":"m","content":[{{{block}}},{"type":"text","text":"after"}]}}""");
        Assert.Equal(2, msg.Content.Count);
        var raw = Assert.IsType<RawContentBlock>(msg.Content[0]);
        Assert.Equal(type, raw.Type);
        Assert.True(JsonElement.DeepEquals(J(block), raw.Raw));
        Assert.Equal("after", Assert.IsType<TextBlock>(msg.Content[1]).Text);
    }

    #endregion

    #region Extra fields on existing messages

    [Fact]
    public void Assistant_TsOnlyFields()
    {
        var msg = ParseAs<AssistantMessage>("""
            {"type":"assistant","message":{"id":"msg_1","model":"claude-opus","content":[],"stop_reason":"stop_sequence","stop_sequence":"###"},
             "parent_tool_use_id":null,"uuid":"u","session_id":"s","request_id":"req_1",
             "user_message_uuid":"um1","user_message_uuids":["um1","um2"],"resume_reason":"interrupted",
             "resumed_from_incomplete_thinking":true,"supersedes":["old1"],"aborted":true,
             "subagent_type":"Explore","task_description":"find files","timestamp":"2026-09-28T12:00:00Z",
             "context_usage":{"model":"claude-opus","total_tokens":5000,"raw_max_tokens":200000,"percentage":2.5,
               "over_limit":{"tokens_over":10,"kind":"compaction_window"},
               "categories":[{"name":"Messages","tokens":4000,"kind":"used"},{"name":"Free","tokens":195000,"kind":"free"}],
               "mcp_tools":[{"name":"echo","server_name":"srv","tokens":50}],
               "memory_files":[{"path":"/w/CLAUDE.md","type":"Project","tokens":300}],
               "agents":[{"agent_type":"Explore","source":"built-in","tokens":120}],
               "skills":[{"name":"pdf","source":"plugin","plugin_name":"docs","tokens":80}]},
             "usage_report":{"session":{"total_cost_usd":0.42,"total_api_duration_ms":1200,"total_duration_ms":3400,
                 "total_lines_added":10,"total_lines_removed":2,
                 "model_usage":{"claude-opus":{"inputTokens":10,"outputTokens":20,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,
                   "webSearchRequests":0,"costUSD":0.42,"contextWindow":200000,"maxOutputTokens":32000,"thinkingTokens":7,"costBasis":"list"}}},
               "rate_limits":{"limits":[{"kind":"session","group":"all","percent":40.5,"resets_at":null,
                   "scope":{"model":{"display_name":"Opus"},"surface":null},"severity":"normal","is_active":true}],
                 "extra_usage":{"is_enabled":true,"monthly_limit":100,"used_credits":5.5,"utilization":0.055,"currency":"USD"}}}}
            """);
        Assert.Equal("req_1", msg.RequestId);
        Assert.Equal("um1", msg.UserMessageUuid);
        Assert.Equal(["um1", "um2"], msg.UserMessageUuids);
        Assert.Equal("interrupted", msg.ResumeReason);
        Assert.True(msg.ResumedFromIncompleteThinking);
        Assert.Equal(["old1"], msg.Supersedes);
        Assert.True(msg.Aborted);
        Assert.Equal("Explore", msg.SubagentType);
        Assert.Equal("find files", msg.TaskDescription);
        Assert.Equal("2026-09-28T12:00:00Z", msg.Timestamp);
        Assert.Equal("###", msg.StopSequence);

        var cu = Assert.IsType<SdkContextUsage>(msg.ContextUsage);
        Assert.Equal(("claude-opus", 5000L, 200000L, 2.5), (cu.Model, cu.TotalTokens, cu.RawMaxTokens, cu.Percentage));
        Assert.Equal(new SdkContextOverLimit(10, "compaction_window"), cu.OverLimit);
        Assert.Equal(new SdkContextUsageCategory("Free", 195000, "free"), cu.Categories[1]);
        Assert.Equal(new SdkContextMcpTool("echo", "srv", 50), Assert.Single(cu.McpTools));
        Assert.Equal(new SdkContextMemoryFile("/w/CLAUDE.md", "Project", 300), Assert.Single(cu.MemoryFiles));
        Assert.Equal(new SdkContextAgent("Explore", "built-in", 120), Assert.Single(cu.Agents));
        Assert.Equal(new SdkContextSkill("pdf", "plugin", 80, "docs"), Assert.Single(cu.Skills!));

        var report = Assert.IsType<SdkUsageReport>(msg.UsageReport);
        Assert.Equal(0.42, report.Session!.TotalCostUsd);
        Assert.Equal(10L, report.Session.TotalLinesAdded);
        var mu = report.Session.ModelUsage["claude-opus"];
        Assert.Equal((7, "list"), (mu.ThinkingTokens, mu.CostBasis));
        var limit = Assert.Single(report.RateLimits!.Limits!);
        Assert.Equal(("session", "all", 40.5, null, "Opus", null, "normal", true),
            (limit.Kind, limit.Group, limit.Percent, limit.ResetsAt, limit.ScopeModelDisplayName, limit.ScopeSurfaceDisplayName, limit.Severity, limit.IsActive));
        Assert.Equal("USD", report.RateLimits.ExtraUsage!.Currency);
        Assert.Equal(5.5, report.RateLimits.ExtraUsage.UsedCredits);
    }

    [Fact]
    public void UsageReport_NullRateLimits()
    {
        var r = SdkUsageReport.Parse(J("""{"session":{"total_cost_usd":1,"total_api_duration_ms":0,"total_duration_ms":0,"total_lines_added":0,"total_lines_removed":0,"model_usage":{}},"rate_limits":null}"""));
        Assert.NotNull(r!.Session);
        Assert.Null(r.RateLimits);
        Assert.Null(SdkUsageReport.Parse(J("null")));
    }

    [Fact]
    public void User_TsOnlyFields_IncludingReplay()
    {
        var msg = ParseAs<UserMessage>("""
            {"type":"user","message":{"role":"user","content":"again"},"parent_tool_use_id":null,
             "isSynthetic":true,"isReplay":true,"priority":"next","timestamp":"2026-01-01T00:00:00Z","shouldQuery":false,
             "client_composed":true,"uuid":"u","session_id":"s","file_attachments":[{"name":"a.png"}],
             "pasted_content":[[{"type":"text","text":"p"}]],"inline_pastes":["#1"],"subagent_type":"Plan","task_description":"plan it"}
            """);
        Assert.Equal("s", msg.SessionId);
        Assert.True(msg.IsSynthetic);
        Assert.True(msg.IsReplay);
        Assert.Equal("next", msg.Priority);
        Assert.Equal("2026-01-01T00:00:00Z", msg.Timestamp);
        Assert.False(msg.ShouldQuery);
        Assert.True(msg.ClientComposed);
        Assert.Equal("a.png", msg.FileAttachments!.Value[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Array, msg.PastedContent!.Value.ValueKind);
        Assert.Equal(["#1"], msg.InlinePastes);
        Assert.Equal(("Plan", "plan it"), (msg.SubagentType, msg.TaskDescription));
    }

    [Fact]
    public void User_LiveFrame_HasNoReplayFlag()
    {
        var msg = ParseAs<UserMessage>("""{"type":"user","message":{"content":"hi"}}""");
        Assert.Null(msg.IsReplay);
        Assert.Null(msg.SessionId);
    }

    [Fact]
    public void Result_TsOnlyFields_AndTypedPermissionDenials()
    {
        var msg = ParseAs<ResultMessage>("""
            {"type":"result","subtype":"error_during_execution","duration_ms":10,"duration_api_ms":5,"is_error":true,"num_turns":0,
             "session_id":"s","stop_reason":null,"total_cost_usd":0,"usage":{},"modelUsage":{},"errors":["boom"],
             "permission_denials":[{"tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"rm -rf /"}},"garbage"],
             "queued_turn_count":2,"result_index":1,"fast_mode_state":"cooldown","fast_mode_disabled_reason":"network_error",
             "startup_failure_reason":"cwd_unavailable","user_message_uuid":"um","user_message_uuids":["um"],"resume_reason":"r",
             "local_command":"/cost","ttft_ms":321.5,"ttft_stream_ms":300,"time_to_request_ms":20,"terminal_reason":"api_error","uuid":"u"}
            """);
        Assert.Equal(2, msg.QueuedTurnCount);
        Assert.Equal(1, msg.ResultIndex);
        Assert.Equal(FastModeStates.Cooldown, msg.FastModeState);
        Assert.Equal(FastModeDisabledReasons.NetworkError, msg.FastModeDisabledReason);
        Assert.Equal(StartupFailureReasons.CwdUnavailable, msg.StartupFailureReason);
        Assert.Equal(TerminalReasons.ApiError, msg.TerminalReason);
        Assert.Equal(("um", "r", "/cost"), (msg.UserMessageUuid, msg.ResumeReason, msg.LocalCommand));
        Assert.Equal(["um"], msg.UserMessageUuids);
        Assert.Equal((321.5, 300d, 20d), (msg.TtftMs!.Value, msg.TtftStreamMs!.Value, msg.TimeToRequestMs!.Value));

        var denial = Assert.Single(msg.GetPermissionDenials());
        Assert.Equal(("Bash", "t1"), (denial.ToolName, denial.ToolUseId));
        Assert.Equal("rm -rf /", denial.ToolInput.GetProperty("command").GetString());
    }

    [Fact]
    public void Result_NoPermissionDenials_GivesEmptyList()
    {
        var msg = ParseAs<ResultMessage>("""{"type":"result","subtype":"success","duration_ms":1,"duration_api_ms":1,"is_error":false,"num_turns":1,"session_id":"s"}""");
        Assert.Empty(msg.GetPermissionDenials());
    }

    [Fact]
    public void ModelUsage_ThinkingTokensAndCostBasis()
    {
        var msg = ParseAs<ResultMessage>("""
            {"type":"result","subtype":"success","duration_ms":1,"duration_api_ms":1,"is_error":false,"num_turns":1,"session_id":"s",
             "modelUsage":{"m":{"inputTokens":1,"outputTokens":2,"thinkingTokens":3,"costBasis":"managed"}}}
            """);
        Assert.Equal((3, "managed"), (msg.ModelUsage!["m"].ThinkingTokens, msg.ModelUsage["m"].CostBasis));
    }

    [Fact]
    public void StreamEvent_TsOnlyFields()
    {
        var msg = ParseAs<StreamEvent>("""
            {"type":"stream_event","uuid":"u","session_id":"s","event":{"type":"message_start"},"parent_tool_use_id":null,
             "ttft_ms":88,"user_message_uuid":"um","user_message_uuids":["um","um2"],"resume_reason":"retry"}
            """);
        Assert.Equal(88d, msg.TtftMs);
        Assert.Equal("um", msg.UserMessageUuid);
        Assert.Equal(["um", "um2"], msg.UserMessageUuids);
        Assert.Equal("retry", msg.ResumeReason);
    }

    [Fact]
    public void ConversationReset_TsOnlyFields()
    {
        var msg = ParseAs<ConversationResetMessage>("""
            {"type":"conversation_reset","new_conversation_id":"c2","uuid":"u","session_id":"s",
             "trigger":"plan_mode_exit","user_message_uuid":"um","timestamp":"2026-09-28T00:00:00Z"}
            """);
        Assert.Equal(("plan_mode_exit", "um", "2026-09-28T00:00:00Z"), (msg.Trigger, msg.UserMessageUuid, msg.Timestamp));
    }

    [Fact]
    public void TaskMessages_TsOnlyFields()
    {
        var started = ParseAs<TaskStartedMessage>($$$"""
            {"type":"system","subtype":"task_started","task_id":"t","description":"d","subagent_type":"Explore","is_backgrounded":true,
             "spawn_depth":2,"task_type":"local_agent","workflow_name":"wf","prompt":"do it","skip_transcript":true,"ambient":false,{{{Ids}}}}
            """);
        Assert.Equal(("Explore", true, 2, "wf", "do it", true, false),
            (started.SubagentType, started.IsBackgrounded, started.SpawnDepth, started.WorkflowName, started.Prompt, started.SkipTranscript, started.Ambient));

        var progress = ParseAs<TaskProgressMessage>($$$"""
            {"type":"system","subtype":"task_progress","task_id":"t","description":"d","subagent_type":"Explore","summary":"halfway",
             "usage":{"total_tokens":1,"tool_uses":2,"duration_ms":3},{{{Ids}}}}
            """);
        Assert.Equal(("Explore", "halfway"), (progress.SubagentType, progress.Summary));

        var note = ParseAs<TaskNotificationMessage>($$$"""
            {"type":"system","subtype":"task_notification","task_id":"t","status":"brand_new","reason":"worker_restart","output_file":"o","summary":"s",
             "resource_links":[{"uri":"file:///x","name":"x","mimeType":"text/plain","size":12,"annotations":{"audience":["user"]}},{"no_uri":true}],
             "skip_transcript":false,"ambient":true,{{{Ids}}}}
            """);
        Assert.Equal(TaskNotificationStatus.Unknown, note.Status);
        Assert.Equal("brand_new", note.StatusRaw);
        Assert.Equal("worker_restart", note.Reason);
        var link = Assert.Single(note.ResourceLinks!);
        Assert.Equal(("file:///x", "x", "text/plain", 12L), (link.Uri, link.Name, link.MimeType, link.Size!.Value));
        Assert.Equal("user", link.Annotations!.Value.GetProperty("audience")[0].GetString());
        Assert.Equal((false, true), (note.SkipTranscript!.Value, note.Ambient!.Value));
    }

    [Fact]
    public void TaskUpdated_TypedPatch()
    {
        var msg = ParseAs<TaskUpdatedMessage>($$$"""
            {"type":"system","subtype":"task_updated","task_id":"t",
             "patch":{"status":"paused","description":"new","end_time":1767225600000,"total_paused_ms":250,"error":"e","is_backgrounded":true},{{{Ids}}}}
            """);
        var patch = Assert.IsType<TaskUpdatedPatch>(msg.TypedPatch);
        Assert.Equal(("paused", "new", 1767225600000d, 250d, "e", true),
            (patch.Status, patch.Description, patch.EndTime!.Value, patch.TotalPausedMs!.Value, patch.Error, patch.IsBackgrounded!.Value));
    }

    [Fact]
    public void McpServerStatus_SourceAndToolMeta()
    {
        var status = JsonSerializer.Deserialize("""
            {"mcpServers":[{"name":"srv","status":"connected","source":"project","tools":[{"name":"echo","_meta":{"anthropic/x":1}}]}]}
            """, SdkJsonContext.Default.McpStatusResponse)!;
        var server = Assert.Single(status.McpServers);
        Assert.Equal("project", server.Source);
        Assert.Equal(1, Assert.Single(server.Tools!).Meta!.Value.GetProperty("anthropic/x").GetInt32());
    }

    #endregion

    #region Typed system subtypes

    public static TheoryData<string, Type> SystemSubtypes => new()
    {
        { """{"subtype":"init","cwd":"/w","tools":[],"mcp_servers":[],"model":"m","permissionMode":"default","slash_commands":[],"output_style":"default","skills":[],"plugins":[],"apiKeySource":"none","claude_code_version":"2.1.280"}""", typeof(SystemInitMessage) },
        { """{"subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":1}}""", typeof(CompactBoundaryMessage) },
        { """{"subtype":"status","status":null}""", typeof(StatusMessage) },
        { """{"subtype":"api_retry","attempt":1,"max_retries":3,"retry_delay_ms":100,"error_status":null,"error":"overloaded"}""", typeof(ApiRetryMessage) },
        { """{"subtype":"control_request_progress","request_id":"r","status":"started"}""", typeof(ControlRequestProgressMessage) },
        { """{"subtype":"model_refusal_fallback","trigger":"refusal","direction":"retry","original_model":"a","fallback_model":"b","request_id":null,"content":"c"}""", typeof(ModelRefusalFallbackMessage) },
        { """{"subtype":"model_refusal_no_fallback","original_model":"a","request_id":null,"content":"c"}""", typeof(ModelRefusalNoFallbackMessage) },
        { """{"subtype":"local_command_output","content":"c"}""", typeof(LocalCommandOutputMessage) },
        { """{"subtype":"hook_started","hook_id":"h","hook_name":"n","hook_event":"PreToolUse"}""", typeof(HookEventMessage) },
        { """{"subtype":"hook_progress","hook_id":"h","hook_name":"n","hook_event":"PreToolUse","stdout":"","stderr":"","output":""}""", typeof(HookProgressMessage) },
        { """{"subtype":"hook_response","hook_id":"h","hook_name":"n","hook_event":"PreToolUse","output":"","stdout":"","stderr":"","outcome":"success"}""", typeof(HookEventMessage) },
        { """{"subtype":"plugin_install","status":"started"}""", typeof(PluginInstallMessage) },
        { """{"subtype":"background_tasks_changed","tasks":[]}""", typeof(BackgroundTasksChangedMessage) },
        { """{"subtype":"thinking_tokens","estimated_tokens":1,"estimated_tokens_delta":1}""", typeof(ThinkingTokensMessage) },
        { """{"subtype":"session_state_changed","state":"idle"}""", typeof(SessionStateChangedMessage) },
        { """{"subtype":"worker_shutting_down","reason":"r"}""", typeof(WorkerShuttingDownMessage) },
        { """{"subtype":"commands_changed","commands":[]}""", typeof(CommandsChangedMessage) },
        { """{"subtype":"notification","key":"k","text":"t","priority":"low"}""", typeof(NotificationMessage) },
        { """{"subtype":"files_persisted","files":[],"failed":[],"processed_at":"now"}""", typeof(FilesPersistedMessage) },
        { """{"subtype":"memory_recall","mode":"select","memories":[]}""", typeof(MemoryRecallMessage) },
        { """{"subtype":"elicitation_complete","mcp_server_name":"m","elicitation_id":"e"}""", typeof(ElicitationCompleteMessage) },
        { """{"subtype":"permission_denied","tool_name":"Bash","tool_use_id":"t","message":"no"}""", typeof(PermissionDeniedMessage) },
        { """{"subtype":"informational","content":"c","level":"info"}""", typeof(InformationalMessage) },
        { """{"subtype":"never_seen_before","x":1}""", typeof(SystemMessage) },
        // Lenient: a typed subtype with missing fields still parses.
        { """{"subtype":"notification"}""", typeof(NotificationMessage) },
        { """{"subtype":"init"}""", typeof(SystemInitMessage) }
    };

    [Theory]
    [MemberData(nameof(SystemSubtypes))]
    public void SystemSubtypes_MapToTypedSubclasses_AndKeepData(string body, Type expected)
    {
        var json = "{\"type\":\"system\"," + Ids + "," + body[1..];
        var msg = Assert.IsAssignableFrom<SystemMessage>(Parse(json));
        Assert.Equal(expected, msg.GetType());
        Assert.True(JsonElement.DeepEquals(J(json), msg.Data));
        Assert.True(JsonElement.DeepEquals(J(json), msg.Raw));
    }

    [Fact]
    public void SystemInit_AllFields()
    {
        var msg = ParseAs<SystemInitMessage>($$$"""
            {"type":"system","subtype":"init","agents":["Explore","Plan"],"apiKeySource":"oauth","betas":["b1"],
             "claude_code_version":"2.1.280","cwd":"/work","tools":["Bash","Read"],
             "mcp_servers":[{"name":"srv","status":"connected","source":"project"},{"name":"other","status":"failed"}],
             "model":"claude-opus","permissionMode":"acceptEdits","slash_commands":["/cost"],"terminal_slash_commands":["/exit"],
             "output_style":"default","skills":["pdf"],"plugins":[{"name":"p","path":"/p","version":"1.0"}],
             "plugin_errors":[{"plugin":"bad","type":"load","message":"nope","path":"/bad"}],
             "fast_mode_state":"off","fast_mode_disabled_reason":"preference","effort":"xhigh","view_mode":"focus",
             "capabilities":["goals"],{{{Ids}}}}
            """);
        Assert.Equal(["Explore", "Plan"], msg.Agents);
        Assert.Equal(ApiKeySources.OAuth, msg.ApiKeySource);
        Assert.Equal(["b1"], msg.Betas);
        Assert.Equal(("2.1.280", "/work", "claude-opus", "acceptEdits"), (msg.ClaudeCodeVersion, msg.Cwd, msg.Model, msg.PermissionMode));
        Assert.Equal(["Bash", "Read"], msg.Tools);
        Assert.Equal([new InitMcpServer("srv", "connected", "project"), new InitMcpServer("other", "failed")], msg.McpServers);
        Assert.Equal(["/cost"], msg.SlashCommands);
        Assert.Equal(["/exit"], msg.TerminalSlashCommands);
        Assert.Equal("default", msg.OutputStyle);
        Assert.Equal(["pdf"], msg.Skills);
        Assert.Equal(new InitPlugin("p", "/p", "1.0"), Assert.Single(msg.Plugins));
        Assert.Equal(new InitPluginError("bad", "load", "nope", "/bad"), Assert.Single(msg.PluginErrors!));
        Assert.Equal((FastModeStates.Off, FastModeDisabledReasons.Preference, "xhigh", "focus"),
            (msg.FastModeState, msg.FastModeDisabledReason, msg.Effort, msg.ViewMode));
        Assert.Equal(["goals"], msg.Capabilities);
        Assert.Equal(("u-1", "s-1"), (msg.Uuid, msg.SessionId));
    }

    [Fact]
    public void CompactBoundary_AllFields()
    {
        var msg = ParseAs<CompactBoundaryMessage>($$$"""
            {"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":150000,"post_tokens":20000,
             "duration_ms":3210,"preserved_segment":{"head_uuid":"h","anchor_uuid":"a","tail_uuid":"t"},
             "preserved_messages":{"anchor_uuid":"a","uuids":["m1","m2"]}},{{{Ids}}}}
            """);
        var m = msg.CompactMetadata!;
        Assert.Equal(("manual", 150000L, 20000L, 3210d), (m.Trigger, m.PreTokens, m.PostTokens!.Value, m.DurationMs!.Value));
        Assert.Equal(new CompactPreservedSegment("h", "a", "t"), m.PreservedSegment);
        Assert.Equal("a", m.PreservedMessages!.AnchorUuid);
        Assert.Equal(["m1", "m2"], m.PreservedMessages.Uuids);
    }

    [Fact]
    public void Status_ApiRetry_ControlRequestProgress()
    {
        var status = ParseAs<StatusMessage>($$$"""
            {"type":"system","subtype":"status","status":"compacting","permissionMode":"plan","compact_result":"failed","compact_error":"too big",{{{Ids}}}}
            """);
        Assert.Equal((SdkStatuses.Compacting, "plan", "failed", "too big"),
            (status.Status, status.PermissionMode, status.CompactResult, status.CompactError));

        var retry = ParseAs<ApiRetryMessage>($$$"""
            {"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":2000,"error_status":529,
             "error":"overloaded","no_response":{"waited_ms":30000,"retry_wait_ms":1000},{{{Ids}}}}
            """);
        Assert.Equal((2, 10, 2000d, 529), (retry.Attempt, retry.MaxRetries, retry.RetryDelayMs, retry.ErrorStatus!.Value));
        Assert.Equal((AssistantMessageError.Overloaded, "overloaded"), (retry.Error!.Value, retry.ErrorRaw));
        Assert.Equal(new ApiRetryNoResponse(30000, 1000), retry.NoResponse);

        var weird = ParseAs<ApiRetryMessage>("""{"type":"system","subtype":"api_retry","error":"space_weather"}""");
        Assert.Equal((AssistantMessageError.Unknown, "space_weather"), (weird.Error!.Value, weird.ErrorRaw));

        var progress = ParseAs<ControlRequestProgressMessage>($$$"""
            {"type":"system","subtype":"control_request_progress","request_id":"req_9","status":"api_retry","attempt":1,
             "max_retries":3,"retry_delay_ms":500,"error_status":null,{{{Ids}}}}
            """);
        Assert.Equal(("req_9", "api_retry", 1, 3, 500d), (progress.RequestId, progress.Status, progress.Attempt!.Value, progress.MaxRetries!.Value, progress.RetryDelayMs!.Value));
        Assert.Null(progress.ErrorStatus);
    }

    [Fact]
    public void ModelRefusalMessages()
    {
        var fb = ParseAs<ModelRefusalFallbackMessage>($$$"""
            {"type":"system","subtype":"model_refusal_fallback","trigger":"refusal","direction":"sticky","scope":"session",
             "original_model":"opus","fallback_model":"sonnet","request_id":"req","api_refusal_category":"cyber",
             "api_refusal_explanation":"why","retracted_message_uuids":["r1"],"refused_user_message_uuid":"um","content":"Switched",{{{Ids}}}}
            """);
        Assert.Equal(("refusal", "sticky", "session", "opus", "sonnet", "req"),
            (fb.Trigger, fb.Direction, fb.Scope, fb.OriginalModel, fb.FallbackModel, fb.RequestId));
        Assert.Equal(("cyber", "why", "um", "Switched"), (fb.ApiRefusalCategory, fb.ApiRefusalExplanation, fb.RefusedUserMessageUuid, fb.Content));
        Assert.Equal(["r1"], fb.RetractedMessageUuids);

        var nofb = ParseAs<ModelRefusalNoFallbackMessage>($$$"""
            {"type":"system","subtype":"model_refusal_no_fallback","original_model":"opus","request_id":null,
             "api_refusal_category":null,"refused_user_message_uuid":"um","content":"Refused",{{{Ids}}}}
            """);
        Assert.Equal(("opus", null, null, "um", "Refused"),
            (nofb.OriginalModel, nofb.RequestId, nofb.ApiRefusalCategory, nofb.RefusedUserMessageUuid, nofb.Content));
    }

    [Fact]
    public void Hook_LifecycleMessages_CarryHookFields()
    {
        var progress = ParseAs<HookProgressMessage>($$$"""
            {"type":"system","subtype":"hook_progress","hook_id":"h1","hook_name":"lint","hook_event":"PostToolUse",
             "stdout":"ok","stderr":"warn","output":"ok\nwarn",{{{Ids}}}}
            """);
        Assert.Equal(("hook_progress", "PostToolUse", "h1", "lint", "ok", "warn", "ok\nwarn"),
            (progress.Subtype, progress.HookEventName, progress.HookId, progress.HookName, progress.Stdout, progress.Stderr, progress.Output));
        Assert.IsAssignableFrom<HookEventMessage>(progress);
        Assert.Equal("s-1", progress.SessionId);

        var response = ParseAs<HookEventMessage>($$$"""
            {"type":"system","subtype":"hook_response","hook_id":"h1","hook_name":"lint","hook_event":"PostToolUse",
             "output":"o","stdout":"so","stderr":"se","exit_code":2,"outcome":"error",{{{Ids}}}}
            """);
        Assert.Equal((2, "error", "so", "se", "o"), (response.ExitCode!.Value, response.Outcome, response.Stdout, response.Stderr, response.Output));
    }

    [Fact]
    public void MiscSystemSubtypes_AllFields()
    {
        var local = ParseAs<LocalCommandOutputMessage>("""{"type":"system","subtype":"local_command_output","content":"Total cost: $0"}""");
        Assert.Equal("Total cost: $0", local.Content);

        var plugin = ParseAs<PluginInstallMessage>("""{"type":"system","subtype":"plugin_install","status":"failed","name":"p","error":"e"}""");
        Assert.Equal(("failed", "p", "e"), (plugin.Status, plugin.Name, plugin.Error));

        var bg = ParseAs<BackgroundTasksChangedMessage>("""
            {"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"t","task_type":"shell","description":"npm run dev","ambient":true}]}
            """);
        Assert.Equal(new BackgroundTaskInfo("t", "shell", "npm run dev", true), Assert.Single(bg.Tasks));

        var think = ParseAs<ThinkingTokensMessage>("""{"type":"system","subtype":"thinking_tokens","estimated_tokens":900,"estimated_tokens_delta":50,"user_message_uuid":"um"}""");
        Assert.Equal((900L, 50L, "um"), (think.EstimatedTokens, think.EstimatedTokensDelta, think.UserMessageUuid));

        var state = ParseAs<SessionStateChangedMessage>("""{"type":"system","subtype":"session_state_changed","state":"requires_action"}""");
        Assert.Equal("requires_action", state.State);

        var shutdown = ParseAs<WorkerShuttingDownMessage>("""{"type":"system","subtype":"worker_shutting_down","reason":"idle_timeout"}""");
        Assert.Equal("idle_timeout", shutdown.Reason);

        var commands = ParseAs<CommandsChangedMessage>("""
            {"type":"system","subtype":"commands_changed","commands":[{"name":"review","description":"Review a PR","argumentHint":"<pr>","aliases":["r"],"builtin":false},{"description":"nameless"}]}
            """);
        var cmd = Assert.Single(commands.Commands);
        Assert.Equal(("review", "Review a PR", "<pr>", false), (cmd.Name, cmd.Description, cmd.ArgumentHint, cmd.Builtin!.Value));
        Assert.Equal(["r"], cmd.Aliases);

        var note = ParseAs<NotificationMessage>("""{"type":"system","subtype":"notification","key":"k","text":"Heads up","priority":"immediate","color":"red","timeout_ms":5000}""");
        Assert.Equal(("k", "Heads up", "immediate", "red", 5000d), (note.Key, note.Text, note.Priority, note.Color, note.TimeoutMs!.Value));

        var files = ParseAs<FilesPersistedMessage>("""
            {"type":"system","subtype":"files_persisted","files":[{"filename":"a.txt","file_id":"file_1"}],"failed":[{"filename":"b.bin","error":"too large"}],"processed_at":"2026-09-28T00:00:00Z"}
            """);
        Assert.Equal(new PersistedFile("a.txt", "file_1"), Assert.Single(files.Files));
        Assert.Equal(new FailedPersistedFile("b.bin", "too large"), Assert.Single(files.Failed));
        Assert.Equal("2026-09-28T00:00:00Z", files.ProcessedAt);

        var recall = ParseAs<MemoryRecallMessage>("""
            {"type":"system","subtype":"memory_recall","mode":"synthesize","memories":[{"path":"/m.md","scope":"team","content":"c"},{"path":"/n.md","scope":"personal"}]}
            """);
        Assert.Equal("synthesize", recall.Mode);
        Assert.Equal([new RecalledMemory("/m.md", "team", "c"), new RecalledMemory("/n.md", "personal")], recall.Memories);

        var elicit = ParseAs<ElicitationCompleteMessage>("""{"type":"system","subtype":"elicitation_complete","mcp_server_name":"srv","elicitation_id":"el1"}""");
        Assert.Equal(("srv", "el1"), (elicit.McpServerName, elicit.ElicitationId));

        var denied = ParseAs<PermissionDeniedMessage>("""
            {"type":"system","subtype":"permission_denied","tool_name":"Bash","tool_use_id":"t","agent_id":"a","decision_reason_type":"rule","decision_reason":"deny rule","message":"Denied"}
            """);
        Assert.Equal(("Bash", "t", "a", "rule", "deny rule", "Denied"),
            (denied.ToolName, denied.ToolUseId, denied.AgentId, denied.DecisionReasonType, denied.DecisionReason, denied.Message));

        var info = ParseAs<InformationalMessage>("""{"type":"system","subtype":"informational","content":"FYI","level":"warning","tool_use_id":"t","prevent_continuation":true}""");
        Assert.Equal(("FYI", "warning", "t", true), (info.Content, info.Level, info.ToolUseId, info.PreventContinuation!.Value));
    }

    #endregion

    #region Info types

    private const string InitializeResponseJson = """
        {"commands":[{"name":"compact","description":"Compact the conversation","argumentHint":"<instructions>","builtin":true},
                     {"name":"review","description":"Review","argumentHint":"","aliases":["rv"]},
                     {"description":"missing name is skipped"}, 7],
         "agents":[{"name":"Explore","description":"Search code","model":"haiku"},{"name":"Plan","description":"Plan"}],
         "output_style":"default","available_output_styles":["default","Explanatory","Learning"],
         "models":[{"value":"default","resolvedModel":"claude-opus-4-7","displayName":"Default","description":"Recommended",
                    "supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"],
                    "supportsAdaptiveThinking":true,"supportsFastMode":false,"supportsAutoMode":true}],
         "account":{"email":"a@example.com","organization":"Org","subscriptionType":"max","tokenSource":"claude.ai",
                    "apiKeySource":"/login managed key","apiProvider":"firstParty"},
         "hooks_applied":true,"plugins_applied":false,"fast_mode_state":"on","fast_mode_disabled_reason":"pending","future":1}
        """;

    [Fact]
    public void InitializeResponse_ParsesEverything()
    {
        var r = InitializeResponse.Parse(J(InitializeResponseJson));

        Assert.Equal(2, r.Commands.Count);
        Assert.Equal(("compact", "Compact the conversation", "<instructions>", true),
            (r.Commands[0].Name, r.Commands[0].Description, r.Commands[0].ArgumentHint, r.Commands[0].Builtin!.Value));
        Assert.Equal(["rv"], r.Commands[1].Aliases);
        Assert.Null(r.Commands[1].Builtin);

        Assert.Equal(2, r.Agents.Count);
        Assert.Equal(("Explore", "Search code", "haiku"), (r.Agents[0].Name, r.Agents[0].Description, r.Agents[0].Model));
        Assert.Null(r.Agents[1].Model);

        Assert.Equal("default", r.OutputStyle);
        Assert.Equal(["default", "Explanatory", "Learning"], r.AvailableOutputStyles);

        var model = Assert.Single(r.Models);
        Assert.Equal(("default", "claude-opus-4-7", "Default", "Recommended"), (model.Value, model.ResolvedModel, model.DisplayName, model.Description));
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], model.SupportedEffortLevels);
        Assert.Equal((true, true, false, true),
            (model.SupportsEffort!.Value, model.SupportsAdaptiveThinking!.Value, model.SupportsFastMode!.Value, model.SupportsAutoMode!.Value));

        var acct = r.Account!;
        Assert.Equal(("a@example.com", "Org", "max", "claude.ai", ApiKeySources.LoginManagedKey, "firstParty"),
            (acct.Email, acct.Organization, acct.SubscriptionType, acct.TokenSource, acct.ApiKeySource, acct.ApiProvider));

        Assert.Equal((true, false, FastModeStates.On, FastModeDisabledReasons.Pending),
            (r.HooksApplied!.Value, r.PluginsApplied!.Value, r.FastModeState, r.FastModeDisabledReason));
        Assert.Equal(1, r.Raw.GetProperty("future").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"commands":"nope","agents":null,"models":{},"account":"x"}""")]
    [InlineData("null")]
    [InlineData("[]")]
    public void InitializeResponse_IsLenient(string json)
    {
        var r = InitializeResponse.Parse(J(json));
        Assert.Empty(r.Commands);
        Assert.Empty(r.Agents);
        Assert.Empty(r.Models);
        Assert.Null(r.Account);
    }

    [Fact]
    public void InfoTypes_ParseReturnsNullForNonObjects()
    {
        Assert.Null(SlashCommand.Parse(J("1")));
        Assert.Null(AgentInfo.Parse(J("{}")));
        Assert.Null(ModelInfo.Parse(J("{\"displayName\":\"x\"}")));
        Assert.Null(AccountInfo.Parse(J("\"x\"")));
        Assert.Null(SdkContextUsage.Parse(J("[]")));
    }

    [Fact]
    public void InfoTypes_RoundTripThroughSourceGeneratedMetadata()
    {
        var parsed = InitializeResponse.Parse(J(InitializeResponseJson));
        var json = JsonSerializer.Serialize(parsed, SdkJsonContext.Default.InitializeResponse);
        var back = JsonSerializer.Deserialize(json, SdkJsonContext.Default.InitializeResponse)!;

        Assert.Equal(parsed.Commands, back.Commands, (a, b) => a.Name == b.Name && a.ArgumentHint == b.ArgumentHint);
        Assert.Equal(parsed.Account, back.Account);
        Assert.Equal(parsed.Models[0].SupportedEffortLevels, back.Models[0].SupportedEffortLevels);
        Assert.Equal("Explanatory", J(json).GetProperty("available_output_styles")[1].GetString());
        Assert.Equal("<instructions>", J(json).GetProperty("commands")[0].GetProperty("argumentHint").GetString());
    }

    #endregion
}
