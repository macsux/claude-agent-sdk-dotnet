// TypeScript SDK (0.3.283) parity: all 33 hook events, the string escape
// hatch, typed hook inputs (HookInput.Parse), hook-specific outputs,
// terminalSequence, typed PermissionRequest decisions and cliArg destination.

using System.Text.Json;
using Claude.AgentSdk.Builders;
using Claude.AgentSdk.Internal;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class TsParityHooksTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // TS sdk.d.ts HOOK_EVENTS, in TS order.
    private static readonly string[] TsHookEvents =
    [
        "PreToolUse", "PostToolUse", "PostToolUseFailure", "PostToolBatch", "Notification", "UserPromptSubmit",
        "UserPromptExpansion", "SessionStart", "SessionEnd", "Stop", "StopFailure", "SubagentStart", "SubagentStop",
        "PreCompact", "PostCompact", "PreModelSwitch", "PostModelSwitch", "PermissionRequest", "PermissionDenied",
        "Setup", "TeammateIdle", "TaskCreated", "TaskCompleted", "Elicitation", "ElicitationResult", "ConfigChange",
        "WorktreeCreate", "WorktreeRemove", "InstructionsLoaded", "CwdChanged", "FileChanged", "DirectoryAdded",
        "MessageDisplay"
    ];

    #region HookEvent names

    [Fact]
    public void HookEvent_CoversExactlyTheTsHookEvents()
    {
        var names = Enum.GetValues<HookEvent>().Select(e => e.ToJsonString()).ToHashSet();
        Assert.Equal(TsHookEvents.ToHashSet(), names);
        Assert.Equal(TsHookEvents.ToHashSet(), HookEventNames.Known.ToHashSet());
    }

    [Fact]
    public void HookEvent_WireNameEqualsMemberName_ForEveryMember()
    {
        foreach (var e in Enum.GetValues<HookEvent>())
        {
            Assert.Equal(e.ToString(), e.ToWireName());
            Assert.True(HookEventNames.IsKnown(e));
            Assert.True(HookEventNames.TryParseKnown(e.ToString(), out var back));
            Assert.Equal(e, back);
            Assert.Equal(e, HookEventNames.Parse(e.ToString()));
        }
    }

    [Fact]
    public void HookEvent_OriginalNumericValuesAreStable()
    {
        Assert.Equal(0, (int)HookEvent.PreToolUse);
        Assert.Equal(9, (int)HookEvent.PermissionRequest);
    }

    [Fact]
    public void HookEventNames_Parse_RegistersCustomEvents()
    {
        var a = HookEventNames.Parse("SomeFutureEvent_" + nameof(HookEventNames_Parse_RegistersCustomEvents));
        var b = HookEventNames.Parse("SomeFutureEvent_" + nameof(HookEventNames_Parse_RegistersCustomEvents));
        var c = HookEventNames.Parse("AnotherFutureEvent_" + nameof(HookEventNames_Parse_RegistersCustomEvents));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True((int)a >= HookEventNames.CustomEventBase);
        Assert.False(HookEventNames.IsKnown(a));
        Assert.Equal("SomeFutureEvent_" + nameof(HookEventNames_Parse_RegistersCustomEvents), a.ToWireName());
        Assert.Equal(a.ToWireName(), a.ToJsonString());
        Assert.False(HookEventNames.TryParseKnown(a.ToWireName(), out _));
    }

    [Fact]
    public void HookEventNames_Parse_IsCaseSensitive_AndRejectsEmpty()
    {
        Assert.NotEqual(HookEvent.Stop, HookEventNames.Parse("stop"));
        Assert.Throws<ArgumentException>(() => HookEventNames.Parse(""));
    }

    [Fact]
    public async Task Initialize_SendsWireNames_ForNewAndCustomEvents()
    {
        HookCallback noop = (_, _, _, _) => Task.FromResult(new HookOutput());
        var hooks = new HooksBuilder()
            .On(HookEvent.SessionEnd, null, noop)
            .On("MessageDisplay", null, noop)
            .On("FutureHookEvent_Init", "matcher-x", noop, timeout: 5)
            .Build();
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, new ClaudeAgentOptions { Hooks = hooks });
        await handler.StartAsync();
        await handler.InitializeAsync();

        var init = transport.Written.First(w => w.GetProperty("type").GetString() == "control_request" &&
                                                w.GetProperty("request").GetProperty("subtype").GetString() == "initialize");
        var hooksJson = init.GetProperty("request").GetProperty("hooks");
        Assert.Equal(["FutureHookEvent_Init", "MessageDisplay", "SessionEnd"],
            hooksJson.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var future = Assert.Single(hooksJson.GetProperty("FutureHookEvent_Init").EnumerateArray());
        Assert.Equal("matcher-x", future.GetProperty("matcher").GetString());
        Assert.Equal(5, future.GetProperty("timeout").GetDouble());
    }

    [Fact]
    public void HooksBuilder_OnString_KnownNameUsesEnumMember()
    {
        HookCallback noop = (_, _, _, _) => Task.FromResult(new HookOutput());
        var built = new HooksBuilder().On("CwdChanged", null, noop).On(HookEvent.FileChanged, "*.cs", noop, noop).Build();
        Assert.True(built.ContainsKey(HookEvent.CwdChanged));
        Assert.Equal(2, Assert.Single(built[HookEvent.FileChanged]).Hooks!.Count);
    }

    #endregion

    #region HookInput.Parse

    private const string Base =
        "\"session_id\":\"sess-1\",\"transcript_path\":\"/t.jsonl\",\"cwd\":\"/w\",\"permission_mode\":\"acceptEdits\"," +
        "\"prompt_id\":\"p-1\",\"agent_id\":\"agent-1\",\"agent_type\":\"general-purpose\",\"effort\":{\"level\":\"high\"}";

    private static BaseHookInput ParseHook(string eventName, string fields) =>
        HookInput.Parse(J("{" + Base + ",\"hook_event_name\":\"" + eventName + "\"" + (fields.Length > 0 ? "," + fields : "") + "}"));

    public static TheoryData<string, Type, string> AllEvents => new()
    {
        { "PreToolUse", typeof(PreToolUseHookInput), """{"tool_name":"Bash","tool_input":{"command":"ls"},"tool_use_id":"t1"}""" },
        { "PostToolUse", typeof(PostToolUseHookInput), """{"tool_name":"Bash","tool_input":{},"tool_response":{"stdout":"x"},"tool_use_id":"t1"}""" },
        { "PostToolUseFailure", typeof(PostToolUseFailureHookInput), """{"tool_name":"Bash","tool_input":{},"tool_use_id":"t1","error":"boom"}""" },
        { "PostToolBatch", typeof(PostToolBatchHookInput), """{"tool_calls":[]}""" },
        { "Notification", typeof(NotificationHookInput), """{"message":"m","notification_type":"idle"}""" },
        { "UserPromptSubmit", typeof(UserPromptSubmitHookInput), """{"prompt":"hi"}""" },
        { "UserPromptExpansion", typeof(UserPromptExpansionHookInput), """{"expansion_type":"slash_command","command_name":"review","command_args":"42","prompt":"p"}""" },
        { "SessionStart", typeof(SessionStartHookInput), """{"source":"startup"}""" },
        { "SessionEnd", typeof(SessionEndHookInput), """{"reason":"logout"}""" },
        { "Stop", typeof(StopHookInput), """{"stop_hook_active":false}""" },
        { "StopFailure", typeof(StopFailureHookInput), """{"error":"rate_limit"}""" },
        { "SubagentStart", typeof(SubagentStartHookInput), """{"agent_id":"a","agent_type":"Explore"}""" },
        { "SubagentStop", typeof(SubagentStopHookInput), """{"stop_hook_active":true,"agent_id":"a","agent_transcript_path":"/a","agent_type":"Explore"}""" },
        { "PreCompact", typeof(PreCompactHookInput), """{"trigger":"auto","custom_instructions":null}""" },
        { "PostCompact", typeof(PostCompactHookInput), """{"trigger":"manual","compact_summary":"sum"}""" },
        { "PreModelSwitch", typeof(PreModelSwitchHookInput), """{"from_model":"a","to_model":"b","requested_model":null,"source":"sdk","context_tokens":1,"prompt_cache_warm":true,"cache_ttl":"5m","estimated_cache_write_usd":0.1,"pricing":"catalog"}""" },
        { "PostModelSwitch", typeof(PostModelSwitchHookInput), """{"from_model":"a","to_model":"b","requested_model":"b","source":"auto","context_tokens":1,"prompt_cache_warm":false,"cache_ttl":"1h","estimated_cache_write_usd":0,"pricing":"default"}""" },
        { "PermissionRequest", typeof(PermissionRequestHookInput), """{"tool_name":"Write","tool_input":{}}""" },
        { "PermissionDenied", typeof(PermissionDeniedHookInput), """{"tool_name":"Write","tool_input":{},"tool_use_id":"t","reason":"rule"}""" },
        { "Setup", typeof(SetupHookInput), """{"trigger":"init"}""" },
        { "TeammateIdle", typeof(TeammateIdleHookInput), """{"teammate_name":"bob","team_name":"core"}""" },
        { "TaskCreated", typeof(TaskCreatedHookInput), """{"task_id":"1","task_subject":"s"}""" },
        { "TaskCompleted", typeof(TaskCompletedHookInput), """{"task_id":"1","task_subject":"s"}""" },
        { "Elicitation", typeof(ElicitationHookInput), """{"mcp_server_name":"srv","message":"Pick one"}""" },
        { "ElicitationResult", typeof(ElicitationResultHookInput), """{"mcp_server_name":"srv","action":"decline"}""" },
        { "ConfigChange", typeof(ConfigChangeHookInput), """{"source":"project_settings"}""" },
        { "WorktreeCreate", typeof(WorktreeCreateHookInput), """{"name":"feature-x"}""" },
        { "WorktreeRemove", typeof(WorktreeRemoveHookInput), """{"worktree_path":"/wt"}""" },
        { "InstructionsLoaded", typeof(InstructionsLoadedHookInput), """{"file_path":"/w/CLAUDE.md","memory_type":"Project","load_reason":"session_start"}""" },
        { "CwdChanged", typeof(CwdChangedHookInput), """{"old_cwd":"/a","new_cwd":"/b"}""" },
        { "FileChanged", typeof(FileChangedHookInput), """{"file_path":"/w/x.cs","event":"change"}""" },
        { "DirectoryAdded", typeof(DirectoryAddedHookInput), """{"directory":"/d","source":"slash_command"}""" },
        { "MessageDisplay", typeof(MessageDisplayHookInput), """{"turn_id":"t","message_id":"m","index":0,"final":false,"delta":"Hel"}""" }
    };

    [Fact]
    public void AllEvents_TheoryCoversEveryTsEvent()
    {
        Assert.Equal(TsHookEvents.Order(), AllEvents.Select(r => (string)r[0]).Order());
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Parse_EveryEvent_ToItsTypedInput_WithBaseFieldsAndRaw(string eventName, Type expected, string fields)
    {
        var input = ParseHook(eventName, fields[1..^1]);

        Assert.Equal(expected, input.GetType());
        Assert.Equal(("sess-1", "/t.jsonl", "/w", "acceptEdits", "p-1"),
            (input.SessionId, input.TranscriptPath, input.Cwd, input.PermissionMode, input.PromptId));
        Assert.Equal(new HookEffort("high"), input.Effort);
        Assert.Equal(eventName, input.Raw.GetProperty("hook_event_name").GetString());
        // hook_event_name property on the typed record matches the wire.
        var nameProp = expected.GetProperty("HookEventName")!;
        Assert.Equal(eventName, nameProp.GetValue(input));
    }

    [Fact]
    public void Parse_BaseAgentFields_OnEveryEvent()
    {
        var input = ParseHook("Setup", "\"trigger\":\"maintenance\"");
        Assert.Equal(("agent-1", "general-purpose"), (input.AgentId, input.AgentType));
    }

    [Fact]
    public void Parse_RealCliPreToolUseInput()
    {
        // Captured from Fixtures/hook_pre_tool_use_deny.jsonl (includes an unmodelled scratchpad_dir key).
        var input = Assert.IsType<PreToolUseHookInput>(HookInput.Parse(J("""
            {"session_id":"1bb0d35f","transcript_path":"/h/.claude/projects/p/1bb0d35f.jsonl","cwd":"/cwd",
             "scratchpad_dir":"/tmp/scratchpad","prompt_id":"810f1b72","permission_mode":"acceptEdits",
             "hook_event_name":"PreToolUse","tool_name":"Write","tool_input":{"file_path":"/cwd/blocked-1.txt","content":"hello"},
             "tool_use_id":"toolu_0161"}
            """)));
        Assert.Equal(("Write", "toolu_0161", "810f1b72"), (input.ToolName, input.ToolUseId, input.PromptId));
        Assert.Equal("hello", input.ToolInput.GetProperty("content").GetString());
        Assert.Equal("/tmp/scratchpad", input.Raw.GetProperty("scratchpad_dir").GetString());
        Assert.Null(input.McpServer);
    }

    [Fact]
    public void Parse_ToolHooks_McpServerAndDuration()
    {
        const string mcp = "\"mcp_server\":{\"name\":\"github\",\"source\":\"plugin\"}";
        var pre = Assert.IsType<PreToolUseHookInput>(ParseHook("PreToolUse", "\"tool_name\":\"mcp__github__x\",\"tool_input\":{},\"tool_use_id\":\"t\"," + mcp));
        Assert.Equal(new McpServerProvenance("github", "plugin"), pre.McpServer);

        var post = Assert.IsType<PostToolUseHookInput>(ParseHook("PostToolUse", "\"tool_name\":\"x\",\"tool_input\":{},\"tool_response\":\"ok\",\"tool_use_id\":\"t\",\"duration_ms\":12.5," + mcp));
        Assert.Equal((12.5, "ok"), (post.DurationMs!.Value, post.ToolResponse.GetString()));
        Assert.Equal("github", post.McpServer!.Name);

        var fail = Assert.IsType<PostToolUseFailureHookInput>(ParseHook("PostToolUseFailure", "\"tool_name\":\"x\",\"tool_input\":{},\"tool_use_id\":\"t\",\"error\":\"e\",\"is_interrupt\":true,\"duration_ms\":3," + mcp));
        Assert.Equal(("e", true, 3d), (fail.Error, fail.IsInterrupt!.Value, fail.DurationMs!.Value));
        Assert.NotNull(fail.McpServer);

        var denied = Assert.IsType<PermissionDeniedHookInput>(ParseHook("PermissionDenied", "\"tool_name\":\"x\",\"tool_input\":{\"a\":1},\"tool_use_id\":\"t\",\"reason\":\"auto-mode classifier\"," + mcp));
        Assert.Equal(("x", "t", "auto-mode classifier", 1), (denied.ToolName, denied.ToolUseId, denied.Reason, denied.ToolInput.GetProperty("a").GetInt32()));
        Assert.NotNull(denied.McpServer);
    }

    [Fact]
    public void Parse_PostToolBatch_ToolCalls()
    {
        var input = Assert.IsType<PostToolBatchHookInput>(ParseHook("PostToolBatch", """
            "tool_calls":[{"tool_name":"Read","tool_input":{"file_path":"/a"},"tool_use_id":"t1","tool_response":"contents"},
                          {"tool_name":"Grep","tool_input":{},"tool_use_id":"t2"}]
            """));
        Assert.Equal(2, input.ToolCalls.Count);
        Assert.Equal(("Read", "t1", "/a", "contents"),
            (input.ToolCalls[0].ToolName, input.ToolCalls[0].ToolUseId, input.ToolCalls[0].ToolInput.GetProperty("file_path").GetString(), input.ToolCalls[0].ToolResponse!.Value.GetString()));
        Assert.Null(input.ToolCalls[1].ToolResponse);
    }

    [Fact]
    public void Parse_UserPromptSubmit_And_Expansion()
    {
        var submit = Assert.IsType<UserPromptSubmitHookInput>(ParseHook("UserPromptSubmit", "\"prompt\":\"fix it\",\"source\":\"loop_wakeup\",\"session_title\":\"Bugfix\""));
        Assert.Equal(("fix it", "loop_wakeup", "Bugfix"), (submit.Prompt, submit.Source, submit.SessionTitle));

        var exp = Assert.IsType<UserPromptExpansionHookInput>(ParseHook("UserPromptExpansion",
            "\"expansion_type\":\"mcp_prompt\",\"command_name\":\"srv:review\",\"command_args\":\"--all\",\"command_source\":\"mcp\",\"prompt\":\"Review everything\""));
        Assert.Equal(("mcp_prompt", "srv:review", "--all", "mcp", "Review everything"),
            (exp.ExpansionType, exp.CommandName, exp.CommandArgs, exp.CommandSource, exp.Prompt));
    }

    [Fact]
    public void Parse_Stop_And_SubagentStop_ExtraFields()
    {
        const string extras = """
            "last_assistant_message":"Done.",
            "background_tasks":[{"id":"b1","type":"shell","status":"running","description":"dev server","command":"npm run dev"},
                                {"id":"b2","type":"mcp","status":"running","description":"d","server":"srv","tool":"watch","name":"n","agent_type":"x"}],
            "session_crons":[{"id":"c1","schedule":"*/5 * * * *","recurring":true,"prompt":"check CI"}]
            """;
        var stop = Assert.IsType<StopHookInput>(ParseHook("Stop", "\"stop_hook_active\":true," + extras));
        Assert.True(stop.StopHookActive);
        Assert.Equal("Done.", stop.LastAssistantMessage);
        Assert.Equal(2, stop.BackgroundTasks!.Count);
        Assert.Equal(("b1", "shell", "running", "dev server", "npm run dev"),
            (stop.BackgroundTasks[0].Id, stop.BackgroundTasks[0].Type, stop.BackgroundTasks[0].Status, stop.BackgroundTasks[0].Description, stop.BackgroundTasks[0].Command));
        Assert.Equal(("srv", "watch", "n", "x"),
            (stop.BackgroundTasks[1].Server, stop.BackgroundTasks[1].Tool, stop.BackgroundTasks[1].Name, stop.BackgroundTasks[1].AgentType));
        Assert.Equal(new SessionCronSummary("c1", "*/5 * * * *", true, "check CI"), Assert.Single(stop.SessionCrons!));

        var sub = Assert.IsType<SubagentStopHookInput>(ParseHook("SubagentStop",
            "\"stop_hook_active\":false,\"agent_id\":\"sub-7\",\"agent_transcript_path\":\"/sub.jsonl\",\"agent_type\":\"Explore\"," + extras));
        Assert.Equal(("sub-7", "/sub.jsonl", "Explore"), (sub.AgentId, sub.AgentTranscriptPath, sub.AgentType));
        // The base-class view reads the same wire field.
        Assert.Equal("sub-7", ((BaseHookInput)sub).AgentId);
        Assert.Equal("Done.", sub.LastAssistantMessage);
        Assert.Single(sub.SessionCrons!);
    }

    [Fact]
    public void Parse_StopFailure_ErrorMapping()
    {
        var known = Assert.IsType<StopFailureHookInput>(ParseHook("StopFailure", "\"error\":\"max_output_tokens\",\"error_details\":\"d\",\"last_assistant_message\":\"partial\""));
        Assert.Equal((AssistantMessageError.MaxOutputTokens, "max_output_tokens", "d", "partial"),
            (known.Error!.Value, known.ErrorRaw, known.ErrorDetails, known.LastAssistantMessage));

        var unknown = Assert.IsType<StopFailureHookInput>(ParseHook("StopFailure", "\"error\":\"brand_new\""));
        Assert.Equal((AssistantMessageError.Unknown, "brand_new"), (unknown.Error!.Value, unknown.ErrorRaw));
    }

    [Fact]
    public void Parse_SessionStart_SessionEnd_Setup_Compact()
    {
        var start = Assert.IsType<SessionStartHookInput>(ParseHook("SessionStart", """
            "source":"resume","agent_type":"custom","model":"claude-opus","session_title":"T","seconds_since_last_response":3600.5,
            "context_tokens":42000,"prompt_cache_likely_expired":true,"estimated_cache_write_usd":0.12
            """));
        Assert.Equal(("resume", "claude-opus", "T", 3600.5, 42000L, true, 0.12),
            (start.Source, start.Model, start.SessionTitle, start.SecondsSinceLastResponse!.Value, start.ContextTokens!.Value,
             start.PromptCacheLikelyExpired!.Value, start.EstimatedCacheWriteUsd!.Value));
        Assert.Equal("custom", start.AgentType);

        Assert.Equal("prompt_input_exit", Assert.IsType<SessionEndHookInput>(ParseHook("SessionEnd", "\"reason\":\"prompt_input_exit\"")).Reason);
        Assert.Equal("maintenance", Assert.IsType<SetupHookInput>(ParseHook("Setup", "\"trigger\":\"maintenance\"")).Trigger);

        var pre = Assert.IsType<PreCompactHookInput>(ParseHook("PreCompact", "\"trigger\":\"manual\",\"custom_instructions\":\"keep APIs\""));
        Assert.Equal(("manual", "keep APIs"), (pre.Trigger, pre.CustomInstructions));
        var post = Assert.IsType<PostCompactHookInput>(ParseHook("PostCompact", "\"trigger\":\"auto\",\"compact_summary\":\"We did X\""));
        Assert.Equal(("auto", "We did X"), (post.Trigger, post.CompactSummary));
    }

    [Fact]
    public void Parse_ModelSwitch()
    {
        var pre = Assert.IsType<PreModelSwitchHookInput>(ParseHook("PreModelSwitch", """
            "from_model":"claude-opus","to_model":"claude-haiku","requested_model":"haiku","source":"picker","context_tokens":90000,
            "prompt_cache_warm":true,"cache_ttl":"1h","estimated_cache_write_usd":0.75,"pricing":"configured"
            """));
        Assert.Equal(("claude-opus", "claude-haiku", "haiku", "picker", 90000L, true, "1h", 0.75, "configured"),
            (pre.FromModel, pre.ToModel, pre.RequestedModel, pre.Source, pre.ContextTokens, pre.PromptCacheWarm, pre.CacheTtl, pre.EstimatedCacheWriteUsd, pre.Pricing));

        var post = Assert.IsType<PostModelSwitchHookInput>(ParseHook("PostModelSwitch", """
            "from_model":"a","to_model":"b","requested_model":null,"source":"resume","context_tokens":0,"prompt_cache_warm":false,
            "cache_ttl":"5m","estimated_cache_write_usd":0,"pricing":"default"
            """));
        Assert.Null(post.RequestedModel);
        Assert.Equal("resume", post.Source);
        Assert.IsAssignableFrom<ModelSwitchHookInput>(post);
    }

    [Fact]
    public void Parse_PermissionRequest_TypedSuggestions_IncludingCliArg()
    {
        var input = Assert.IsType<PermissionRequestHookInput>(ParseHook("PermissionRequest", """
            "tool_name":"Bash","tool_input":{"command":"npm test"},
            "permission_suggestions":[
              {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"cliArg"},
              {"type":"setMode","mode":"acceptEdits","destination":"session"},
              {"type":"futureUpdateKind","destination":"session"}],
            "mcp_server":{"name":"srv","source":"user"}
            """));
        Assert.Equal(JsonValueKind.Array, input.PermissionSuggestions!.Value.ValueKind);
        Assert.Equal(2, input.Suggestions!.Count);
        var add = input.Suggestions[0];
        Assert.Equal((PermissionUpdateType.AddRules, PermissionBehavior.Allow, PermissionUpdateDestination.CliArg),
            (add.Type, add.Behavior!.Value, add.Destination!.Value));
        Assert.Equal(new PermissionRuleValue("Bash", "npm test"), Assert.Single(add.Rules!));
        Assert.Equal(PermissionMode.AcceptEdits, input.Suggestions[1].Mode);
        Assert.Equal(new McpServerProvenance("srv", "user"), input.McpServer);
    }

    [Fact]
    public void Parse_TeamAndElicitationEvents()
    {
        var idle = Assert.IsType<TeammateIdleHookInput>(ParseHook("TeammateIdle", "\"teammate_name\":\"alice\",\"team_name\":\"core\""));
        Assert.Equal(("alice", "core"), (idle.TeammateName, idle.TeamName));

        var created = Assert.IsType<TaskCreatedHookInput>(ParseHook("TaskCreated",
            "\"task_id\":\"7\",\"task_subject\":\"Write docs\",\"task_description\":\"All of them\",\"teammate_name\":\"bob\",\"team_name\":\"core\""));
        Assert.Equal(("7", "Write docs", "All of them", "bob", "core"),
            (created.TaskId, created.TaskSubject, created.TaskDescription, created.TeammateName, created.TeamName));
        var done = Assert.IsType<TaskCompletedHookInput>(ParseHook("TaskCompleted", "\"task_id\":\"7\",\"task_subject\":\"Write docs\""));
        Assert.Null(done.TaskDescription);

        var elicit = Assert.IsType<ElicitationHookInput>(ParseHook("Elicitation", """
            "mcp_server_name":"srv","message":"Choose","mode":"form","url":"https://e","elicitation_id":"el1",
            "requested_schema":{"type":"object","properties":{"color":{"type":"string"}}}
            """));
        Assert.Equal(("srv", "Choose", "form", "https://e", "el1"), (elicit.McpServerName, elicit.Message, elicit.Mode, elicit.Url, elicit.ElicitationId));
        Assert.Equal("string", elicit.RequestedSchema!.Value.GetProperty("properties").GetProperty("color").GetProperty("type").GetString());

        var result = Assert.IsType<ElicitationResultHookInput>(ParseHook("ElicitationResult",
            "\"mcp_server_name\":\"srv\",\"elicitation_id\":\"el1\",\"mode\":\"url\",\"action\":\"accept\",\"content\":{\"color\":\"red\"}"));
        Assert.Equal(("accept", "url", "red"), (result.Action, result.Mode, result.Content!.Value.GetProperty("color").GetString()));
    }

    [Fact]
    public void Parse_FileSystemEvents()
    {
        var cfg = Assert.IsType<ConfigChangeHookInput>(ParseHook("ConfigChange", "\"source\":\"skills\",\"file_path\":\"/s/SKILL.md\""));
        Assert.Equal(("skills", "/s/SKILL.md"), (cfg.Source, cfg.FilePath));

        Assert.Equal("feat", Assert.IsType<WorktreeCreateHookInput>(ParseHook("WorktreeCreate", "\"name\":\"feat\"")).Name);
        Assert.Equal("/wt/feat", Assert.IsType<WorktreeRemoveHookInput>(ParseHook("WorktreeRemove", "\"worktree_path\":\"/wt/feat\"")).WorktreePath);

        var loaded = Assert.IsType<InstructionsLoadedHookInput>(ParseHook("InstructionsLoaded", """
            "file_path":"/w/src/CLAUDE.md","memory_type":"Project","load_reason":"path_glob_match","globs":["src/**"],
            "trigger_file_path":"/w/src/a.cs","parent_file_path":"/w/CLAUDE.md"
            """));
        Assert.Equal(("/w/src/CLAUDE.md", "Project", "path_glob_match", "/w/src/a.cs", "/w/CLAUDE.md"),
            (loaded.FilePath, loaded.MemoryType, loaded.LoadReason, loaded.TriggerFilePath, loaded.ParentFilePath));
        Assert.Equal(["src/**"], loaded.Globs);

        var cwd = Assert.IsType<CwdChangedHookInput>(ParseHook("CwdChanged", "\"old_cwd\":\"/a\",\"new_cwd\":\"/b\""));
        Assert.Equal(("/a", "/b"), (cwd.OldCwd, cwd.NewCwd));

        var file = Assert.IsType<FileChangedHookInput>(ParseHook("FileChanged", "\"file_path\":\"/w/x\",\"event\":\"unlink\""));
        Assert.Equal(("/w/x", "unlink"), (file.FilePath, file.Event));

        var dir = Assert.IsType<DirectoryAddedHookInput>(ParseHook("DirectoryAdded", "\"directory\":\"/repo2\",\"source\":\"register_repo_root\""));
        Assert.Equal(("/repo2", "register_repo_root"), (dir.Directory, dir.Source));

        var display = Assert.IsType<MessageDisplayHookInput>(ParseHook("MessageDisplay",
            "\"turn_id\":\"turn\",\"message_id\":\"msg\",\"index\":3,\"final\":true,\"delta\":\"lo!\""));
        Assert.Equal(("turn", "msg", 3, true, "lo!"), (display.TurnId, display.MessageId, display.Index, display.Final, display.Delta));
    }

    [Fact]
    public void Parse_UnknownEvent_GivesUnknownHookInput()
    {
        var input = Assert.IsType<UnknownHookInput>(ParseHook("FutureEvent", "\"x\":1"));
        Assert.Equal("FutureEvent", input.HookEventName);
        Assert.Equal(1, input.Raw.GetProperty("x").GetInt32());
        Assert.Equal("sess-1", input.SessionId);
    }

    [Fact]
    public void Parse_IsLenient_AndRejectsNonObjects()
    {
        var sparse = Assert.IsType<PreToolUseHookInput>(HookInput.Parse(J("""{"hook_event_name":"PreToolUse"}""")));
        Assert.Equal(("", "", ""), (sparse.SessionId, sparse.ToolName, sparse.ToolUseId));
        Assert.Null(sparse.Effort);
        Assert.Throws<ArgumentException>(() => HookInput.Parse(J("[]")));
        Assert.Throws<ArgumentException>(() => HookInput.Parse(default));
    }

    [Fact]
    public void ParseAs_ReturnsNullForOtherEvents()
    {
        var json = J("{" + Base + ",\"hook_event_name\":\"SessionEnd\",\"reason\":\"clear\"}");
        Assert.NotNull(HookInput.ParseAs<SessionEndHookInput>(json));
        Assert.Null(HookInput.ParseAs<SessionStartHookInput>(json));
    }

    #endregion

    #region Hook-specific outputs

    public static TheoryData<string, string> Outputs => new()
    {
        { new StopHookSpecificOutput { AdditionalContext = "c" }.ToJsonElement().GetRawText(), """{"hookEventName":"Stop","additionalContext":"c"}""" },
        { new StopHookSpecificOutput().ToJsonElement().GetRawText(), """{"hookEventName":"Stop"}""" },
        { new SubagentStopHookSpecificOutput { AdditionalContext = "c" }.ToJsonElement().GetRawText(), """{"hookEventName":"SubagentStop","additionalContext":"c"}""" },
        { new UserPromptExpansionHookSpecificOutput { AdditionalContext = "c", SuppressOriginalPrompt = true }.ToJsonElement().GetRawText(), """{"hookEventName":"UserPromptExpansion","additionalContext":"c","suppressOriginalPrompt":true}""" },
        { new SetupHookSpecificOutput { AdditionalContext = "c" }.ToJsonElement().GetRawText(), """{"hookEventName":"Setup","additionalContext":"c"}""" },
        { new PreModelSwitchHookSpecificOutput { PermissionDecision = HookPermissionDecision.Deny, PermissionDecisionReason = "too pricey" }.ToJsonElement().GetRawText(), """{"hookEventName":"PreModelSwitch","permissionDecision":"deny","permissionDecisionReason":"too pricey"}""" },
        { new PostModelSwitchHookSpecificOutput { AdditionalContext = "c" }.ToJsonElement().GetRawText(), """{"hookEventName":"PostModelSwitch","additionalContext":"c"}""" },
        { new PostToolBatchHookSpecificOutput { AdditionalContext = "c" }.ToJsonElement().GetRawText(), """{"hookEventName":"PostToolBatch","additionalContext":"c"}""" },
        { new PermissionDeniedHookSpecificOutput { Retry = true }.ToJsonElement().GetRawText(), """{"hookEventName":"PermissionDenied","retry":true}""" },
        { new ElicitationHookSpecificOutput { Action = "accept", Content = J("""{"color":"red"}""") }.ToJsonElement().GetRawText(), """{"hookEventName":"Elicitation","action":"accept","content":{"color":"red"}}""" },
        { new ElicitationResultHookSpecificOutput { Action = "cancel" }.ToJsonElement().GetRawText(), """{"hookEventName":"ElicitationResult","action":"cancel"}""" },
        { new CwdChangedHookSpecificOutput { WatchPaths = ["/a", "/b"] }.ToJsonElement().GetRawText(), """{"hookEventName":"CwdChanged","watchPaths":["/a","/b"]}""" },
        { new FileChangedHookSpecificOutput { WatchPaths = ["/c"] }.ToJsonElement().GetRawText(), """{"hookEventName":"FileChanged","watchPaths":["/c"]}""" },
        { new WorktreeCreateHookSpecificOutput { WorktreePath = "/wt/x" }.ToJsonElement().GetRawText(), """{"hookEventName":"WorktreeCreate","worktreePath":"/wt/x"}""" },
        { new MessageDisplayHookSpecificOutput { DisplayContent = "***" }.ToJsonElement().GetRawText(), """{"hookEventName":"MessageDisplay","displayContent":"***"}""" },
        // Fields added to existing outputs.
        { new PostToolUseHookSpecificOutput { ClassifierContext = "safe read" }.ToJsonElement().GetRawText(), """{"hookEventName":"PostToolUse","classifierContext":"safe read"}""" },
        { new UserPromptSubmitHookSpecificOutput { SessionTitle = "T", SuppressOriginalPrompt = true }.ToJsonElement().GetRawText(), """{"hookEventName":"UserPromptSubmit","sessionTitle":"T","suppressOriginalPrompt":true}""" },
        { new SessionStartHookSpecificOutput { AdditionalContext = "c", InitialUserMessage = "hi", SessionTitle = "T", WatchPaths = ["/w"], ReloadSkills = true }.ToJsonElement().GetRawText(),
          """{"hookEventName":"SessionStart","additionalContext":"c","initialUserMessage":"hi","sessionTitle":"T","watchPaths":["/w"],"reloadSkills":true}""" }
    };

    [Theory]
    [MemberData(nameof(Outputs))]
    public void HookSpecificOutputs_SerializeToTsWireShape(string actual, string expected)
    {
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task HookCallback_TerminalSequence_And_NewOutputs_ReachTheWire()
    {
        var options = new ClaudeAgentOptions
        {
            Hooks = new HooksBuilder().On("SessionStart", null, (input, _, _, _) =>
            {
                var start = Assert.IsType<SessionStartHookInput>(HookInput.Parse(input));
                return Task.FromResult(new HookOutput
                {
                    TerminalSequence = "\u001b]0;" + start.Source + "\u0007",
                    Decision = HookDecision.Approve,
                    HookSpecificOutput = new SessionStartHookSpecificOutput { WatchPaths = ["/w/.env"], ReloadSkills = true }.ToJsonElement()
                });
            }).Build()
        };
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();

        transport.Send(new
        {
            type = "control_request",
            request_id = "hook_req",
            request = new
            {
                subtype = "hook_callback",
                callback_id = "hook_0",
                input = new { session_id = "s", transcript_path = "/t", cwd = "/w", hook_event_name = "SessionStart", source = "startup" }
            }
        });
        await transport.WaitForAsync(t => t.ResponsesFor("hook_req").Count > 0);

        var response = transport.ResponsesFor("hook_req").Single().GetProperty("response").GetProperty("response");
        Assert.Equal("\u001b]0;startup\u0007", response.GetProperty("terminalSequence").GetString());
        Assert.Equal("approve", response.GetProperty("decision").GetString());
        Assert.Equal("""{"hookEventName":"SessionStart","watchPaths":["/w/.env"],"reloadSkills":true}""",
            response.GetProperty("hookSpecificOutput").GetRawText());
    }

    [Fact]
    public async Task HookCallback_NoTerminalSequence_OmitsTheKey()
    {
        var options = new ClaudeAgentOptions
        {
            Hooks = new HooksBuilder().On(HookEvent.Stop, null, (_, _, _, _) => Task.FromResult(new HookOutput { Continue = true })).Build()
        };
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();

        transport.Send(new { type = "control_request", request_id = "r", request = new { subtype = "hook_callback", callback_id = "hook_0", input = new { } } });
        await transport.WaitForAsync(t => t.ResponsesFor("r").Count > 0);

        var response = transport.ResponsesFor("r").Single().GetProperty("response").GetProperty("response");
        Assert.False(response.TryGetProperty("terminalSequence", out _));
    }

    #endregion

    #region PermissionRequest decision

    [Fact]
    public void PermissionRequestDecision_Allow_RoundTrips()
    {
        var decision = PermissionRequestDecision.Allow(
            J("""{"command":"npm test -- --ci"}"""),
            [new PermissionUpdate(PermissionUpdateType.AddRules, [new PermissionRuleValue("Bash", "npm test:*")], PermissionBehavior.Allow, Destination: PermissionUpdateDestination.CliArg)]);

        var json = decision.ToJsonElement();
        Assert.Equal(
            """{"behavior":"allow","updatedInput":{"command":"npm test -- --ci"},"updatedPermissions":[{"type":"addRules","destination":"cliArg","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow"}]}""",
            json.GetRawText());

        var back = Assert.IsType<PermissionRequestAllowDecision>(PermissionRequestDecision.Parse(json));
        Assert.Equal("npm test -- --ci", back.UpdatedInput!.Value.GetProperty("command").GetString());
        var update = Assert.Single(back.UpdatedPermissions!);
        Assert.Equal(PermissionUpdateDestination.CliArg, update.Destination);
        Assert.Equal("allow", back.Behavior);
    }

    [Fact]
    public void PermissionRequestDecision_Deny_RoundTrips_AndOmitsUnset()
    {
        Assert.Equal("""{"behavior":"deny"}""", PermissionRequestDecision.Deny().ToJsonElement().GetRawText());
        Assert.Equal("""{"behavior":"allow"}""", PermissionRequestDecision.Allow().ToJsonElement().GetRawText());

        var json = PermissionRequestDecision.Deny("Not on main", interrupt: true).ToJsonElement();
        Assert.Equal("""{"behavior":"deny","message":"Not on main","interrupt":true}""", json.GetRawText());
        var back = Assert.IsType<PermissionRequestDenyDecision>(PermissionRequestDecision.Parse(json));
        Assert.Equal(("Not on main", true), (back.Message, back.Interrupt!.Value));
    }

    [Fact]
    public void PermissionRequestDecision_Parse_UnknownShapes()
    {
        Assert.Null(PermissionRequestDecision.Parse(J("""{"behavior":"ask"}""")));
        Assert.Null(PermissionRequestDecision.Parse(J("42")));
    }

    [Fact]
    public void PermissionRequestOutput_FromTypedDecision()
    {
        var output = PermissionRequestHookSpecificOutput.From(PermissionRequestDecision.Deny("no"));
        Assert.Equal("""{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"no"}}""", output.ToJsonElement().GetRawText());
        Assert.Equal("no", Assert.IsType<PermissionRequestDenyDecision>(output.TypedDecision).Message);
    }

    #endregion

    #region PermissionUpdateDestination.CliArg

    [Fact]
    public void CliArgDestination_AllConversions()
    {
        var update = PermissionUpdate.FromControlProtocol(J("""{"type":"removeDirectories","directories":["/d"],"destination":"cliArg"}"""))!;
        Assert.Equal(PermissionUpdateDestination.CliArg, update.Destination);

        var dict = update.ToDictionary();
        Assert.Equal("cliArg", dict["destination"]);

        var back = PermissionUpdate.FromDictionary(dict);
        Assert.Equal(PermissionUpdateDestination.CliArg, back.Destination);
        Assert.Equal("cliArg", PermissionUpdateDestination.CliArg.ToJsonString());
    }

    #endregion
}
