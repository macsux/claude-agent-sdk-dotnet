// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/types.py

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claude.AgentSdk;

#region Enums

/// <summary>
/// Permission modes for controlling tool execution.
/// </summary>
public enum PermissionMode
{
    Default,
    AcceptEdits,
    Plan,
    BypassPermissions,
    /// <summary>Don't prompt for permissions; deny if not pre-approved (Python commit e30c742).</summary>
    DontAsk,
    /// <summary>Automatic permission mode (Python commit 841ee87).</summary>
    Auto
}

/// <summary>
/// Hook event types. Covers every event in the TypeScript SDK's
/// <c>HOOK_EVENTS</c> (0.3.283). The first ten members keep their original
/// numeric values; newer events are appended.
/// </summary>
/// <remarks>
/// For an event this SDK version doesn't list yet, use
/// <see cref="HookEventNames.Parse"/> (or <c>HooksBuilder.On(string, ...)</c>):
/// it returns a <see cref="HookEvent"/> value outside the named range that is
/// sent to the CLI under the exact name you passed.
/// </remarks>
public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    PostToolUseFailure,
    UserPromptSubmit,
    Stop,
    SubagentStop,
    PreCompact,
    Notification,
    SubagentStart,
    PermissionRequest,
    /// <summary>After a batch of parallel tool calls completes. TS 0.3.283.</summary>
    PostToolBatch,
    /// <summary>When a slash command or MCP prompt expands into a prompt. TS 0.3.283.</summary>
    UserPromptExpansion,
    /// <summary>Session start (startup, resume, clear, compact, fork). TS 0.3.283.</summary>
    SessionStart,
    /// <summary>Session end. TS 0.3.283.</summary>
    SessionEnd,
    /// <summary>Turn ended with an API error. TS 0.3.283.</summary>
    StopFailure,
    /// <summary>After compaction. TS 0.3.283.</summary>
    PostCompact,
    /// <summary>Before the model is switched. TS 0.3.283.</summary>
    PreModelSwitch,
    /// <summary>After the model is switched. TS 0.3.283.</summary>
    PostModelSwitch,
    /// <summary>A tool call was denied by the permission system. TS 0.3.283.</summary>
    PermissionDenied,
    /// <summary>Repository setup (init / maintenance). TS 0.3.283.</summary>
    Setup,
    /// <summary>A teammate went idle. TS 0.3.283.</summary>
    TeammateIdle,
    /// <summary>A team task was created. TS 0.3.283.</summary>
    TaskCreated,
    /// <summary>A team task was completed. TS 0.3.283.</summary>
    TaskCompleted,
    /// <summary>An MCP server requested user input (elicitation). TS 0.3.283.</summary>
    Elicitation,
    /// <summary>The user answered an MCP elicitation. TS 0.3.283.</summary>
    ElicitationResult,
    /// <summary>A settings file or skills changed. TS 0.3.283.</summary>
    ConfigChange,
    /// <summary>A worktree should be created (hook returns its path). TS 0.3.283.</summary>
    WorktreeCreate,
    /// <summary>A worktree was removed. TS 0.3.283.</summary>
    WorktreeRemove,
    /// <summary>A CLAUDE.md / memory file was loaded. TS 0.3.283.</summary>
    InstructionsLoaded,
    /// <summary>The working directory changed. TS 0.3.283.</summary>
    CwdChanged,
    /// <summary>A watched file changed. TS 0.3.283.</summary>
    FileChanged,
    /// <summary>A directory was added to the session. TS 0.3.283.</summary>
    DirectoryAdded,
    /// <summary>Assistant text is about to be displayed. TS 0.3.283.</summary>
    MessageDisplay
}

/// <summary>
/// Effort level for thinking depth.
/// </summary>
public enum EffortLevel
{
    Low,
    Medium,
    High,
    /// <summary>Extended reasoning depth (Opus 4.7 only; falls back to high on other models). Python commit 04a39ac.</summary>
    XHigh,
    Max
}

/// <summary>
/// Setting sources to load.
/// </summary>
public enum SettingSource
{
    User,
    Project,
    Local
}

/// <summary>
/// Permission behavior options.
/// </summary>
public enum PermissionBehavior
{
    Allow,
    Deny,
    Ask
}

/// <summary>
/// Permission update destinations.
/// </summary>
public enum PermissionUpdateDestination
{
    UserSettings,
    ProjectSettings,
    LocalSettings,
    Session,
    /// <summary>Rule came from a CLI argument (e.g. <c>--allowedTools</c>). TS 0.3.283.</summary>
    CliArg
}

/// <summary>
/// Permission update types.
/// </summary>
public enum PermissionUpdateType
{
    AddRules,
    ReplaceRules,
    RemoveRules,
    SetMode,
    AddDirectories,
    RemoveDirectories
}

/// <summary>
/// Assistant message error types (TS <c>SDKAssistantMessageError</c>).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> covers both the CLI's literal <c>"unknown"</c> and any
/// value this SDK version doesn't recognize; the exact wire string is kept on
/// <see cref="AssistantMessage.ErrorRaw"/>. Values after <see cref="Unknown"/>
/// were appended for TS 0.3.283 parity, so existing numeric values are stable.
/// </remarks>
public enum AssistantMessageError
{
    AuthenticationFailed,
    BillingError,
    RateLimit,
    InvalidRequest,
    ServerError,
    Unknown,
    OauthOrgNotAllowed,
    AccountOnHold,
    VerificationRequired,
    Overloaded,
    ModelNotFound,
    MaxOutputTokens,
    CloudCredentialError
}

/// <summary>
/// Helper methods for enum string conversion.
/// </summary>
internal static class EnumHelpers
{
    public static string ToJsonString(this PermissionMode mode) => mode switch
    {
        PermissionMode.Default => "default",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.DontAsk => "dontAsk",
        PermissionMode.Auto => "auto",
        _ => mode.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this SettingSource source) => source switch
    {
        SettingSource.User => "user",
        SettingSource.Project => "project",
        SettingSource.Local => "local",
        _ => source.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this PermissionBehavior behavior) => behavior switch
    {
        PermissionBehavior.Allow => "allow",
        PermissionBehavior.Deny => "deny",
        PermissionBehavior.Ask => "ask",
        _ => behavior.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this PermissionUpdateDestination dest) => dest switch
    {
        PermissionUpdateDestination.UserSettings => "userSettings",
        PermissionUpdateDestination.ProjectSettings => "projectSettings",
        PermissionUpdateDestination.LocalSettings => "localSettings",
        PermissionUpdateDestination.Session => "session",
        PermissionUpdateDestination.CliArg => "cliArg",
        _ => dest.ToString()
    };

    public static string ToJsonString(this PermissionUpdateType type) => type switch
    {
        PermissionUpdateType.AddRules => "addRules",
        PermissionUpdateType.ReplaceRules => "replaceRules",
        PermissionUpdateType.RemoveRules => "removeRules",
        PermissionUpdateType.SetMode => "setMode",
        PermissionUpdateType.AddDirectories => "addDirectories",
        PermissionUpdateType.RemoveDirectories => "removeDirectories",
        _ => type.ToString()
    };

    public static string ToJsonString(this EffortLevel effort) => effort switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.High => "high",
        EffortLevel.XHigh => "xhigh",
        EffortLevel.Max => "max",
        _ => effort.ToString().ToLowerInvariant()
    };

    public static string ToJsonString(this HookEvent hookEvent) => HookEventNames.ToWireName(hookEvent);
}

#endregion

#region Content Blocks

/// <summary>
/// Base class for content blocks.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingBlock), "redacted_thinking")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
[JsonDerivedType(typeof(ServerToolUseBlock), "server_tool_use")]
[JsonDerivedType(typeof(ServerToolResultBlock), "server_tool_result")]
public abstract record ContentBlock;

/// <summary>
/// Text content block.
/// </summary>
public record TextBlock(
    [property: JsonPropertyName("text")] string Text
) : ContentBlock;

/// <summary>
/// Thinking content block.
/// </summary>
public record ThinkingBlock(
    [property: JsonPropertyName("thinking")] string Thinking,
    [property: JsonPropertyName("signature")] string Signature
) : ContentBlock;

/// <summary>
/// Tool use content block.
/// </summary>
public record ToolUseBlock(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] JsonElement Input
) : ContentBlock;

/// <summary>
/// Tool result content block.
/// </summary>
public record ToolResultBlock(
    [property: JsonPropertyName("tool_use_id")] string ToolUseId,
    [property: JsonPropertyName("content")] JsonElement? Content = null,
    [property: JsonPropertyName("is_error")] bool? IsError = null
) : ContentBlock;

/// <summary>
/// Well-known server-side tool names. The wire value is a free string;
/// these constants document the values produced by the API today.
/// Python commit 6ab97b4.
/// </summary>
public static class ServerToolName
{
    public const string Advisor = "advisor";
    public const string WebSearch = "web_search";
    public const string WebFetch = "web_fetch";
    public const string CodeExecution = "code_execution";
    public const string BashCodeExecution = "bash_code_execution";
    public const string TextEditorCodeExecution = "text_editor_code_execution";
    public const string ToolSearchToolRegex = "tool_search_tool_regex";
    public const string ToolSearchToolBm25 = "tool_search_tool_bm25";
}

/// <summary>
/// Server-side tool use block (e.g. advisor, web_search, web_fetch).
/// These are tools the API executes server-side on the model's behalf, so they
/// appear in the message stream alongside regular tool_use blocks but the
/// caller never needs to return a result. Python commit 6ab97b4.
/// </summary>
public record ServerToolUseBlock(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] JsonElement Input
) : ContentBlock;

/// <summary>
/// Result block returned for a server-side tool call. Python commit 6ab97b4.
/// </summary>
public record ServerToolResultBlock(
    [property: JsonPropertyName("tool_use_id")] string ToolUseId,
    [property: JsonPropertyName("content")] JsonElement Content
) : ContentBlock
{
    /// <summary>
    /// The wire block type this result was parsed from, e.g.
    /// <c>advisor_tool_result</c>, <c>web_search_tool_result</c>,
    /// <c>web_fetch_tool_result</c>, <c>code_execution_tool_result</c>,
    /// <c>bash_code_execution_tool_result</c>,
    /// <c>text_editor_code_execution_tool_result</c> or
    /// <c>tool_search_tool_result</c>. <c>null</c> when constructed directly.
    /// </summary>
    [JsonIgnore]
    public string? ResultType { get; init; }
}

#endregion

#region Messages

/// <summary>
/// Base class for messages.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(StreamEvent), "stream_event")]
public abstract record Message
{
    /// <summary>
    /// The complete, unmodified JSON frame this message was parsed from (TS
    /// SDK parity: every <c>SDKMessage</c> is the raw object). Use it to read
    /// fields this SDK version doesn't model. <c>default</c>
    /// (<see cref="JsonValueKind.Undefined"/>) for messages constructed in code.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; init; }
}

/// <summary>
/// User message.
/// </summary>
public record UserMessage : Message
{
    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    /// <summary>
    /// Structured tool result the CLI attaches to tool-result user messages.
    /// Python: <c>UserMessage.tool_use_result</c>.
    /// </summary>
    [JsonPropertyName("tool_use_result")]
    public JsonElement? ToolUseResult { get; init; }

    /// <summary>
    /// Provenance of this message — see <see cref="MessageOrigin"/>. <c>null</c>
    /// when the CLI did not attribute it. Python: <c>UserMessage.origin</c>.
    /// </summary>
    [JsonPropertyName("origin")]
    public MessageOrigin? Origin { get; init; }

    /// <summary>Session this message belongs to. TS <c>SDKUserMessage.session_id</c>.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>True for messages the CLI synthesized (not typed by a user). TS <c>isSynthetic</c>.</summary>
    [JsonPropertyName("isSynthetic")]
    public bool? IsSynthetic { get; init; }

    /// <summary>
    /// True when this is a replay of an earlier user message (TS
    /// <c>SDKUserMessageReplay</c>, e.g. on resume) rather than a live frame.
    /// </summary>
    [JsonPropertyName("isReplay")]
    public bool? IsReplay { get; init; }

    /// <summary>Queue priority: <c>"now"</c>, <c>"next"</c> or <c>"later"</c>. TS <c>priority</c>.</summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    /// <summary>ISO-8601 timestamp. TS <c>timestamp</c>.</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    /// <summary>Whether this message should trigger a model query. TS <c>shouldQuery</c>.</summary>
    [JsonPropertyName("shouldQuery")]
    public bool? ShouldQuery { get; init; }

    /// <summary>True when the prompt was delivered verbatim (no @-mention / slash expansion). TS <c>client_composed</c>.</summary>
    [JsonPropertyName("client_composed")]
    public bool? ClientComposed { get; init; }

    /// <summary>File attachments on a replayed message, raw. TS <c>SDKUserMessageReplay.file_attachments</c>.</summary>
    [JsonPropertyName("file_attachments")]
    public JsonElement? FileAttachments { get; init; }

    /// <summary>Pasted content blocks, raw. TS <c>pasted_content</c>.</summary>
    [JsonPropertyName("pasted_content")]
    public JsonElement? PastedContent { get; init; }

    /// <summary>Inline paste references. TS <c>inline_pastes</c>.</summary>
    [JsonPropertyName("inline_pastes")]
    public IReadOnlyList<string>? InlinePastes { get; init; }

    /// <summary>Sub-agent type, when the message belongs to a sub-agent. TS <c>subagent_type</c>.</summary>
    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>Sub-agent task description. TS <c>task_description</c>.</summary>
    [JsonPropertyName("task_description")]
    public string? TaskDescription { get; init; }

    /// <summary>
    /// Gets the content as a string if it's a simple text message.
    /// </summary>
    public string? GetTextContent()
    {
        if (Content.ValueKind == JsonValueKind.String)
            return Content.GetString();
        return null;
    }

    /// <summary>
    /// Gets the content blocks if the content is an array. Like Python's
    /// parser, only <c>text</c>, <c>tool_use</c> and <c>tool_result</c> blocks
    /// are materialized; other block types (images, documents, ...) are skipped.
    /// </summary>
    public IReadOnlyList<ContentBlock>? GetContentBlocks()
    {
        if (Content.ValueKind != JsonValueKind.Array)
            return null;
        return Internal.MessageParser.ParseUserContentBlocks(Content);
    }
}

/// <summary>
/// Assistant message with content blocks.
/// </summary>
public record AssistantMessage : Message
{
    [JsonPropertyName("content")]
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    [JsonPropertyName("error")]
    public AssistantMessageError? Error { get; init; }

    /// <summary>API usage stats for this message. Python commit fc82420.</summary>
    [JsonPropertyName("usage")]
    public JsonElement? Usage { get; init; }

    /// <summary>Message identifier from the API. Python commit 24b9b68.</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    /// <summary>Reason the model stopped (e.g. "end_turn", "tool_use"). Python commit 24b9b68.</summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    /// <summary>Session ID this message belongs to. Python commit 24b9b68.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>Unique ID for this message. Python commit 24b9b68.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>
    /// The exact <c>error</c> string from the wire, preserved even when
    /// <see cref="Error"/> is <see cref="AssistantMessageError.Unknown"/>.
    /// TS <c>SDKAssistantMessageError</c>.
    /// </summary>
    [JsonPropertyName("error_raw")]
    public string? ErrorRaw { get; init; }

    /// <summary>API request id. TS <c>request_id</c>.</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>UUID of the user message this reply answers. TS <c>user_message_uuid</c>.</summary>
    [JsonPropertyName("user_message_uuid")]
    public string? UserMessageUuid { get; init; }

    /// <summary>UUIDs of the user messages this reply answers (batched turns). TS <c>user_message_uuids</c>.</summary>
    [JsonPropertyName("user_message_uuids")]
    public IReadOnlyList<string>? UserMessageUuids { get; init; }

    /// <summary>Why a turn was resumed. TS <c>resume_reason</c>.</summary>
    [JsonPropertyName("resume_reason")]
    public string? ResumeReason { get; init; }

    /// <summary>True when resumed from incomplete thinking. TS <c>resumed_from_incomplete_thinking</c>.</summary>
    [JsonPropertyName("resumed_from_incomplete_thinking")]
    public bool? ResumedFromIncompleteThinking { get; init; }

    /// <summary>UUIDs of earlier assistant messages this one replaces. TS <c>supersedes</c>.</summary>
    [JsonPropertyName("supersedes")]
    public IReadOnlyList<string>? Supersedes { get; init; }

    /// <summary>True when the message was cut short by an abort. TS <c>aborted</c>.</summary>
    [JsonPropertyName("aborted")]
    public bool? Aborted { get; init; }

    /// <summary>Sub-agent type, when produced by a sub-agent. TS <c>subagent_type</c>.</summary>
    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>Sub-agent task description. TS <c>task_description</c>.</summary>
    [JsonPropertyName("task_description")]
    public string? TaskDescription { get; init; }

    /// <summary>ISO-8601 timestamp. TS <c>timestamp</c>.</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    /// <summary>Context-window usage snapshot after this message. TS <c>context_usage</c>.</summary>
    [JsonPropertyName("context_usage")]
    public SdkContextUsage? ContextUsage { get; init; }

    /// <summary>Session cost / rate-limit report. TS <c>usage_report</c>.</summary>
    [JsonPropertyName("usage_report")]
    public SdkUsageReport? UsageReport { get; init; }

    /// <summary>Stop sequence that ended generation, if any (inner <c>message.stop_sequence</c>).</summary>
    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; init; }
}

/// <summary>
/// System message with metadata.
/// </summary>
public record SystemMessage : Message
{
    [JsonPropertyName("subtype")]
    public required string Subtype { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

/// <summary>
/// Result message with cost and usage information.
/// </summary>
public record ResultMessage : Message
{
    [JsonPropertyName("subtype")]
    public required string Subtype { get; init; }

    [JsonPropertyName("duration_ms")]
    public required int DurationMs { get; init; }

    [JsonPropertyName("duration_api_ms")]
    public required int DurationApiMs { get; init; }

    [JsonPropertyName("is_error")]
    public required bool IsError { get; init; }

    [JsonPropertyName("num_turns")]
    public required int NumTurns { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; init; }

    [JsonPropertyName("usage")]
    public JsonElement? Usage { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("structured_output")]
    public JsonElement? StructuredOutput { get; init; }

    /// <summary>Reason the run stopped, when applicable. Python commit 7219299.</summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    /// <summary>Tool call deferred by a PreToolUse hook returning "defer". Python commit f5a1b67.</summary>
    [JsonPropertyName("deferred_tool_use")]
    public DeferredToolUse? DeferredToolUse { get; init; }

    /// <summary>Errors collected during the run. Python commit f9fc8e0.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<string>? Errors { get; init; }

    /// <summary>HTTP status code of the failing API call (e.g. 429, 500, 529). Python commit b80d244.</summary>
    [JsonPropertyName("api_error_status")]
    public int? ApiErrorStatus { get; init; }

    /// <summary>Unique ID for this result. Python commit 24b9b68.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>
    /// Per-model usage keyed by model id (the CLI's camelCase <c>modelUsage</c>).
    /// Python: <c>ResultMessage.model_usage</c>.
    /// </summary>
    [JsonPropertyName("modelUsage")]
    public IReadOnlyDictionary<string, ModelUsage>? ModelUsage { get; init; }

    /// <summary>Tool calls denied during the run, passed through raw. Python: <c>permission_denials</c>.</summary>
    [JsonPropertyName("permission_denials")]
    public JsonElement? PermissionDenials { get; init; }

    /// <summary>
    /// Why the query loop terminated (e.g. <c>"completed"</c>,
    /// <c>"max_turns"</c>, <c>"aborted_streaming"</c>); <c>null</c> when not
    /// reported. Python: <c>ResultMessage.terminal_reason</c>.
    /// </summary>
    [JsonPropertyName("terminal_reason")]
    public string? TerminalReason { get; init; }

    /// <summary>
    /// Origin of the user message that triggered this turn — lets a streaming
    /// consumer tell the result of its own prompt from results of injected
    /// turns. Python: <c>ResultMessage.origin</c>.
    /// </summary>
    [JsonPropertyName("origin")]
    public MessageOrigin? Origin { get; init; }

    /// <summary>Typed view of <see cref="PermissionDenials"/> (TS <c>SDKPermissionDenial[]</c>); empty when absent.</summary>
    public IReadOnlyList<PermissionDenial> GetPermissionDenials() =>
        Internal.TsParsing.ParsePermissionDenials(PermissionDenials);

    /// <summary>Turns still queued behind this one. TS <c>queued_turn_count</c>.</summary>
    [JsonPropertyName("queued_turn_count")]
    public int? QueuedTurnCount { get; init; }

    /// <summary>Index of this result within a batched turn. TS <c>result_index</c>.</summary>
    [JsonPropertyName("result_index")]
    public int? ResultIndex { get; init; }

    /// <summary>Fast-mode state; see <see cref="FastModeStates"/>. TS <c>fast_mode_state</c>.</summary>
    [JsonPropertyName("fast_mode_state")]
    public string? FastModeState { get; init; }

    /// <summary>Why fast mode is off; see <see cref="FastModeDisabledReasons"/>. TS <c>fast_mode_disabled_reason</c>.</summary>
    [JsonPropertyName("fast_mode_disabled_reason")]
    public string? FastModeDisabledReason { get; init; }

    /// <summary>Why the session failed to start; see <see cref="StartupFailureReasons"/>. TS <c>startup_failure_reason</c>.</summary>
    [JsonPropertyName("startup_failure_reason")]
    public string? StartupFailureReason { get; init; }

    /// <summary>UUID of the user message that triggered this turn. TS <c>user_message_uuid</c>.</summary>
    [JsonPropertyName("user_message_uuid")]
    public string? UserMessageUuid { get; init; }

    /// <summary>UUIDs of the user messages this turn answered. TS <c>user_message_uuids</c>.</summary>
    [JsonPropertyName("user_message_uuids")]
    public IReadOnlyList<string>? UserMessageUuids { get; init; }

    /// <summary>Why the turn was resumed. TS <c>resume_reason</c>.</summary>
    [JsonPropertyName("resume_reason")]
    public string? ResumeReason { get; init; }

    /// <summary>Local slash command that produced this result. TS <c>local_command</c>.</summary>
    [JsonPropertyName("local_command")]
    public string? LocalCommand { get; init; }

    /// <summary>Time to first token, in ms. TS <c>ttft_ms</c>.</summary>
    [JsonPropertyName("ttft_ms")]
    public double? TtftMs { get; init; }

    /// <summary>Time to first streamed token, in ms. TS <c>ttft_stream_ms</c>.</summary>
    [JsonPropertyName("ttft_stream_ms")]
    public double? TtftStreamMs { get; init; }

    /// <summary>Time until the API request was sent, in ms. TS <c>time_to_request_ms</c>.</summary>
    [JsonPropertyName("time_to_request_ms")]
    public double? TimeToRequestMs { get; init; }
}

/// <summary>
/// Tool use that was deferred by a PreToolUse hook returning "defer".
/// Python commit f5a1b67.
/// </summary>
public record DeferredToolUse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] JsonElement Input
);

/// <summary>
/// Stream event for partial message updates during streaming.
/// </summary>
public record StreamEvent : Message
{
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("event")]
    public required JsonElement Event { get; init; }

    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    /// <summary>Time to first token, in ms. TS <c>ttft_ms</c>.</summary>
    [JsonPropertyName("ttft_ms")]
    public double? TtftMs { get; init; }

    /// <summary>UUID of the user message being answered. TS <c>user_message_uuid</c>.</summary>
    [JsonPropertyName("user_message_uuid")]
    public string? UserMessageUuid { get; init; }

    /// <summary>UUIDs of the user messages being answered. TS <c>user_message_uuids</c>.</summary>
    [JsonPropertyName("user_message_uuids")]
    public IReadOnlyList<string>? UserMessageUuids { get; init; }

    /// <summary>Why the turn was resumed. TS <c>resume_reason</c>.</summary>
    [JsonPropertyName("resume_reason")]
    public string? ResumeReason { get; init; }
}

#endregion

#region Permission Types

/// <summary>
/// Permission rule value.
/// </summary>
public record PermissionRuleValue(
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("rule_content")] string? RuleContent = null
);

/// <summary>
/// Permission update configuration.
/// </summary>
public record PermissionUpdate(
    [property: JsonPropertyName("type")] PermissionUpdateType Type,
    [property: JsonPropertyName("rules")] IReadOnlyList<PermissionRuleValue>? Rules = null,
    [property: JsonPropertyName("behavior")] PermissionBehavior? Behavior = null,
    [property: JsonPropertyName("mode")] PermissionMode? Mode = null,
    [property: JsonPropertyName("directories")] IReadOnlyList<string>? Directories = null,
    [property: JsonPropertyName("destination")] PermissionUpdateDestination? Destination = null
)
{
    /// <summary>
    /// Parse the control-protocol wire format (inverse of <see cref="ToDictionary"/>;
    /// Python: <c>PermissionUpdate.from_dict</c>). Returns null for an update type
    /// this SDK version doesn't know, so one unfamiliar suggestion from a newer CLI
    /// can't fail the whole permission request. Unknown enum values in optional
    /// fields are dropped.
    /// </summary>
    public static PermissionUpdate? FromControlProtocol(JsonElement data)
    {
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        PermissionUpdateType? type = Str(data, "type") switch
        {
            "addRules" => PermissionUpdateType.AddRules,
            "replaceRules" => PermissionUpdateType.ReplaceRules,
            "removeRules" => PermissionUpdateType.RemoveRules,
            "setMode" => PermissionUpdateType.SetMode,
            "addDirectories" => PermissionUpdateType.AddDirectories,
            "removeDirectories" => PermissionUpdateType.RemoveDirectories,
            _ => null
        };
        if (type == null)
            return null;

        List<PermissionRuleValue>? rules = null;
        if (data.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            rules = r.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && Str(x, "toolName") != null)
                .Select(x => new PermissionRuleValue(Str(x, "toolName")!, Str(x, "ruleContent")))
                .ToList();
        }

        List<string>? directories = null;
        if (data.TryGetProperty("directories", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            directories = d.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList();
        }

        return new PermissionUpdate(
            type.Value,
            rules,
            Str(data, "behavior") switch
            {
                "allow" => PermissionBehavior.Allow,
                "deny" => PermissionBehavior.Deny,
                "ask" => PermissionBehavior.Ask,
                _ => null
            },
            Str(data, "mode") switch
            {
                "default" => PermissionMode.Default,
                "acceptEdits" => PermissionMode.AcceptEdits,
                "plan" => PermissionMode.Plan,
                "bypassPermissions" => PermissionMode.BypassPermissions,
                "dontAsk" => PermissionMode.DontAsk,
                "auto" => PermissionMode.Auto,
                _ => null
            },
            directories,
            Str(data, "destination") switch
            {
                "userSettings" => PermissionUpdateDestination.UserSettings,
                "projectSettings" => PermissionUpdateDestination.ProjectSettings,
                "localSettings" => PermissionUpdateDestination.LocalSettings,
                "session" => PermissionUpdateDestination.Session,
                "cliArg" => PermissionUpdateDestination.CliArg,
                _ => null
            });
    }

    /// <summary>
    /// Convert to dictionary format matching TypeScript control protocol.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = Type.ToJsonString()
        };

        if (Destination.HasValue)
            result["destination"] = Destination.Value.ToJsonString();

        if (Type is PermissionUpdateType.AddRules or PermissionUpdateType.ReplaceRules or PermissionUpdateType.RemoveRules)
        {
            if (Rules != null)
            {
                result["rules"] = Rules.Select(r => new Dictionary<string, object?>
                {
                    ["toolName"] = r.ToolName,
                    ["ruleContent"] = r.RuleContent
                }).ToList();
            }
            if (Behavior.HasValue)
                result["behavior"] = Behavior.Value.ToJsonString();
        }
        else if (Type == PermissionUpdateType.SetMode)
        {
            if (Mode.HasValue)
                result["mode"] = Mode.Value.ToJsonString();
        }
        else if (Type is PermissionUpdateType.AddDirectories or PermissionUpdateType.RemoveDirectories)
        {
            if (Directories != null)
                result["directories"] = Directories.ToList();
        }

        return result;
    }

    /// <summary>
    /// Construct a PermissionUpdate from the control protocol dict format
    /// (inverse of <see cref="ToDictionary"/>). Python commit 6597529.
    /// </summary>
    public static PermissionUpdate FromDictionary(IReadOnlyDictionary<string, object?> data)
    {
        var typeStr = data["type"]?.ToString() ?? throw new ArgumentException("'type' is required");
        var type = typeStr switch
        {
            "addRules" => PermissionUpdateType.AddRules,
            "replaceRules" => PermissionUpdateType.ReplaceRules,
            "removeRules" => PermissionUpdateType.RemoveRules,
            "setMode" => PermissionUpdateType.SetMode,
            "addDirectories" => PermissionUpdateType.AddDirectories,
            "removeDirectories" => PermissionUpdateType.RemoveDirectories,
            _ => throw new ArgumentException($"Unknown permission update type: {typeStr}")
        };

        IReadOnlyList<PermissionRuleValue>? rules = null;
        if (data.TryGetValue("rules", out var rawRules) && rawRules is not null)
        {
            var list = new List<PermissionRuleValue>();
            switch (rawRules)
            {
                case IEnumerable<IDictionary<string, object?>> typedRules:
                    foreach (var r in typedRules)
                        list.Add(new PermissionRuleValue(
                            r["toolName"]?.ToString() ?? string.Empty,
                            r.TryGetValue("ruleContent", out var rc) ? rc?.ToString() : null));
                    break;
                case JsonElement je when je.ValueKind == JsonValueKind.Array:
                    foreach (var item in je.EnumerateArray())
                        list.Add(new PermissionRuleValue(
                            item.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? string.Empty : string.Empty,
                            item.TryGetProperty("ruleContent", out var rc) && rc.ValueKind != JsonValueKind.Null ? rc.GetString() : null));
                    break;
                case System.Collections.IEnumerable enumerable:
                    foreach (var item in enumerable)
                    {
                        if (item is IDictionary<string, object?> d)
                            list.Add(new PermissionRuleValue(
                                d["toolName"]?.ToString() ?? string.Empty,
                                d.TryGetValue("ruleContent", out var rc) ? rc?.ToString() : null));
                    }
                    break;
            }
            rules = list;
        }

        PermissionBehavior? behavior = null;
        if (data.TryGetValue("behavior", out var rawBehavior) && rawBehavior is not null)
        {
            behavior = rawBehavior.ToString() switch
            {
                "allow" => PermissionBehavior.Allow,
                "deny" => PermissionBehavior.Deny,
                "ask" => PermissionBehavior.Ask,
                _ => null
            };
        }

        PermissionMode? mode = null;
        if (data.TryGetValue("mode", out var rawMode) && rawMode is not null)
        {
            mode = rawMode.ToString() switch
            {
                "default" => PermissionMode.Default,
                "acceptEdits" => PermissionMode.AcceptEdits,
                "plan" => PermissionMode.Plan,
                "bypassPermissions" => PermissionMode.BypassPermissions,
                "dontAsk" => PermissionMode.DontAsk,
                "auto" => PermissionMode.Auto,
                _ => null
            };
        }

        IReadOnlyList<string>? directories = null;
        if (data.TryGetValue("directories", out var rawDirs) && rawDirs is not null)
        {
            directories = rawDirs switch
            {
                IEnumerable<string> ss => ss.ToList(),
                JsonElement je when je.ValueKind == JsonValueKind.Array =>
                    je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList(),
                System.Collections.IEnumerable e => e.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).ToList(),
                _ => null
            };
        }

        PermissionUpdateDestination? destination = null;
        if (data.TryGetValue("destination", out var rawDest) && rawDest is not null)
        {
            destination = rawDest.ToString() switch
            {
                "userSettings" => PermissionUpdateDestination.UserSettings,
                "projectSettings" => PermissionUpdateDestination.ProjectSettings,
                "localSettings" => PermissionUpdateDestination.LocalSettings,
                "session" => PermissionUpdateDestination.Session,
                "cliArg" => PermissionUpdateDestination.CliArg,
                _ => null
            };
        }

        return new PermissionUpdate(type, rules, behavior, mode, directories, destination);
    }
}

/// <summary>
/// Context information for tool permission callbacks.
/// Expanded in Python commits 3caf665 and fe0cff3.
/// </summary>
public record ToolPermissionContext(
    object? Signal = null,
    IReadOnlyList<PermissionUpdate>? Suggestions = null,
    string? ToolUseId = null,
    string? AgentId = null,
    string? BlockedPath = null,
    string? DecisionReason = null,
    string? Title = null,
    string? DisplayName = null,
    string? Description = null
);

/// <summary>
/// Base class for permission results.
/// </summary>
public abstract record PermissionResult;

/// <summary>
/// Allow permission result.
/// </summary>
public record PermissionResultAllow(
    JsonElement? UpdatedInput = null,
    IReadOnlyList<PermissionUpdate>? UpdatedPermissions = null
) : PermissionResult
{
    public string Behavior => "allow";
}

/// <summary>
/// Deny permission result.
/// </summary>
public record PermissionResultDeny(
    string Message = "",
    bool Interrupt = false
) : PermissionResult
{
    public string Behavior => "deny";
}

/// <summary>
/// Delegate for tool permission callbacks.
/// </summary>
public delegate Task<PermissionResult> CanUseToolCallback(
    string toolName,
    JsonElement input,
    ToolPermissionContext context,
    CancellationToken cancellationToken = default
);

#endregion

#region Hook Types

/// <summary>
/// Base hook input fields.
/// </summary>
public record BaseHookInput
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("transcript_path")]
    public required string TranscriptPath { get; init; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    /// <summary>Identifier of the prompt that triggered this hook. TS <c>BaseHookInput.prompt_id</c>.</summary>
    [JsonPropertyName("prompt_id")]
    public string? PromptId { get; init; }

    /// <summary>
    /// Sub-agent identifier when this hook fires inside a Task-spawned
    /// sub-agent. Python commit 2f1fd38 (tool hooks); on every hook in TS 0.3.283.
    /// </summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    /// <summary>Agent type name (e.g. "general-purpose"). Python commit 2f1fd38; TS <c>BaseHookInput.agent_type</c>.</summary>
    [JsonPropertyName("agent_type")]
    public string? AgentType { get; init; }

    /// <summary>Effort level in force for this turn. TS <c>BaseHookInput.effort</c>.</summary>
    [JsonPropertyName("effort")]
    public HookEffort? Effort { get; init; }

    /// <summary>
    /// The full hook input as received from the CLI, set by
    /// <see cref="HookInput.Parse"/>. <c>default</c> when constructed in code.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; init; }
}

/// <summary>
/// Input data for PreToolUse hook events.
/// </summary>
public record PreToolUseHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PreToolUse";

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    /// <summary>MCP server that provides the tool, for MCP tools. TS <c>mcp_server</c>.</summary>
    [JsonPropertyName("mcp_server")]
    public McpServerProvenance? McpServer { get; init; }
}

/// <summary>
/// Input data for PostToolUse hook events.
/// </summary>
public record PostToolUseHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PostToolUse";

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_response")]
    public required JsonElement ToolResponse { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    /// <summary>Tool execution time in milliseconds. TS <c>duration_ms</c>.</summary>
    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    /// <summary>MCP server that provides the tool, for MCP tools. TS <c>mcp_server</c>.</summary>
    [JsonPropertyName("mcp_server")]
    public McpServerProvenance? McpServer { get; init; }
}

/// <summary>
/// Input data for PostToolUseFailure hook events.
/// </summary>
public record PostToolUseFailureHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PostToolUseFailure";

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("is_interrupt")]
    public bool? IsInterrupt { get; init; }

    /// <summary>Tool execution time in milliseconds. TS <c>duration_ms</c>.</summary>
    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    /// <summary>MCP server that provides the tool, for MCP tools. TS <c>mcp_server</c>.</summary>
    [JsonPropertyName("mcp_server")]
    public McpServerProvenance? McpServer { get; init; }
}

/// <summary>
/// Input data for UserPromptSubmit hook events.
/// </summary>
public record UserPromptSubmitHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "UserPromptSubmit";

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>
    /// Where the prompt came from: <c>user</c>, <c>sdk</c>, <c>system</c>,
    /// <c>loop_wakeup</c>, <c>schedule_wakeup</c>, <c>poll_event</c>. TS <c>source</c>.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Current session title. TS <c>session_title</c>.</summary>
    [JsonPropertyName("session_title")]
    public string? SessionTitle { get; init; }
}

/// <summary>
/// Input data for Stop hook events.
/// </summary>
public record StopHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "Stop";

    [JsonPropertyName("stop_hook_active")]
    public required bool StopHookActive { get; init; }

    /// <summary>Text of the last assistant message. TS <c>last_assistant_message</c>.</summary>
    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }

    /// <summary>Background tasks still running. TS <c>background_tasks</c>.</summary>
    [JsonPropertyName("background_tasks")]
    public IReadOnlyList<BackgroundTaskSummary>? BackgroundTasks { get; init; }

    /// <summary>Session cron jobs. TS <c>session_crons</c>.</summary>
    [JsonPropertyName("session_crons")]
    public IReadOnlyList<SessionCronSummary>? SessionCrons { get; init; }
}

/// <summary>
/// Input data for SubagentStop hook events.
/// </summary>
public record SubagentStopHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "SubagentStop";

    [JsonPropertyName("stop_hook_active")]
    public required bool StopHookActive { get; init; }

    [JsonPropertyName("agent_id")]
    public new required string AgentId { get; init; }

    [JsonPropertyName("agent_transcript_path")]
    public required string AgentTranscriptPath { get; init; }

    [JsonPropertyName("agent_type")]
    public new required string AgentType { get; init; }

    /// <summary>Text of the last assistant message. TS <c>last_assistant_message</c>.</summary>
    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }

    /// <summary>Background tasks still running. TS <c>background_tasks</c>.</summary>
    [JsonPropertyName("background_tasks")]
    public IReadOnlyList<BackgroundTaskSummary>? BackgroundTasks { get; init; }

    /// <summary>Session cron jobs. TS <c>session_crons</c>.</summary>
    [JsonPropertyName("session_crons")]
    public IReadOnlyList<SessionCronSummary>? SessionCrons { get; init; }
}

/// <summary>
/// Input data for PreCompact hook events.
/// </summary>
public record PreCompactHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PreCompact";

    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("custom_instructions")]
    public string? CustomInstructions { get; init; }
}

/// <summary>
/// Input data for Notification hook events.
/// </summary>
public record NotificationHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "Notification";

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("notification_type")]
    public required string NotificationType { get; init; }
}

/// <summary>
/// Input data for SubagentStart hook events.
/// </summary>
public record SubagentStartHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "SubagentStart";

    [JsonPropertyName("agent_id")]
    public new required string AgentId { get; init; }

    [JsonPropertyName("agent_type")]
    public new required string AgentType { get; init; }
}

/// <summary>
/// Input data for PermissionRequest hook events.
/// </summary>
public record PermissionRequestHookInput : BaseHookInput
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName => "PermissionRequest";

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public required JsonElement ToolInput { get; init; }

    [JsonPropertyName("permission_suggestions")]
    public JsonElement? PermissionSuggestions { get; init; }

    /// <summary>
    /// Typed view of <see cref="PermissionSuggestions"/> (TS <c>PermissionUpdate[]</c>).
    /// Set by <see cref="HookInput.Parse"/>; unknown update types are skipped.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<PermissionUpdate>? Suggestions { get; init; }

    /// <summary>MCP server that provides the tool, for MCP tools. TS <c>mcp_server</c>.</summary>
    [JsonPropertyName("mcp_server")]
    public McpServerProvenance? McpServer { get; init; }
}

/// <summary>
/// Hook output configuration.
/// </summary>
public record HookOutput
{
    [JsonPropertyName("continue")]
    public bool? Continue { get; init; }

    [JsonPropertyName("suppressOutput")]
    public bool? SuppressOutput { get; init; }

    [JsonPropertyName("stopReason")]
    public string? StopReason { get; init; }

    /// <summary>Top-level decision: <see cref="HookDecision.Approve"/> or <see cref="HookDecision.Block"/>.</summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    [JsonPropertyName("systemMessage")]
    public string? SystemMessage { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Terminal escape sequence for the CLI to write (e.g. a bell or title
    /// change). TS <c>SyncHookJSONOutput.terminalSequence</c>.
    /// </summary>
    [JsonPropertyName("terminalSequence")]
    public string? TerminalSequence { get; init; }

    [JsonPropertyName("hookSpecificOutput")]
    public JsonElement? HookSpecificOutput { get; init; }

    /// <summary>
    /// Set to true to defer hook execution (async mode).
    /// </summary>
    [JsonPropertyName("async")]
    public bool? Async { get; init; }

    /// <summary>
    /// Timeout in milliseconds for async hook operations.
    /// </summary>
    [JsonPropertyName("asyncTimeout")]
    public int? AsyncTimeout { get; init; }
}

/// <summary>
/// Hook context information.
/// </summary>
public record HookContext(object? Signal = null);

/// <summary>
/// Delegate for hook callbacks.
/// </summary>
public delegate Task<HookOutput> HookCallback(
    JsonElement input,
    string? toolUseId,
    HookContext context,
    CancellationToken cancellationToken = default
);

/// <summary>
/// Hook matcher configuration.
/// </summary>
public record HookMatcher(
    string? Matcher = null,
    IReadOnlyList<HookCallback>? Hooks = null,
    double? Timeout = null
);

#endregion

#region MCP Server Config

/// <summary>
/// MCP stdio server configuration.
/// </summary>
public record McpStdioServerConfig
{
    [JsonPropertyName("type")]
    public string Type => "stdio";

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Args { get; init; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

/// <summary>
/// MCP SSE server configuration.
/// </summary>
public record McpSSEServerConfig
{
    [JsonPropertyName("type")]
    public string Type => "sse";

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// MCP HTTP server configuration.
/// </summary>
public record McpHttpServerConfig
{
    [JsonPropertyName("type")]
    public string Type => "http";

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// SDK MCP server configuration for in-process servers.
/// </summary>
/// <remarks>
/// Use this configuration to run an MCP server in the same process as your application.
/// The server will communicate with Claude Code via the SDK's control protocol bridge.
/// </remarks>
/// <example>
/// <code>
/// var config = new McpSdkServerConfig
/// {
///     Name = "calculator",
///     Handlers = new McpServerHandlers
///     {
///         ListTools = ct => Task.FromResult&lt;IReadOnlyList&lt;McpToolDefinition&gt;&gt;(
///             [new McpToolDefinition { Name = "add", Description = "Add two numbers" }]
///         ),
///         CallTool = (name, args, ct) => Task.FromResult(
///             new McpToolResult { Content = [new McpContent { Type = "text", Text = "4" }] }
///         )
///     }
/// };
/// </code>
/// </example>
public record McpSdkServerConfig
{
    /// <summary>
    /// The server type identifier. Always "sdk" for in-process servers.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type => "sdk";

    /// <summary>
    /// The name of the server, used for routing MCP messages.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The MCP server handlers for processing requests.
    /// </summary>
    /// <remarks>
    /// Define handlers for tools, prompts, and resources that your server supports.
    /// Only the handlers you provide will be advertised as capabilities.
    /// </remarks>
    [JsonIgnore]
    public Mcp.McpServerHandlers Handlers { get; set; } = null!;
}

#endregion

#region System Prompt Config

/// <summary>
/// Configuration for using Claude Code's preset system prompt with optional additions.
/// Python: <c>SystemPromptPreset</c>.
/// </summary>
public record SystemPromptPreset : SystemPromptConfig
{
    /// <summary>Type identifier. Always "preset" for preset system prompts.</summary>
    [JsonPropertyName("type")]
    public string Type => "preset";

    /// <summary>The preset to use. Currently only "claude_code" is supported.</summary>
    [JsonPropertyName("preset")]
    public required string Preset { get; init; }

    /// <summary>Additional instructions to append to the preset system prompt.</summary>
    [JsonPropertyName("append")]
    public string? Append { get; init; }

    /// <summary>
    /// Strip per-user dynamic sections (working directory, auto-memory, git
    /// status) from the system prompt so it stays static and cacheable across
    /// users. Python commit 3bf8fd5.
    /// </summary>
    [JsonPropertyName("exclude_dynamic_sections")]
    public bool? ExcludeDynamicSections { get; init; }

    /// <summary>
    /// Whether the session keeps the system prompt it recorded on its first
    /// request. When true, every later request (including after resume) sends
    /// the recorded prompt, so changing <see cref="Append"/> has no effect until
    /// the session is compacted or a new session starts. When false, the prompt
    /// is rebuilt on every request. When null it acts as true, except in bare
    /// mode (<c>--bare</c>), where it acts as false. Sent as
    /// <c>systemPromptSnapshot</c> on the initialize request. Requires Claude
    /// Code CLI 2.1.257 or later. Python: <c>SystemPromptPreset.snapshot</c>.
    /// </summary>
    [JsonPropertyName("snapshot")]
    public bool? Snapshot { get; init; }

    internal override bool? SnapshotForInitialize => Snapshot;

    /// <summary>
    /// Creates a Claude Code preset system prompt with the specified append text.
    /// </summary>
    public static SystemPromptPreset ClaudeCode(string? append = null) =>
        new() { Preset = "claude_code", Append = append };
}

/// <summary>
/// System prompt loaded from a file. Python commit 139b815.
/// </summary>
public record SystemPromptFile : SystemPromptConfig
{
    /// <summary>Type identifier. Always "file".</summary>
    [JsonPropertyName("type")]
    public string Type => "file";

    /// <summary>Path to the system prompt file.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}

/// <summary>
/// Tools preset configuration. When passed instead of a list, enables the
/// CLI's default tool set. Python commit reference: ToolsPreset in types.py.
/// </summary>
public record ToolsPreset
{
    /// <summary>Type identifier. Always "preset".</summary>
    [JsonPropertyName("type")]
    public string Type => "preset";

    /// <summary>The preset to use. Currently only "claude_code" is supported.</summary>
    [JsonPropertyName("preset")]
    public required string Preset { get; init; }

    /// <summary>Creates a Claude Code tools preset.</summary>
    public static ToolsPreset ClaudeCode() => new() { Preset = "claude_code" };
}

/// <summary>
/// API-side task budget in tokens. Sent as output_config.task_budget with the
/// task-budgets-2026-03-13 beta header. Python commit 2e60cec.
/// </summary>
public record TaskBudget(
    [property: JsonPropertyName("total")] int Total
);

#endregion

#region Agent and Sandbox Config

/// <summary>
/// Agent definition configuration. Expanded by Python commits 028d591,
/// fad1b84, 7c6902b.
/// </summary>
/// <param name="Description">When to use this agent.</param>
/// <param name="Prompt">The agent's system prompt.</param>
/// <param name="Tools">Allowed tool names (passing "Skill" is deprecated; use <paramref name="Skills"/>).</param>
/// <param name="Model">Model alias ("sonnet", "opus", "haiku", "inherit") or a full model ID.</param>
/// <param name="DisallowedTools">Tool names the agent may not use.</param>
/// <param name="Skills">Skills to enable for the agent.</param>
/// <param name="Memory">"user", "project" or "local".</param>
/// <param name="InitialPrompt">Prompt sent when the agent starts.</param>
/// <param name="MaxTurns">Maximum turns for the agent.</param>
/// <param name="Background">Run the agent in the background.</param>
/// <param name="PermissionMode">Permission mode for the agent.</param>
/// <param name="Effort">An <see cref="EffortLevel"/> or an int (Python <c>EffortLevel | int</c>); both convert implicitly.</param>
/// <param name="McpServers">Server names or inline <c>{name: config}</c> maps (Python <c>list[str | dict]</c>); strings and dictionaries convert implicitly.</param>
public record AgentDefinition(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("tools")] IReadOnlyList<string>? Tools = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("disallowedTools")] IReadOnlyList<string>? DisallowedTools = null,
    [property: JsonPropertyName("skills")] IReadOnlyList<string>? Skills = null,
    [property: JsonPropertyName("memory")] string? Memory = null,
    [property: JsonPropertyName("mcpServers")] IReadOnlyList<AgentMcpServer>? McpServers = null,
    [property: JsonPropertyName("initialPrompt")] string? InitialPrompt = null,
    [property: JsonPropertyName("maxTurns")] int? MaxTurns = null,
    [property: JsonPropertyName("background")] bool? Background = null,
    [property: JsonPropertyName("effort")] AgentEffort? Effort = null,
    [property: JsonPropertyName("permissionMode")] PermissionMode? PermissionMode = null
);

/// <summary>
/// SDK plugin configuration.
/// </summary>
public record SdkPluginConfig(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path
);

/// <summary>
/// Network configuration for sandbox.
/// </summary>
public record SandboxNetworkConfig
{
    /// <summary>Domain names that sandboxed processes can access. Python commit 92a4615.</summary>
    [JsonPropertyName("allowedDomains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedDomains { get; init; }

    /// <summary>Domains that are always blocked, even if matched by allowedDomains. Python commit 92a4615.</summary>
    [JsonPropertyName("deniedDomains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DeniedDomains { get; init; }

    /// <summary>When true in managed settings, only managed-settings allowedDomains are respected. Python commit 92a4615.</summary>
    [JsonPropertyName("allowManagedDomainsOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowManagedDomainsOnly { get; init; }

    [JsonPropertyName("allowUnixSockets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowUnixSockets { get; init; }

    [JsonPropertyName("allowAllUnixSockets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowAllUnixSockets { get; init; }

    [JsonPropertyName("allowLocalBinding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowLocalBinding { get; init; }

    /// <summary>macOS only: XPC/Mach service names to allow (supports trailing wildcard).</summary>
    [JsonPropertyName("allowMachLookup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowMachLookup { get; init; }

    [JsonPropertyName("httpProxyPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpProxyPort { get; init; }

    [JsonPropertyName("socksProxyPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SocksProxyPort { get; init; }
}

/// <summary>
/// Violations to ignore in sandbox.
/// </summary>
public record SandboxIgnoreViolations
{
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? File { get; init; }

    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Network { get; init; }
}

/// <summary>
/// Sandbox settings configuration.
/// </summary>
public record SandboxSettings
{
    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }

    [JsonPropertyName("autoAllowBashIfSandboxed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoAllowBashIfSandboxed { get; init; }

    [JsonPropertyName("excludedCommands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ExcludedCommands { get; init; }

    [JsonPropertyName("allowUnsandboxedCommands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowUnsandboxedCommands { get; init; }

    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxNetworkConfig? Network { get; init; }

    [JsonPropertyName("ignoreViolations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxIgnoreViolations? IgnoreViolations { get; init; }

    [JsonPropertyName("enableWeakerNestedSandbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableWeakerNestedSandbox { get; init; }
}

#endregion

#region ThinkingConfig

/// <summary>
/// Base interface for thinking configuration.
/// </summary>
public interface IThinkingConfig
{
    /// <summary>The thinking configuration type.</summary>
    string Type { get; }

    /// <summary>
    /// Optional thinking display mode forwarded as <c>--thinking-display</c>.
    /// Python commit 32f09c1. Ignored for the Disabled variant.
    /// </summary>
    string? Display { get; }
}

/// <summary>
/// Adaptive thinking configuration — lets the model decide how much to think.
/// </summary>
public record ThinkingConfigAdaptive : IThinkingConfig
{
    /// <inheritdoc />
    public string Type => "adaptive";

    /// <inheritdoc />
    public string? Display { get; init; }
}

/// <summary>
/// Enabled thinking configuration with a specific budget.
/// </summary>
public record ThinkingConfigEnabled(int BudgetTokens) : IThinkingConfig
{
    /// <inheritdoc />
    public string Type => "enabled";

    /// <inheritdoc />
    public string? Display { get; init; }
}

/// <summary>
/// Disabled thinking configuration.
/// </summary>
public record ThinkingConfigDisabled : IThinkingConfig
{
    /// <inheritdoc />
    public string Type => "disabled";

    /// <inheritdoc />
    public string? Display => null;
}

#endregion

#region Claude Agent Options

/// <summary>
/// Query options for Claude SDK.
/// </summary>
/// <remarks>
/// A record so the SDK can derive adjusted copies with <c>with</c> (Python's
/// <c>dataclasses.replace</c>). <see cref="ToString"/> deliberately omits
/// members such as <see cref="Env"/> that commonly carry secrets.
/// </remarks>
public record ClaudeAgentOptions
{
    /// <summary>Prints only non-sensitive members.</summary>
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Model = ").Append(Model).Append(", Cwd = ").Append(Cwd)
            .Append(", PermissionMode = ").Append(PermissionMode);
        return true;
    }

    /// <summary>Base set of tools to enable.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Additional tools to allow.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// System prompt configuration: a string, <see cref="SystemPromptPreset"/>,
    /// <see cref="SystemPromptCustom"/> or <see cref="SystemPromptFile"/>.
    /// <c>null</c> sends an empty system prompt (Python parity).
    /// </summary>
    public SystemPromptConfig? SystemPrompt { get; init; }

    /// <summary>
    /// MCP server configurations: a name → config dictionary (see the
    /// <c>McpServers</c> helpers for in-process SDK servers), or a path to an
    /// MCP config file / JSON string.
    /// </summary>
    public McpServersConfig? McpServers { get; init; }

    /// <summary>Permission mode for tool execution.</summary>
    public PermissionMode? PermissionMode { get; init; }

    /// <summary>Continue from previous conversation.</summary>
    public bool ContinueConversation { get; init; }

    /// <summary>Session ID to resume.</summary>
    public string? Resume { get; init; }

    /// <summary>Maximum number of turns.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Maximum budget in USD.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Tools to disallow.</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Fallback model if primary unavailable.</summary>
    public string? FallbackModel { get; init; }

    /// <summary>Beta features to enable.</summary>
    public IReadOnlyList<string> Betas { get; init; } = [];

    /// <summary>Permission prompt tool name.</summary>
    public string? PermissionPromptToolName { get; init; }

    /// <summary>Working directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>Path to CLI binary.</summary>
    public string? CliPath { get; init; }

    /// <summary>Settings path or JSON.</summary>
    public string? Settings { get; init; }

    /// <summary>Additional directories to include.</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];

    /// <summary>Environment variables to set.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Extra CLI arguments.</summary>
    public IReadOnlyDictionary<string, string?> ExtraArgs { get; init; } = new Dictionary<string, string?>();

    /// <summary>Maximum buffer size for CLI stdout.</summary>
    public int? MaxBufferSize { get; init; }

    /// <summary>
    /// Deprecated and no longer read by the transport (Python:
    /// <c>debug_stderr</c>). Use <see cref="StderrCallback"/>.
    /// </summary>
    [Obsolete("No longer read by the transport. Use StderrCallback instead.")]
    public TextWriter? DebugStderr { get; init; }

    /// <summary>Callback for stderr output from CLI.</summary>
    public Action<string>? StderrCallback { get; init; }

    /// <summary>Tool permission callback.</summary>
    public CanUseToolCallback? CanUseTool { get; init; }

    /// <summary>Hook configurations.</summary>
    public IReadOnlyDictionary<HookEvent, IReadOnlyList<HookMatcher>>? Hooks { get; init; }

    /// <summary>User identifier.</summary>
    public string? User { get; init; }

    /// <summary>Include partial messages during streaming.</summary>
    public bool IncludePartialMessages { get; init; }

    /// <summary>Fork session when resuming.</summary>
    public bool ForkSession { get; init; }

    /// <summary>
    /// When resuming, only load the conversation up to and including the
    /// message with this UUID. Use with <see cref="Resume"/> (and usually
    /// <see cref="ForkSession"/>) to branch from an earlier point. Maps to
    /// <c>--resume-session-at=&lt;uuid&gt;</c>. Python: <c>resume_session_at</c>.
    /// </summary>
    public string? ResumeSessionAt { get; init; }

    /// <summary>
    /// With <see cref="ResumeSessionAt"/>: the UUID of the user prompt whose
    /// turn this truncating resume intends to discard. The CLI then refuses the
    /// resume (surfacing as a <see cref="ProcessException"/> whose message
    /// contains <c>Resume rejected by --resume-drops-turn:</c>) unless every
    /// entry after the resume point belongs to that turn. An empty string is
    /// forwarded (and rejected by the CLI) rather than silently disarming the
    /// guard. Maps to <c>--resume-drops-turn=&lt;uuid&gt;</c>. Python:
    /// <c>resume_drops_turn</c>.
    /// </summary>
    public string? ResumeDropsTurn { get; init; }

    /// <summary>
    /// Forward subagent text and thinking blocks as messages in the stream
    /// (by default only tool_use / tool_result blocks from subagents are
    /// emitted). Sent as <c>forwardSubagentText</c> on the initialize request.
    /// Python: <c>forward_subagent_text</c>.
    /// </summary>
    public bool ForwardSubagentText { get; init; }

    /// <summary>
    /// Deliver every prompt to Claude as written: every user message the SDK
    /// sends is marked <c>client_composed</c>, so the CLI performs no
    /// <c>@path</c> file-mention expansion and no slash-command dispatch. Any
    /// caller-supplied <c>client_composed</c> value is overwritten while this is
    /// on. Requires Claude Code 2.1.248 or later (older versions ignore the
    /// field; the SDK warns on connect). Python: <c>verbatim_prompts</c>.
    /// </summary>
    public bool VerbatimPrompts { get; init; }

    /// <summary>Agent definitions.</summary>
    public IReadOnlyDictionary<string, AgentDefinition>? Agents { get; init; }

    /// <summary>Setting sources to load.</summary>
    public IReadOnlyList<SettingSource>? SettingSources { get; init; }

    /// <summary>Sandbox settings.</summary>
    public SandboxSettings? Sandbox { get; init; }

    /// <summary>Plugin configurations.</summary>
    public IReadOnlyList<SdkPluginConfig> Plugins { get; init; } = [];

    /// <summary>Maximum thinking tokens.</summary>
    /// <remarks>Deprecated: Use <see cref="Thinking"/> instead.</remarks>
    [Obsolete("Use Thinking instead.")]
    public int? MaxThinkingTokens { get; init; }

    /// <summary>
    /// Controls extended thinking behavior. Takes precedence over MaxThinkingTokens.
    /// </summary>
    public IThinkingConfig? Thinking { get; init; }

    /// <summary>
    /// Effort level for thinking depth.
    /// </summary>
    public EffortLevel? Effort { get; init; }

    /// <summary>Output format for structured outputs.</summary>
    public JsonElement? OutputFormat { get; init; }

    /// <summary>Enable file checkpointing.</summary>
    public bool EnableFileCheckpointing { get; init; }

    /// <summary>
    /// Tools preset (alternative to <see cref="Tools"/>). When set, indicates
    /// the CLI's built-in tools preset should be used.
    /// </summary>
    public ToolsPreset? ToolsPreset { get; init; }

    /// <summary>
    /// Use a specific session ID for the conversation instead of an auto-generated one.
    /// Must be a valid UUID. Python commit 5656d20.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// API-side task budget in tokens. Sent as output_config.task_budget with
    /// the task-budgets-2026-03-13 beta header. Python commit 2e60cec.
    /// </summary>
    public TaskBudget? TaskBudget { get; init; }

    /// <summary>
    /// Skills to enable. <c>null</c> = no SDK auto-configuration; empty list =
    /// suppress every skill; <c>"all"</c> = enable every discovered skill;
    /// list of names = enable only those. Python commit 1c26bd3.
    /// </summary>
    /// <remarks>
    /// Python <c>list[str] | Literal["all"] | None</c>. Assign <c>"all"</c>,
    /// a <see cref="List{T}"/> / array of names, or a <see cref="SkillsConfig"/>.
    /// </remarks>
    public SkillsConfig? Skills { get; init; }

    /// <summary>
    /// When true, only use MCP servers passed via <see cref="McpServers"/>,
    /// ignoring all other MCP configurations the CLI would otherwise load.
    /// Maps to <c>--strict-mcp-config</c>. Python commit 32bcc4e.
    /// </summary>
    public bool StrictMcpConfig { get; init; }

    /// <summary>
    /// Include hook lifecycle events (PreToolUse, PostToolUse, Stop, etc.)
    /// in the message stream as <see cref="HookEventMessage"/> objects.
    /// Python commit c1182a4.
    /// </summary>
    public bool IncludeHookEvents { get; init; }

    /// <summary>
    /// Mirror session transcripts to an external store. Stub interface added
    /// in Phase 2B; wiring (transport/handler integration) lands in Phase 3B.
    /// Python commit 6e3d54f.
    /// </summary>
    public ISessionStore? SessionStore { get; init; }

    /// <summary>
    /// When to flush mirrored transcript entries to <see cref="SessionStore"/>.
    /// Defaults to <see cref="SessionStoreFlushMode.Batched"/>.
    /// Ignored when <see cref="SessionStore"/> is null. Python commit 0a69e94.
    /// </summary>
    public SessionStoreFlushMode SessionStoreFlush { get; init; } = SessionStoreFlushMode.Batched;

    /// <summary>
    /// Optional logger for SDK diagnostics: CLI discovery and spawn, process exit,
    /// skipped/dropped stdout lines, control-protocol traffic (subtypes and ids only),
    /// callback failures, and warnings. Argument values, prompts, environment and
    /// message contents are never logged, since they can carry secrets. When a
    /// logger is set, CLI stderr lines are also logged at Trace (only if stderr is
    /// being read, i.e. <see cref="StderrCallback"/> or debug-to-stderr is set).
    /// Not part of Python's options; .NET addition.
    /// </summary>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; init; }

    /// <summary>
    /// Timeout, in milliseconds, for each <see cref="ISessionStore"/> load /
    /// list call during resume materialization. The query fails with a clear
    /// error instead of hanging when the adapter doesn't settle in time. 0 (or
    /// less) means an immediate timeout. Python: <c>load_timeout_ms</c>.
    /// </summary>
    public int LoadTimeoutMs { get; init; } = 60_000;
}

#endregion

#region JSON Serialization Context

/// <summary>
/// JSON serialization context for AOT compatibility.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(SystemMessage))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextBlock))]
[JsonSerializable(typeof(ThinkingBlock))]
[JsonSerializable(typeof(ToolUseBlock))]
[JsonSerializable(typeof(ToolResultBlock))]
[JsonSerializable(typeof(HookOutput))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(List<ContentBlock>))]
internal partial class ClaudeJsonContext : JsonSerializerContext
{
}

#endregion
