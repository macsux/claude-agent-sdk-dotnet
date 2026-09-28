// Claude Agent SDK for .NET — message types added for parity with the
// TypeScript SDK (@anthropic-ai/claude-agent-sdk 0.3.283, sdk.d.ts).
// Python (0.2.160) drops or leaves these untyped; see docs/PARITY.md.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claude.AgentSdk;

#region Unknown / new top-level messages

/// <summary>
/// A top-level message whose <c>type</c> this SDK version doesn't model. The
/// whole frame is on <see cref="Message.Raw"/>.
/// </summary>
/// <remarks>
/// TS behaviour: the TypeScript SDK yields every stdout frame it doesn't
/// consume internally, so new message types reach callers without an SDK
/// release. Python drops unknown types instead; this SDK follows TS. Internal
/// frames (<c>keep_alive</c>) are still skipped.
/// </remarks>
public record UnknownMessage : Message
{
    /// <summary>The frame's <c>type</c> value.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

/// <summary>
/// Heartbeat for a running tool call (<c>type: "tool_progress"</c>). TS
/// <c>SDKToolProgressMessage</c>.
/// </summary>
public record ToolProgressMessage : Message
{
    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("parent_tool_use_id")]
    public string? ParentToolUseId { get; init; }

    /// <summary>Seconds since the tool started.</summary>
    [JsonPropertyName("elapsed_time_seconds")]
    public double ElapsedTimeSeconds { get; init; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>True for a keep-alive tick with no new information.</summary>
    [JsonPropertyName("heartbeat")]
    public bool? Heartbeat { get; init; }

    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>Set while a sub-agent's API call is being retried.</summary>
    [JsonPropertyName("subagent_retry")]
    public SubagentRetryInfo? SubagentRetry { get; init; }
}

/// <summary>Retry state of a sub-agent API call. TS <c>SDKToolProgressMessage.subagent_retry</c>.</summary>
public record SubagentRetryInfo
{
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; }

    [JsonPropertyName("retry_delay_ms")]
    public double RetryDelayMs { get; init; }

    [JsonPropertyName("error_status")]
    public int? ErrorStatus { get; init; }

    [JsonPropertyName("error_category")]
    public required string ErrorCategory { get; init; }
}

/// <summary>
/// One-line summary of preceding tool calls (<c>type: "tool_use_summary"</c>).
/// TS <c>SDKToolUseSummaryMessage</c>.
/// </summary>
public record ToolUseSummaryMessage : Message
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("preceding_tool_use_ids")]
    public IReadOnlyList<string> PrecedingToolUseIds { get; init; } = [];

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>
/// Authentication progress (<c>type: "auth_status"</c>). TS <c>SDKAuthStatusMessage</c>.
/// </summary>
public record AuthStatusMessage : Message
{
    [JsonPropertyName("isAuthenticating")]
    public bool IsAuthenticating { get; init; }

    /// <summary>Output lines from the auth flow.</summary>
    [JsonPropertyName("output")]
    public IReadOnlyList<string> Output { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>
/// Suggested next prompt (<c>type: "prompt_suggestion"</c>). TS <c>SDKPromptSuggestionMessage</c>.
/// </summary>
public record PromptSuggestionMessage : Message
{
    [JsonPropertyName("suggestion")]
    public required string Suggestion { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>
/// The session's active goal changed (<c>type: "active_goal"</c>). TS
/// <c>SDKActiveGoalMessage</c> (declared but not in the <c>SDKMessage</c> union).
/// </summary>
public record ActiveGoalMessage : Message
{
    /// <summary>The goal, or <c>null</c> when it was cleared.</summary>
    [JsonPropertyName("value")]
    public ActiveGoal? Value { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>An active goal. TS <c>SDKActiveGoalMessage.value</c>.</summary>
public record ActiveGoal
{
    [JsonPropertyName("condition")]
    public required string Condition { get; init; }

    [JsonPropertyName("iterations")]
    public int Iterations { get; init; }

    /// <summary>When the goal was set (epoch milliseconds).</summary>
    [JsonPropertyName("set_at")]
    public double SetAt { get; init; }

    [JsonPropertyName("tokens_at_start")]
    public long TokensAtStart { get; init; }

    [JsonPropertyName("last_reason")]
    public string? LastReason { get; init; }
}

#endregion

#region Content blocks

/// <summary>
/// Redacted thinking content (encrypted; pass back unchanged). API
/// <c>redacted_thinking</c> block; Python drops it.
/// </summary>
public record RedactedThinkingBlock(
    [property: JsonPropertyName("data")] string Data
) : ContentBlock;

/// <summary>
/// An assistant content block whose <c>type</c> this SDK doesn't model (e.g.
/// <c>mcp_tool_use</c>, <c>mcp_tool_result</c>, <c>container_upload</c>).
/// Python drops these; this SDK keeps them so no content is lost.
/// </summary>
/// <param name="Type">The block's <c>type</c> value.</param>
/// <param name="Raw">The block, unmodified.</param>
public record RawContentBlock(
    [property: JsonIgnore] string Type,
    [property: JsonIgnore] JsonElement Raw
) : ContentBlock;

#endregion

#region Supporting message types

/// <summary>A tool call denied during the run. TS <c>SDKPermissionDenial</c>.</summary>
public record PermissionDenial
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("tool_input")]
    public JsonElement ToolInput { get; init; }
}

/// <summary>An MCP resource link. TS <c>SDKMcpResourceLink</c>.</summary>
public record McpResourceLink
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("annotations")]
    public JsonElement? Annotations { get; init; }
}

/// <summary>
/// Conversions between <see cref="AssistantMessageError"/> and its wire strings
/// (TS <c>SDKAssistantMessageError</c>).
/// </summary>
public static class AssistantMessageErrors
{
    /// <summary>
    /// Parse a wire value. <c>null</c> for <c>null</c>; any unrecognized string
    /// maps to <see cref="AssistantMessageError.Unknown"/> (keep the string
    /// yourself, e.g. <see cref="AssistantMessage.ErrorRaw"/>).
    /// </summary>
    public static AssistantMessageError? Parse(string? value) => value switch
    {
        null => null,
        "authentication_failed" => AssistantMessageError.AuthenticationFailed,
        "oauth_org_not_allowed" => AssistantMessageError.OauthOrgNotAllowed,
        "account_on_hold" => AssistantMessageError.AccountOnHold,
        "verification_required" => AssistantMessageError.VerificationRequired,
        "billing_error" => AssistantMessageError.BillingError,
        "rate_limit" => AssistantMessageError.RateLimit,
        "overloaded" => AssistantMessageError.Overloaded,
        "invalid_request" => AssistantMessageError.InvalidRequest,
        "model_not_found" => AssistantMessageError.ModelNotFound,
        "server_error" => AssistantMessageError.ServerError,
        "max_output_tokens" => AssistantMessageError.MaxOutputTokens,
        "cloud_credential_error" => AssistantMessageError.CloudCredentialError,
        _ => AssistantMessageError.Unknown
    };

    /// <summary>The wire string for <paramref name="error"/>.</summary>
    public static string ToWireString(this AssistantMessageError error) => error switch
    {
        AssistantMessageError.AuthenticationFailed => "authentication_failed",
        AssistantMessageError.OauthOrgNotAllowed => "oauth_org_not_allowed",
        AssistantMessageError.AccountOnHold => "account_on_hold",
        AssistantMessageError.VerificationRequired => "verification_required",
        AssistantMessageError.BillingError => "billing_error",
        AssistantMessageError.RateLimit => "rate_limit",
        AssistantMessageError.Overloaded => "overloaded",
        AssistantMessageError.InvalidRequest => "invalid_request",
        AssistantMessageError.ModelNotFound => "model_not_found",
        AssistantMessageError.ServerError => "server_error",
        AssistantMessageError.MaxOutputTokens => "max_output_tokens",
        AssistantMessageError.CloudCredentialError => "cloud_credential_error",
        _ => "unknown"
    };
}

#endregion

#region Typed system subtypes

/// <summary>
/// Session initialization (<c>system/init</c>). TS <c>SDKSystemMessage</c>.
/// Previously a plain <see cref="SystemMessage"/>; <c>Data</c> still holds the frame.
/// </summary>
public record SystemInitMessage : SystemMessage
{
    [JsonPropertyName("agents")]
    public IReadOnlyList<string>? Agents { get; init; }

    /// <summary>See <see cref="ApiKeySources"/>.</summary>
    [JsonPropertyName("apiKeySource")]
    public string? ApiKeySource { get; init; }

    [JsonPropertyName("betas")]
    public IReadOnlyList<string>? Betas { get; init; }

    [JsonPropertyName("claude_code_version")]
    public string? ClaudeCodeVersion { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<string> Tools { get; init; } = [];

    [JsonPropertyName("mcp_servers")]
    public IReadOnlyList<InitMcpServer> McpServers { get; init; } = [];

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Permission mode wire string (e.g. <c>"default"</c>, <c>"acceptEdits"</c>).</summary>
    [JsonPropertyName("permissionMode")]
    public string? PermissionMode { get; init; }

    [JsonPropertyName("slash_commands")]
    public IReadOnlyList<string> SlashCommands { get; init; } = [];

    [JsonPropertyName("terminal_slash_commands")]
    public IReadOnlyList<string>? TerminalSlashCommands { get; init; }

    [JsonPropertyName("output_style")]
    public string? OutputStyle { get; init; }

    [JsonPropertyName("skills")]
    public IReadOnlyList<string> Skills { get; init; } = [];

    [JsonPropertyName("plugins")]
    public IReadOnlyList<InitPlugin> Plugins { get; init; } = [];

    [JsonPropertyName("plugin_errors")]
    public IReadOnlyList<InitPluginError>? PluginErrors { get; init; }

    /// <summary>See <see cref="FastModeStates"/>.</summary>
    [JsonPropertyName("fast_mode_state")]
    public string? FastModeState { get; init; }

    /// <summary>See <see cref="FastModeDisabledReasons"/>.</summary>
    [JsonPropertyName("fast_mode_disabled_reason")]
    public string? FastModeDisabledReason { get; init; }

    /// <summary>Effort level wire string (<c>low</c> … <c>max</c>), or null.</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }

    /// <summary><c>focus</c> or <c>default</c>.</summary>
    [JsonPropertyName("view_mode")]
    public string? ViewMode { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>An MCP server entry in <see cref="SystemInitMessage.McpServers"/>.</summary>
public record InitMcpServer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string? Source = null);

/// <summary>A plugin entry in <see cref="SystemInitMessage.Plugins"/>.</summary>
public record InitPlugin(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("version")] string? Version = null);

/// <summary>A plugin load error in <see cref="SystemInitMessage.PluginErrors"/>.</summary>
public record InitPluginError(
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path = null);

/// <summary>Compaction boundary (<c>system/compact_boundary</c>). TS <c>SDKCompactBoundaryMessage</c>.</summary>
public record CompactBoundaryMessage : SystemMessage
{
    [JsonPropertyName("compact_metadata")]
    public CompactMetadata? CompactMetadata { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Details of a compaction. TS <c>SDKCompactBoundaryMessage.compact_metadata</c>.</summary>
public record CompactMetadata
{
    /// <summary><c>manual</c> or <c>auto</c>.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("pre_tokens")]
    public long PreTokens { get; init; }

    [JsonPropertyName("post_tokens")]
    public long? PostTokens { get; init; }

    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; init; }

    [JsonPropertyName("preserved_segment")]
    public CompactPreservedSegment? PreservedSegment { get; init; }

    [JsonPropertyName("preserved_messages")]
    public CompactPreservedMessages? PreservedMessages { get; init; }
}

/// <summary>A preserved transcript segment. TS <c>compact_metadata.preserved_segment</c>.</summary>
public record CompactPreservedSegment(
    [property: JsonPropertyName("head_uuid")] string HeadUuid,
    [property: JsonPropertyName("anchor_uuid")] string AnchorUuid,
    [property: JsonPropertyName("tail_uuid")] string TailUuid);

/// <summary>Messages kept across compaction. TS <c>compact_metadata.preserved_messages</c>.</summary>
public record CompactPreservedMessages(
    [property: JsonPropertyName("anchor_uuid")] string AnchorUuid,
    [property: JsonPropertyName("uuids")] IReadOnlyList<string> Uuids);

/// <summary>Session status (<c>system/status</c>). TS <c>SDKStatusMessage</c>.</summary>
public record StatusMessage : SystemMessage
{
    /// <summary><c>compacting</c>, <c>requesting</c> or <c>null</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("permissionMode")]
    public string? PermissionMode { get; init; }

    /// <summary><c>success</c> or <c>failed</c>.</summary>
    [JsonPropertyName("compact_result")]
    public string? CompactResult { get; init; }

    [JsonPropertyName("compact_error")]
    public string? CompactError { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>An API call is being retried (<c>system/api_retry</c>). TS <c>SDKAPIRetryMessage</c>.</summary>
public record ApiRetryMessage : SystemMessage
{
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; }

    [JsonPropertyName("retry_delay_ms")]
    public double RetryDelayMs { get; init; }

    [JsonPropertyName("error_status")]
    public int? ErrorStatus { get; init; }

    /// <summary>Parsed <c>error</c>; see <see cref="ErrorRaw"/> for the wire string.</summary>
    [JsonPropertyName("error")]
    public AssistantMessageError? Error { get; init; }

    [JsonPropertyName("error_raw")]
    public string? ErrorRaw { get; init; }

    [JsonPropertyName("no_response")]
    public ApiRetryNoResponse? NoResponse { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Set when the retried request never received a response. TS <c>SDKAPIRetryMessage.no_response</c>.</summary>
public record ApiRetryNoResponse(
    [property: JsonPropertyName("waited_ms")] double WaitedMs,
    [property: JsonPropertyName("retry_wait_ms")] double RetryWaitMs);

/// <summary>Progress of a control request (<c>system/control_request_progress</c>). TS <c>SDKControlRequestProgressMessage</c>.</summary>
public record ControlRequestProgressMessage : SystemMessage
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    /// <summary><c>started</c> or <c>api_retry</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("attempt")]
    public int? Attempt { get; init; }

    [JsonPropertyName("max_retries")]
    public int? MaxRetries { get; init; }

    [JsonPropertyName("retry_delay_ms")]
    public double? RetryDelayMs { get; init; }

    [JsonPropertyName("error_status")]
    public int? ErrorStatus { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>A refusal triggered a model fallback (<c>system/model_refusal_fallback</c>). TS <c>SDKModelRefusalFallbackMessage</c>.</summary>
public record ModelRefusalFallbackMessage : SystemMessage
{
    [JsonPropertyName("trigger")]
    public string? Trigger { get; init; }

    /// <summary><c>retry</c>, <c>revert</c> or <c>sticky</c>.</summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    /// <summary><c>session</c> or <c>local</c>.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("original_model")]
    public required string OriginalModel { get; init; }

    [JsonPropertyName("fallback_model")]
    public required string FallbackModel { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("api_refusal_category")]
    public string? ApiRefusalCategory { get; init; }

    [JsonPropertyName("api_refusal_explanation")]
    public string? ApiRefusalExplanation { get; init; }

    [JsonPropertyName("retracted_message_uuids")]
    public IReadOnlyList<string>? RetractedMessageUuids { get; init; }

    [JsonPropertyName("refused_user_message_uuid")]
    public string? RefusedUserMessageUuid { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>A refusal with no fallback available (<c>system/model_refusal_no_fallback</c>). TS <c>SDKModelRefusalNoFallbackMessage</c>.</summary>
public record ModelRefusalNoFallbackMessage : SystemMessage
{
    [JsonPropertyName("original_model")]
    public required string OriginalModel { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("api_refusal_category")]
    public string? ApiRefusalCategory { get; init; }

    [JsonPropertyName("api_refusal_explanation")]
    public string? ApiRefusalExplanation { get; init; }

    [JsonPropertyName("refused_user_message_uuid")]
    public string? RefusedUserMessageUuid { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Output of a local slash command (<c>system/local_command_output</c>). TS <c>SDKLocalCommandOutputMessage</c>.</summary>
public record LocalCommandOutputMessage : SystemMessage
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Plugin install progress (<c>system/plugin_install</c>). TS <c>SDKPluginInstallMessage</c>.</summary>
public record PluginInstallMessage : SystemMessage
{
    /// <summary><c>started</c>, <c>installed</c>, <c>failed</c> or <c>completed</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>The set of background tasks changed (<c>system/background_tasks_changed</c>). TS <c>SDKBackgroundTasksChangedMessage</c>.</summary>
public record BackgroundTasksChangedMessage : SystemMessage
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<BackgroundTaskInfo> Tasks { get; init; } = [];

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>An entry in <see cref="BackgroundTasksChangedMessage.Tasks"/>.</summary>
public record BackgroundTaskInfo(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("task_type")] string TaskType,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("ambient")] bool? Ambient = null);

/// <summary>Estimated thinking tokens so far (<c>system/thinking_tokens</c>). TS <c>SDKThinkingTokensMessage</c>.</summary>
public record ThinkingTokensMessage : SystemMessage
{
    [JsonPropertyName("estimated_tokens")]
    public long EstimatedTokens { get; init; }

    [JsonPropertyName("estimated_tokens_delta")]
    public long EstimatedTokensDelta { get; init; }

    [JsonPropertyName("user_message_uuid")]
    public string? UserMessageUuid { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Session run state changed (<c>system/session_state_changed</c>). TS <c>SDKSessionStateChangedMessage</c>.</summary>
public record SessionStateChangedMessage : SystemMessage
{
    /// <summary><c>idle</c>, <c>running</c> or <c>requires_action</c>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>The worker is shutting down (<c>system/worker_shutting_down</c>). TS <c>SDKWorkerShuttingDownMessage</c>.</summary>
public record WorkerShuttingDownMessage : SystemMessage
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Available slash commands changed (<c>system/commands_changed</c>). TS <c>SDKCommandsChangedMessage</c>.</summary>
public record CommandsChangedMessage : SystemMessage
{
    [JsonPropertyName("commands")]
    public IReadOnlyList<SlashCommand> Commands { get; init; } = [];

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>A UI notification (<c>system/notification</c>). TS <c>SDKNotificationMessage</c>.</summary>
public record NotificationMessage : SystemMessage
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary><c>low</c>, <c>medium</c>, <c>high</c> or <c>immediate</c>.</summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("timeout_ms")]
    public double? TimeoutMs { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Files were uploaded / persisted (<c>system/files_persisted</c>). TS <c>SDKFilesPersistedEvent</c>.</summary>
public record FilesPersistedMessage : SystemMessage
{
    [JsonPropertyName("files")]
    public IReadOnlyList<PersistedFile> Files { get; init; } = [];

    [JsonPropertyName("failed")]
    public IReadOnlyList<FailedPersistedFile> Failed { get; init; } = [];

    [JsonPropertyName("processed_at")]
    public string? ProcessedAt { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>A persisted file. TS <c>SDKFilesPersistedEvent.files[]</c>.</summary>
public record PersistedFile(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("file_id")] string FileId);

/// <summary>A file that failed to persist. TS <c>SDKFilesPersistedEvent.failed[]</c>.</summary>
public record FailedPersistedFile(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("error")] string Error);

/// <summary>Memories were recalled (<c>system/memory_recall</c>). TS <c>SDKMemoryRecallMessage</c>.</summary>
public record MemoryRecallMessage : SystemMessage
{
    /// <summary><c>select</c> or <c>synthesize</c>.</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("memories")]
    public IReadOnlyList<RecalledMemory> Memories { get; init; } = [];

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>A recalled memory. <see cref="Scope"/> is <c>personal</c>, <c>team</c> or <c>organization</c>.</summary>
public record RecalledMemory(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("content")] string? Content = null);

/// <summary>An MCP elicitation finished (<c>system/elicitation_complete</c>). TS <c>SDKElicitationCompleteMessage</c>.</summary>
public record ElicitationCompleteMessage : SystemMessage
{
    [JsonPropertyName("mcp_server_name")]
    public required string McpServerName { get; init; }

    [JsonPropertyName("elicitation_id")]
    public required string ElicitationId { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>A tool call was denied (<c>system/permission_denied</c>). TS <c>SDKPermissionDeniedMessage</c>.</summary>
public record PermissionDeniedMessage : SystemMessage
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("decision_reason_type")]
    public string? DecisionReasonType { get; init; }

    [JsonPropertyName("decision_reason")]
    public string? DecisionReason { get; init; }

    /// <summary>Denial message shown to the model.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>Informational notice (<c>system/informational</c>). TS <c>SDKInformationalMessage</c>.</summary>
public record InformationalMessage : SystemMessage
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary><c>info</c>, <c>notice</c>, <c>suggestion</c> or <c>warning</c>.</summary>
    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("prevent_continuation")]
    public bool? PreventContinuation { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

#endregion
