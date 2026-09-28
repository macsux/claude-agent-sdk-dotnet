// Claude Agent SDK for .NET — hook events, inputs and outputs added for
// parity with the TypeScript SDK (@anthropic-ai/claude-agent-sdk 0.3.283).
// Python (0.2.160) supports 10 hook events; TS supports 33.

using System.Text.Json;
using System.Text.Json.Serialization;
using Claude.AgentSdk.Internal;
using static Claude.AgentSdk.Internal.JsonRead;

namespace Claude.AgentSdk;

#region Hook event names

/// <summary>
/// Wire names for <see cref="HookEvent"/>, plus an escape hatch for events
/// newer than this SDK version.
/// </summary>
/// <remarks>
/// <see cref="Parse"/> maps a name this SDK doesn't list to a stable
/// <see cref="HookEvent"/> value outside the named range (process-wide, one
/// value per distinct name). That value can be used as a key in
/// <see cref="ClaudeAgentOptions.Hooks"/> and is registered with the CLI under
/// the exact name given, so a future CLI hook event works without an SDK
/// release. <c>Enum.ToString()</c> of such a value is a number; use
/// <see cref="ToWireName"/> for the name.
/// </remarks>
public static class HookEventNames
{
    /// <summary>First numeric value used for events registered via <see cref="Parse"/>.</summary>
    public const int CustomEventBase = 10_000;

    // Index == (int)HookEvent for every named member (verified by tests).
    private static readonly string[] s_known =
    [
        "PreToolUse", "PostToolUse", "PostToolUseFailure", "UserPromptSubmit", "Stop",
        "SubagentStop", "PreCompact", "Notification", "SubagentStart", "PermissionRequest",
        "PostToolBatch", "UserPromptExpansion", "SessionStart", "SessionEnd", "StopFailure",
        "PostCompact", "PreModelSwitch", "PostModelSwitch", "PermissionDenied", "Setup",
        "TeammateIdle", "TaskCreated", "TaskCompleted", "Elicitation", "ElicitationResult",
        "ConfigChange", "WorktreeCreate", "WorktreeRemove", "InstructionsLoaded", "CwdChanged",
        "FileChanged", "DirectoryAdded", "MessageDisplay"
    ];

    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, HookEvent> s_customByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<HookEvent, string> s_customByValue = [];

    /// <summary>All hook event names this SDK version knows (TS <c>HOOK_EVENTS</c>).</summary>
    public static IReadOnlyList<string> Known => s_known;

    /// <summary>True when <paramref name="hookEvent"/> is a named member (not a custom registration).</summary>
    public static bool IsKnown(HookEvent hookEvent) => (int)hookEvent >= 0 && (int)hookEvent < s_known.Length;

    /// <summary>The name the CLI uses for <paramref name="hookEvent"/>.</summary>
    public static string ToWireName(this HookEvent hookEvent)
    {
        if (IsKnown(hookEvent))
            return s_known[(int)hookEvent];
        lock (s_lock)
        {
            if (s_customByValue.TryGetValue(hookEvent, out var name))
                return name;
        }
        return hookEvent.ToString();
    }

    /// <summary>Look up a known event by its exact (case-sensitive) wire name.</summary>
    public static bool TryParseKnown(string name, out HookEvent hookEvent)
    {
        var index = Array.IndexOf(s_known, name);
        hookEvent = (HookEvent)Math.Max(index, 0);
        return index >= 0;
    }

    /// <summary>
    /// The <see cref="HookEvent"/> for <paramref name="name"/>: a named member
    /// when known, otherwise a custom value registered for that exact name.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public static HookEvent Parse(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Hook event name must be a non-empty string.", nameof(name));
        if (TryParseKnown(name, out var known))
            return known;
        lock (s_lock)
        {
            if (s_customByName.TryGetValue(name, out var existing))
                return existing;
            var value = (HookEvent)(CustomEventBase + s_customByName.Count);
            s_customByName[name] = value;
            s_customByValue[value] = name;
            return value;
        }
    }
}

/// <summary>Values for <see cref="HookOutput.Decision"/>. TS <c>SyncHookJSONOutput.decision</c>.</summary>
public static class HookDecision
{
    public const string Approve = "approve";
    public const string Block = "block";
}

/// <summary>Values for <c>permissionDecision</c> in PreToolUse / PreModelSwitch outputs. TS <c>HookPermissionDecision</c>.</summary>
public static class HookPermissionDecision
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Ask = "ask";
    /// <summary>PreToolUse only.</summary>
    public const string Defer = "defer";
}

#endregion

#region Hook input supporting types

/// <summary>Effort in force for the turn. TS <c>BaseHookInput.effort</c>.</summary>
public record HookEffort([property: JsonPropertyName("level")] string Level);

/// <summary>The MCP server a tool came from. TS <c>McpServerProvenance</c>.</summary>
public record McpServerProvenance(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source);

/// <summary>A running background task, reported to Stop hooks. TS <c>BackgroundTaskSummary</c>.</summary>
public record BackgroundTaskSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; init; }

    [JsonPropertyName("server")]
    public string? Server { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>A session cron job, reported to Stop hooks. TS <c>SessionCronSummary</c>.</summary>
public record SessionCronSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("schedule")] string Schedule,
    [property: JsonPropertyName("recurring")] bool Recurring,
    [property: JsonPropertyName("prompt")] string Prompt);

/// <summary>One call in a PostToolBatch hook. TS <c>PostToolBatchToolCall</c>.</summary>
public record PostToolBatchToolCall
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("tool_response")]
    public JsonElement? ToolResponse { get; init; }
}

#endregion

#region New hook inputs (TS 0.3.283)

/// <summary>Input for PostToolBatch hooks. TS <c>PostToolBatchHookInput</c>.</summary>
public record PostToolBatchHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PostToolBatch";

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<PostToolBatchToolCall> ToolCalls { get; init; } = [];
}

/// <summary>Input for UserPromptExpansion hooks. TS <c>UserPromptExpansionHookInput</c>.</summary>
public record UserPromptExpansionHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "UserPromptExpansion";

    /// <summary><c>slash_command</c> or <c>mcp_prompt</c>.</summary>
    [JsonPropertyName("expansion_type")]
    public required string ExpansionType { get; init; }

    [JsonPropertyName("command_name")]
    public required string CommandName { get; init; }

    [JsonPropertyName("command_args")]
    public required string CommandArgs { get; init; }

    [JsonPropertyName("command_source")]
    public string? CommandSource { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }
}

/// <summary>Input for SessionStart hooks. TS <c>SessionStartHookInput</c>.</summary>
public record SessionStartHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "SessionStart";

    /// <summary><c>startup</c>, <c>resume</c>, <c>clear</c>, <c>compact</c> or <c>fork</c>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("session_title")]
    public string? SessionTitle { get; init; }

    [JsonPropertyName("seconds_since_last_response")]
    public double? SecondsSinceLastResponse { get; init; }

    [JsonPropertyName("context_tokens")]
    public long? ContextTokens { get; init; }

    [JsonPropertyName("prompt_cache_likely_expired")]
    public bool? PromptCacheLikelyExpired { get; init; }

    [JsonPropertyName("estimated_cache_write_usd")]
    public double? EstimatedCacheWriteUsd { get; init; }
}

/// <summary>Input for SessionEnd hooks. TS <c>SessionEndHookInput</c>.</summary>
public record SessionEndHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "SessionEnd";

    /// <summary>TS <c>ExitReason</c>: <c>clear</c>, <c>resume</c>, <c>logout</c>, <c>prompt_input_exit</c>, <c>other</c>.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>Input for StopFailure hooks. TS <c>StopFailureHookInput</c>.</summary>
public record StopFailureHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "StopFailure";

    /// <summary>Parsed <c>error</c>; see <see cref="ErrorRaw"/> for the wire string.</summary>
    [JsonPropertyName("error")]
    public AssistantMessageError? Error { get; init; }

    [JsonPropertyName("error_raw")]
    public string? ErrorRaw { get; init; }

    [JsonPropertyName("error_details")]
    public string? ErrorDetails { get; init; }

    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }
}

/// <summary>Input for PostCompact hooks. TS <c>PostCompactHookInput</c>.</summary>
public record PostCompactHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PostCompact";

    /// <summary><c>manual</c> or <c>auto</c>.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("compact_summary")]
    public required string CompactSummary { get; init; }
}

/// <summary>Fields shared by PreModelSwitch and PostModelSwitch hook inputs.</summary>
public abstract record ModelSwitchHookInput : BaseHookInput
{
    [JsonPropertyName("from_model")]
    public required string FromModel { get; init; }

    [JsonPropertyName("to_model")]
    public required string ToModel { get; init; }

    [JsonPropertyName("requested_model")]
    public string? RequestedModel { get; init; }

    /// <summary><c>command</c>, <c>picker</c>, <c>sdk</c> (Post also <c>auto</c>, <c>resume</c>).</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("context_tokens")]
    public long ContextTokens { get; init; }

    [JsonPropertyName("prompt_cache_warm")]
    public bool PromptCacheWarm { get; init; }

    /// <summary><c>5m</c> or <c>1h</c>.</summary>
    [JsonPropertyName("cache_ttl")]
    public string? CacheTtl { get; init; }

    [JsonPropertyName("estimated_cache_write_usd")]
    public double EstimatedCacheWriteUsd { get; init; }

    /// <summary><c>configured</c>, <c>catalog</c> or <c>default</c>.</summary>
    [JsonPropertyName("pricing")]
    public string? Pricing { get; init; }
}

/// <summary>Input for PreModelSwitch hooks. TS <c>PreModelSwitchHookInput</c>.</summary>
public record PreModelSwitchHookInput : ModelSwitchHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PreModelSwitch";
}

/// <summary>Input for PostModelSwitch hooks. TS <c>PostModelSwitchHookInput</c>.</summary>
public record PostModelSwitchHookInput : ModelSwitchHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PostModelSwitch";
}

/// <summary>Input for PermissionDenied hooks. TS <c>PermissionDeniedHookInput</c>.</summary>
public record PermissionDeniedHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PermissionDenied";

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("mcp_server")]
    public McpServerProvenance? McpServer { get; init; }
}

/// <summary>Input for Setup hooks. TS <c>SetupHookInput</c>.</summary>
public record SetupHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "Setup";

    /// <summary><c>init</c> or <c>maintenance</c>.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }
}

/// <summary>Input for TeammateIdle hooks. TS <c>TeammateIdleHookInput</c>.</summary>
public record TeammateIdleHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "TeammateIdle";

    [JsonPropertyName("teammate_name")]
    public required string TeammateName { get; init; }

    [JsonPropertyName("team_name")]
    public required string TeamName { get; init; }
}

/// <summary>Fields shared by TaskCreated and TaskCompleted hook inputs.</summary>
public abstract record TeamTaskHookInput : BaseHookInput
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("task_subject")]
    public required string TaskSubject { get; init; }

    [JsonPropertyName("task_description")]
    public string? TaskDescription { get; init; }

    [JsonPropertyName("teammate_name")]
    public string? TeammateName { get; init; }

    [JsonPropertyName("team_name")]
    public string? TeamName { get; init; }
}

/// <summary>Input for TaskCreated hooks. TS <c>TaskCreatedHookInput</c>.</summary>
public record TaskCreatedHookInput : TeamTaskHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "TaskCreated";
}

/// <summary>Input for TaskCompleted hooks. TS <c>TaskCompletedHookInput</c>.</summary>
public record TaskCompletedHookInput : TeamTaskHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "TaskCompleted";
}

/// <summary>Input for Elicitation hooks. TS <c>ElicitationHookInput</c>.</summary>
public record ElicitationHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "Elicitation";

    [JsonPropertyName("mcp_server_name")]
    public required string McpServerName { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary><c>form</c> or <c>url</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("elicitation_id")]
    public string? ElicitationId { get; init; }

    [JsonPropertyName("requested_schema")]
    public JsonElement? RequestedSchema { get; init; }
}

/// <summary>Input for ElicitationResult hooks. TS <c>ElicitationResultHookInput</c>.</summary>
public record ElicitationResultHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "ElicitationResult";

    [JsonPropertyName("mcp_server_name")]
    public required string McpServerName { get; init; }

    [JsonPropertyName("elicitation_id")]
    public string? ElicitationId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary><c>accept</c>, <c>decline</c> or <c>cancel</c>.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }
}

/// <summary>Input for ConfigChange hooks. TS <c>ConfigChangeHookInput</c>.</summary>
public record ConfigChangeHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "ConfigChange";

    /// <summary><c>user_settings</c>, <c>project_settings</c>, <c>local_settings</c>, <c>policy_settings</c> or <c>skills</c>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }
}

/// <summary>Input for WorktreeCreate hooks. TS <c>WorktreeCreateHookInput</c>.</summary>
public record WorktreeCreateHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "WorktreeCreate";

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>Input for WorktreeRemove hooks. TS <c>WorktreeRemoveHookInput</c>.</summary>
public record WorktreeRemoveHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "WorktreeRemove";

    [JsonPropertyName("worktree_path")]
    public required string WorktreePath { get; init; }
}

/// <summary>Input for InstructionsLoaded hooks. TS <c>InstructionsLoadedHookInput</c>.</summary>
public record InstructionsLoadedHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "InstructionsLoaded";

    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary><c>User</c>, <c>Project</c>, <c>Local</c> or <c>Managed</c>.</summary>
    [JsonPropertyName("memory_type")]
    public required string MemoryType { get; init; }

    /// <summary><c>session_start</c>, <c>nested_traversal</c>, <c>path_glob_match</c>, <c>include</c> or <c>compact</c>.</summary>
    [JsonPropertyName("load_reason")]
    public required string LoadReason { get; init; }

    [JsonPropertyName("globs")]
    public IReadOnlyList<string>? Globs { get; init; }

    [JsonPropertyName("trigger_file_path")]
    public string? TriggerFilePath { get; init; }

    [JsonPropertyName("parent_file_path")]
    public string? ParentFilePath { get; init; }
}

/// <summary>Input for CwdChanged hooks. TS <c>CwdChangedHookInput</c>.</summary>
public record CwdChangedHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "CwdChanged";

    [JsonPropertyName("old_cwd")]
    public required string OldCwd { get; init; }

    [JsonPropertyName("new_cwd")]
    public required string NewCwd { get; init; }
}

/// <summary>Input for FileChanged hooks. TS <c>FileChangedHookInput</c>.</summary>
public record FileChangedHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "FileChanged";

    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    /// <summary><c>change</c>, <c>add</c> or <c>unlink</c>.</summary>
    [JsonPropertyName("event")]
    public required string Event { get; init; }
}

/// <summary>Input for DirectoryAdded hooks. TS <c>DirectoryAddedHookInput</c>.</summary>
public record DirectoryAddedHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "DirectoryAdded";

    [JsonPropertyName("directory")]
    public required string Directory { get; init; }

    /// <summary><c>slash_command</c> or <c>register_repo_root</c>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>Input for MessageDisplay hooks. TS <c>MessageDisplayHookInput</c>.</summary>
public record MessageDisplayHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "MessageDisplay";

    [JsonPropertyName("turn_id")]
    public required string TurnId { get; init; }

    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("final")]
    public bool Final { get; init; }

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

/// <summary>
/// Input for a hook event this SDK version doesn't model (e.g. one
/// registered through <see cref="HookEventNames.Parse"/>). Read fields from
/// <see cref="BaseHookInput.Raw"/>.
/// </summary>
public record UnknownHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public required string HookEventName { get; init; }
}

#endregion

#region HookInput dispatcher

/// <summary>
/// Reflection-free (trim / NativeAOT-safe) parsing of the raw hook input a
/// <see cref="HookCallback"/> receives into the typed <c>*HookInput</c> record
/// for its <c>hook_event_name</c>. TS <c>HookInput</c> union.
/// </summary>
/// <example>
/// <code>
/// async Task&lt;HookOutput&gt; OnHook(JsonElement input, string? id, HookContext ctx, CancellationToken ct)
/// {
///     if (HookInput.Parse(input) is PreToolUseHookInput pre &amp;&amp; pre.ToolName == "Bash") { ... }
///     return new HookOutput();
/// }
/// </code>
/// </example>
public static class HookInput
{
    /// <summary>
    /// Parse <paramref name="input"/>. Lenient: missing fields become empty
    /// defaults rather than throwing, and an unrecognized event yields
    /// <see cref="UnknownHookInput"/>. <see cref="BaseHookInput.Raw"/> is
    /// always the full input.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not a JSON object.</exception>
    public static BaseHookInput Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Hook input must be a JSON object (got {input.ValueKind}).", nameof(input));

        var e = input.Clone();
        string S(string name) => Str(e, name) ?? string.Empty;
        JsonElement J(string name) => e.TryGetProperty(name, out var v) ? v : default;
        var sid = S("session_id");
        var tp = S("transcript_path");
        var cwd = S("cwd");
        var eventName = S("hook_event_name");

        BaseHookInput parsed = eventName switch
        {
            "PreToolUse" => new PreToolUseHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ToolName = S("tool_name"), ToolInput = J("tool_input"), ToolUseId = S("tool_use_id"),
                McpServer = McpServer(e)
            },
            "PostToolUse" => new PostToolUseHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ToolName = S("tool_name"), ToolInput = J("tool_input"), ToolResponse = J("tool_response"),
                ToolUseId = S("tool_use_id"), DurationMs = Dbl(e, "duration_ms"), McpServer = McpServer(e)
            },
            "PostToolUseFailure" => new PostToolUseFailureHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ToolName = S("tool_name"), ToolInput = J("tool_input"), ToolUseId = S("tool_use_id"),
                Error = S("error"), IsInterrupt = Bool(e, "is_interrupt"),
                DurationMs = Dbl(e, "duration_ms"), McpServer = McpServer(e)
            },
            "PostToolBatch" => new PostToolBatchHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ToolCalls = ObjList(e, "tool_calls", c => new PostToolBatchToolCall
                {
                    ToolName = Str(c, "tool_name") ?? string.Empty,
                    ToolInput = c.TryGetProperty("tool_input", out var ti) ? ti : default,
                    ToolUseId = Str(c, "tool_use_id") ?? string.Empty,
                    ToolResponse = c.TryGetProperty("tool_response", out var tr) ? tr : null
                })
            },
            "UserPromptSubmit" => new UserPromptSubmitHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                Prompt = S("prompt"), Source = Str(e, "source"), SessionTitle = Str(e, "session_title")
            },
            "UserPromptExpansion" => new UserPromptExpansionHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ExpansionType = S("expansion_type"), CommandName = S("command_name"),
                CommandArgs = S("command_args"), CommandSource = Str(e, "command_source"), Prompt = S("prompt")
            },
            "Stop" => new StopHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                StopHookActive = Bool(e, "stop_hook_active") ?? false,
                LastAssistantMessage = Str(e, "last_assistant_message"),
                BackgroundTasks = BackgroundTasks(e), SessionCrons = SessionCrons(e)
            },
            "SubagentStop" => new SubagentStopHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                StopHookActive = Bool(e, "stop_hook_active") ?? false,
                AgentId = S("agent_id"), AgentTranscriptPath = S("agent_transcript_path"), AgentType = S("agent_type"),
                LastAssistantMessage = Str(e, "last_assistant_message"),
                BackgroundTasks = BackgroundTasks(e), SessionCrons = SessionCrons(e)
            },
            "StopFailure" => new StopFailureHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                Error = AssistantMessageErrors.Parse(Str(e, "error")), ErrorRaw = Str(e, "error"),
                ErrorDetails = Str(e, "error_details"), LastAssistantMessage = Str(e, "last_assistant_message")
            },
            "SubagentStart" => new SubagentStartHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                AgentId = S("agent_id"), AgentType = S("agent_type")
            },
            "PreCompact" => new PreCompactHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                Trigger = S("trigger"), CustomInstructions = Str(e, "custom_instructions")
            },
            "PostCompact" => new PostCompactHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                Trigger = S("trigger"), CompactSummary = S("compact_summary")
            },
            "Notification" => new NotificationHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                Message = S("message"), Title = Str(e, "title"), NotificationType = S("notification_type")
            },
            "PermissionRequest" => new PermissionRequestHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ToolName = S("tool_name"), ToolInput = J("tool_input"),
                PermissionSuggestions = Raw(e, "permission_suggestions"),
                Suggestions = Suggestions(e), McpServer = McpServer(e)
            },
            "PermissionDenied" => new PermissionDeniedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                ToolName = S("tool_name"), ToolInput = J("tool_input"), ToolUseId = S("tool_use_id"),
                Reason = S("reason"), McpServer = McpServer(e)
            },
            "SessionStart" => new SessionStartHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                Source = S("source"), Model = Str(e, "model"), SessionTitle = Str(e, "session_title"),
                SecondsSinceLastResponse = Dbl(e, "seconds_since_last_response"),
                ContextTokens = Long(e, "context_tokens"),
                PromptCacheLikelyExpired = Bool(e, "prompt_cache_likely_expired"),
                EstimatedCacheWriteUsd = Dbl(e, "estimated_cache_write_usd")
            },
            "SessionEnd" => new SessionEndHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, Reason = S("reason")
            },
            "PreModelSwitch" => FillModelSwitch(new PreModelSwitchHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                FromModel = S("from_model"), ToModel = S("to_model"), Source = S("source")
            }, e),
            "PostModelSwitch" => FillModelSwitch(new PostModelSwitchHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                FromModel = S("from_model"), ToModel = S("to_model"), Source = S("source")
            }, e),
            "Setup" => new SetupHookInput { SessionId = sid, TranscriptPath = tp, Cwd = cwd, Trigger = S("trigger") },
            "TeammateIdle" => new TeammateIdleHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                TeammateName = S("teammate_name"), TeamName = S("team_name")
            },
            "TaskCreated" => FillTeamTask(new TaskCreatedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, TaskId = S("task_id"), TaskSubject = S("task_subject")
            }, e),
            "TaskCompleted" => FillTeamTask(new TaskCompletedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, TaskId = S("task_id"), TaskSubject = S("task_subject")
            }, e),
            "Elicitation" => new ElicitationHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                McpServerName = S("mcp_server_name"), Message = S("message"), Mode = Str(e, "mode"),
                Url = Str(e, "url"), ElicitationId = Str(e, "elicitation_id"), RequestedSchema = Raw(e, "requested_schema")
            },
            "ElicitationResult" => new ElicitationResultHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                McpServerName = S("mcp_server_name"), ElicitationId = Str(e, "elicitation_id"),
                Mode = Str(e, "mode"), Action = S("action"), Content = Raw(e, "content")
            },
            "ConfigChange" => new ConfigChangeHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, Source = S("source"), FilePath = Str(e, "file_path")
            },
            "WorktreeCreate" => new WorktreeCreateHookInput { SessionId = sid, TranscriptPath = tp, Cwd = cwd, Name = S("name") },
            "WorktreeRemove" => new WorktreeRemoveHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, WorktreePath = S("worktree_path")
            },
            "InstructionsLoaded" => new InstructionsLoadedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                FilePath = S("file_path"), MemoryType = S("memory_type"), LoadReason = S("load_reason"),
                Globs = StrList(e, "globs"), TriggerFilePath = Str(e, "trigger_file_path"),
                ParentFilePath = Str(e, "parent_file_path")
            },
            "CwdChanged" => new CwdChangedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, OldCwd = S("old_cwd"), NewCwd = S("new_cwd")
            },
            "FileChanged" => new FileChangedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, FilePath = S("file_path"), Event = S("event")
            },
            "DirectoryAdded" => new DirectoryAddedHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd, Directory = S("directory"), Source = S("source")
            },
            "MessageDisplay" => new MessageDisplayHookInput
            {
                SessionId = sid, TranscriptPath = tp, Cwd = cwd,
                TurnId = S("turn_id"), MessageId = S("message_id"), Index = Int(e, "index") ?? 0,
                Final = Bool(e, "final") ?? false, Delta = S("delta")
            },
            _ => new UnknownHookInput { SessionId = sid, TranscriptPath = tp, Cwd = cwd, HookEventName = eventName }
        };

        return parsed with
        {
            PermissionMode = Str(e, "permission_mode"),
            PromptId = Str(e, "prompt_id"),
            AgentId = Str(e, "agent_id"),
            AgentType = Str(e, "agent_type"),
            Effort = Obj(e, "effort") is { } eff && Str(eff, "level") is { } level ? new HookEffort(level) : null,
            Raw = e
        };
    }

    /// <summary>Parse <paramref name="input"/> and return it as <typeparamref name="T"/>, or <c>null</c> for another event.</summary>
    public static T? ParseAs<T>(JsonElement input) where T : BaseHookInput => Parse(input) as T;

    private static McpServerProvenance? McpServer(JsonElement e) =>
        Obj(e, "mcp_server") is { } m ? new McpServerProvenance(Str(m, "name") ?? string.Empty, Str(m, "source") ?? string.Empty) : null;

    private static IReadOnlyList<BackgroundTaskSummary>? BackgroundTasks(JsonElement e) =>
        ObjListOrNull(e, "background_tasks", t => new BackgroundTaskSummary
        {
            Id = Str(t, "id") ?? string.Empty,
            Type = Str(t, "type") ?? string.Empty,
            Status = Str(t, "status") ?? string.Empty,
            Description = Str(t, "description") ?? string.Empty,
            Command = Str(t, "command"),
            AgentType = Str(t, "agent_type"),
            Server = Str(t, "server"),
            Tool = Str(t, "tool"),
            Name = Str(t, "name")
        });

    private static IReadOnlyList<SessionCronSummary>? SessionCrons(JsonElement e) =>
        ObjListOrNull(e, "session_crons", c => new SessionCronSummary(
            Str(c, "id") ?? string.Empty, Str(c, "schedule") ?? string.Empty,
            Bool(c, "recurring") ?? false, Str(c, "prompt") ?? string.Empty));

    private static IReadOnlyList<PermissionUpdate>? Suggestions(JsonElement e) =>
        ObjListOrNull(e, "permission_suggestions", PermissionUpdate.FromControlProtocol);

    private static T FillModelSwitch<T>(T input, JsonElement e) where T : ModelSwitchHookInput =>
        (T)(input with
        {
            RequestedModel = Str(e, "requested_model"),
            ContextTokens = Long(e, "context_tokens") ?? 0,
            PromptCacheWarm = Bool(e, "prompt_cache_warm") ?? false,
            CacheTtl = Str(e, "cache_ttl"),
            EstimatedCacheWriteUsd = Dbl(e, "estimated_cache_write_usd") ?? 0,
            Pricing = Str(e, "pricing")
        });

    private static T FillTeamTask<T>(T input, JsonElement e) where T : TeamTaskHookInput =>
        (T)(input with
        {
            TaskDescription = Str(e, "task_description"),
            TeammateName = Str(e, "teammate_name"),
            TeamName = Str(e, "team_name")
        });
}

#endregion

#region Hook-specific outputs (TS 0.3.283)

/// <summary>Hook-specific output for Stop events. TS <c>StopHookSpecificOutput</c>.</summary>
public record StopHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.StopHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "Stop";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for SubagentStop events. TS <c>SubagentStopHookSpecificOutput</c>.</summary>
public record SubagentStopHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.SubagentStopHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "SubagentStop";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for UserPromptExpansion events. TS <c>UserPromptExpansionHookSpecificOutput</c>.</summary>
public record UserPromptExpansionHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.UserPromptExpansionHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "UserPromptExpansion";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }

    [JsonPropertyName("suppressOriginalPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SuppressOriginalPrompt { get; init; }
}

/// <summary>Hook-specific output for Setup events. TS <c>SetupHookSpecificOutput</c>.</summary>
public record SetupHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.SetupHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "Setup";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for PreModelSwitch events. TS <c>PreModelSwitchHookSpecificOutput</c>.</summary>
public record PreModelSwitchHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.PreModelSwitchHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PreModelSwitch";

    /// <summary><see cref="HookPermissionDecision.Allow"/>, <see cref="HookPermissionDecision.Deny"/> or <see cref="HookPermissionDecision.Ask"/>.</summary>
    [JsonPropertyName("permissionDecision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionDecision { get; init; }

    [JsonPropertyName("permissionDecisionReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionDecisionReason { get; init; }
}

/// <summary>Hook-specific output for PostModelSwitch events. TS <c>PostModelSwitchHookSpecificOutput</c>.</summary>
public record PostModelSwitchHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.PostModelSwitchHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PostModelSwitch";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for PostToolBatch events. TS <c>PostToolBatchHookSpecificOutput</c>.</summary>
public record PostToolBatchHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.PostToolBatchHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PostToolBatch";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for PermissionDenied events. TS <c>PermissionDeniedHookSpecificOutput</c>.</summary>
public record PermissionDeniedHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.PermissionDeniedHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PermissionDenied";

    /// <summary>Ask the model to retry the denied call.</summary>
    [JsonPropertyName("retry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retry { get; init; }
}

/// <summary>Hook-specific output for Elicitation events. TS <c>ElicitationHookSpecificOutput</c>.</summary>
public record ElicitationHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.ElicitationHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "Elicitation";

    /// <summary><c>accept</c>, <c>decline</c> or <c>cancel</c>.</summary>
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }
}

/// <summary>Hook-specific output for ElicitationResult events. TS <c>ElicitationResultHookSpecificOutput</c>.</summary>
public record ElicitationResultHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.ElicitationResultHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "ElicitationResult";

    /// <summary><c>accept</c>, <c>decline</c> or <c>cancel</c>.</summary>
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }
}

/// <summary>Hook-specific output for CwdChanged events. TS <c>CwdChangedHookSpecificOutput</c>.</summary>
public record CwdChangedHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.CwdChangedHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "CwdChanged";

    [JsonPropertyName("watchPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WatchPaths { get; init; }
}

/// <summary>Hook-specific output for FileChanged events. TS <c>FileChangedHookSpecificOutput</c>.</summary>
public record FileChangedHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.FileChangedHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "FileChanged";

    [JsonPropertyName("watchPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WatchPaths { get; init; }
}

/// <summary>Hook-specific output for WorktreeCreate events. TS <c>WorktreeCreateHookSpecificOutput</c>.</summary>
public record WorktreeCreateHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.WorktreeCreateHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "WorktreeCreate";

    /// <summary>Path of the created worktree (required).</summary>
    [JsonPropertyName("worktreePath")]
    public required string WorktreePath { get; init; }
}

/// <summary>Hook-specific output for MessageDisplay events. TS <c>MessageDisplayHookSpecificOutput</c>.</summary>
public record MessageDisplayHookSpecificOutput
{
    /// <summary>This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/> (unset members omitted; AOT-safe).</summary>
    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, SdkJsonContext.Default.MessageDisplayHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "MessageDisplay";

    /// <summary>Replacement text to display.</summary>
    [JsonPropertyName("displayContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayContent { get; init; }
}

#endregion

#region PermissionRequest decision

/// <summary>
/// Typed <c>decision</c> for <see cref="PermissionRequestHookSpecificOutput"/>
/// (TS union <c>{behavior:'allow',…} | {behavior:'deny',…}</c>).
/// </summary>
public abstract record PermissionRequestDecision
{
    /// <summary><c>allow</c> or <c>deny</c>.</summary>
    public abstract string Behavior { get; }

    /// <summary>The wire object (unset members omitted). Trim/AOT-safe.</summary>
    public abstract JsonElement ToJsonElement();

    /// <summary>Allow, optionally rewriting the input and adding permission rules.</summary>
    public static PermissionRequestAllowDecision Allow(JsonElement? updatedInput = null, IReadOnlyList<PermissionUpdate>? updatedPermissions = null) =>
        new(updatedInput, updatedPermissions);

    /// <summary>Deny, optionally with a message and interrupting the turn.</summary>
    public static PermissionRequestDenyDecision Deny(string? message = null, bool? interrupt = null) => new(message, interrupt);

    /// <summary>
    /// Parse a decision object; <c>null</c> when it isn't an object with a
    /// recognized <c>behavior</c>. Unknown permission update types are skipped.
    /// </summary>
    public static PermissionRequestDecision? Parse(JsonElement decision)
    {
        switch (Str(decision, "behavior"))
        {
            case "allow":
                return new PermissionRequestAllowDecision(
                    Raw(decision, "updatedInput"),
                    ObjListOrNull(decision, "updatedPermissions", PermissionUpdate.FromControlProtocol));
            case "deny":
                return new PermissionRequestDenyDecision(Str(decision, "message"), Bool(decision, "interrupt"));
            default:
                return null;
        }
    }
}

/// <summary>Allow decision for a PermissionRequest hook.</summary>
public sealed record PermissionRequestAllowDecision(
    JsonElement? UpdatedInput = null,
    IReadOnlyList<PermissionUpdate>? UpdatedPermissions = null) : PermissionRequestDecision
{
    /// <inheritdoc />
    public override string Behavior => "allow";

    /// <inheritdoc />
    public override JsonElement ToJsonElement()
    {
        var d = new Dictionary<string, object?> { ["behavior"] = Behavior };
        if (UpdatedInput is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } input)
            d["updatedInput"] = input;
        if (UpdatedPermissions != null)
            d["updatedPermissions"] = UpdatedPermissions.Select(p => p.ToDictionary()).ToList();
        return SdkJson.SerializeToElement(d);
    }
}

/// <summary>Deny decision for a PermissionRequest hook.</summary>
public sealed record PermissionRequestDenyDecision(
    string? Message = null,
    bool? Interrupt = null) : PermissionRequestDecision
{
    /// <inheritdoc />
    public override string Behavior => "deny";

    /// <inheritdoc />
    public override JsonElement ToJsonElement()
    {
        var d = new Dictionary<string, object?> { ["behavior"] = Behavior };
        if (Message != null)
            d["message"] = Message;
        if (Interrupt.HasValue)
            d["interrupt"] = Interrupt.Value;
        return SdkJson.SerializeToElement(d);
    }
}

#endregion
