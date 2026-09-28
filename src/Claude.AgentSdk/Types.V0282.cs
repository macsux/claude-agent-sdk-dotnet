// Claude Agent SDK for .NET — types added for parity with v0.2.82.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/types.py @ c352a50

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claude.AgentSdk;

#region Task Messages (T16)

/// <summary>
/// Usage statistics reported in task_progress and task_notification messages.
/// Python commit 9af27d7.
/// </summary>
public record TaskUsage(
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("tool_uses")] int ToolUses,
    [property: JsonPropertyName("duration_ms")] int DurationMs
);

/// <summary>
/// Possible status values for a task_notification message.
/// Python commit 9af27d7.
/// </summary>
public enum TaskNotificationStatus
{
    Completed,
    Failed,
    Stopped,
    Killed,
    /// <summary>A status value this SDK version doesn't recognize; see the raw <c>Data</c>.</summary>
    Unknown
}

/// <summary>
/// System message emitted when a task starts. Python commit 9af27d7.
/// </summary>
public record TaskStartedMessage : SystemMessage
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("task_type")]
    public string? TaskType { get; init; }

    /// <summary>Sub-agent type that runs the task. TS <c>subagent_type</c>.</summary>
    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>True when the task runs in the background. TS <c>is_backgrounded</c>.</summary>
    [JsonPropertyName("is_backgrounded")]
    public bool? IsBackgrounded { get; init; }

    /// <summary>Nesting depth of the spawning agent. TS <c>spawn_depth</c>.</summary>
    [JsonPropertyName("spawn_depth")]
    public int? SpawnDepth { get; init; }

    /// <summary>Workflow name, for workflow tasks. TS <c>workflow_name</c>.</summary>
    [JsonPropertyName("workflow_name")]
    public string? WorkflowName { get; init; }

    /// <summary>Prompt the task was started with. TS <c>prompt</c>.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    /// <summary>When true, the task's messages are not written to the transcript. TS <c>skip_transcript</c>.</summary>
    [JsonPropertyName("skip_transcript")]
    public bool? SkipTranscript { get; init; }

    /// <summary>True for ambient (non-user-initiated) tasks. TS <c>ambient</c>.</summary>
    [JsonPropertyName("ambient")]
    public bool? Ambient { get; init; }
}

/// <summary>
/// System message emitted while a task is in progress. Python commit 9af27d7.
/// </summary>
public record TaskProgressMessage : SystemMessage
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("usage")]
    public required TaskUsage Usage { get; init; }

    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("last_tool_name")]
    public string? LastToolName { get; init; }

    /// <summary>Sub-agent type that runs the task. TS <c>subagent_type</c>.</summary>
    [JsonPropertyName("subagent_type")]
    public string? SubagentType { get; init; }

    /// <summary>Progress summary. TS <c>summary</c>.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

/// <summary>
/// System message emitted when a task completes, fails, or is stopped.
/// Python commit 9af27d7.
/// </summary>
public record TaskNotificationMessage : SystemMessage
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("status")]
    public required TaskNotificationStatus Status { get; init; }

    [JsonPropertyName("output_file")]
    public required string OutputFile { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("usage")]
    public TaskUsage? Usage { get; init; }

    /// <summary>The exact <c>status</c> string from the wire (preserved when <see cref="Status"/> is Unknown).</summary>
    [JsonPropertyName("status_raw")]
    public string? StatusRaw { get; init; }

    /// <summary>Why the task ended, e.g. <c>"worker_restart"</c>. TS <c>reason</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>MCP resource links produced by the task. TS <c>resource_links</c>.</summary>
    [JsonPropertyName("resource_links")]
    public IReadOnlyList<McpResourceLink>? ResourceLinks { get; init; }

    /// <summary>When true, the task's messages are not written to the transcript. TS <c>skip_transcript</c>.</summary>
    [JsonPropertyName("skip_transcript")]
    public bool? SkipTranscript { get; init; }

    /// <summary>True for ambient (non-user-initiated) tasks. TS <c>ambient</c>.</summary>
    [JsonPropertyName("ambient")]
    public bool? Ambient { get; init; }
}

#endregion

#region MirrorErrorMessage (T17)

/// <summary>
/// System message emitted when a SessionStore.AppendAsync call fails. Non-fatal.
/// Python commit 6e3d54f.
/// </summary>
public record MirrorErrorMessage : SystemMessage
{
    [JsonPropertyName("key")]
    public SessionKey? Key { get; init; }

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}

#endregion

#region Rate Limits (T18)

/// <summary>Rate limit status values.</summary>
public enum RateLimitStatus
{
    Allowed,
    AllowedWarning,
    Rejected,
    /// <summary>
    /// A status this SDK version doesn't recognize (see <see cref="RateLimitInfo.Raw"/>).
    /// Python passes the raw value through rather than failing the stream.
    /// </summary>
    Unknown
}

/// <summary>Rate limit window types.</summary>
public enum RateLimitType
{
    FiveHour,
    SevenDay,
    SevenDayOpus,
    SevenDaySonnet,
    Overage,
    /// <summary>Seven-day window including overage. TS 0.3.283.</summary>
    SevenDayOverageIncluded
}

internal static class RateLimitEnumHelpers
{
    public static string ToJsonString(this RateLimitStatus s) => s switch
    {
        RateLimitStatus.Allowed => "allowed",
        RateLimitStatus.AllowedWarning => "allowed_warning",
        RateLimitStatus.Rejected => "rejected",
        _ => s.ToString().ToLowerInvariant()
    };

    public static RateLimitStatus? ParseRateLimitStatus(string? value) => value switch
    {
        "allowed" => RateLimitStatus.Allowed,
        "allowed_warning" => RateLimitStatus.AllowedWarning,
        "rejected" => RateLimitStatus.Rejected,
        _ => null
    };

    public static string ToJsonString(this RateLimitType t) => t switch
    {
        RateLimitType.FiveHour => "five_hour",
        RateLimitType.SevenDay => "seven_day",
        RateLimitType.SevenDayOpus => "seven_day_opus",
        RateLimitType.SevenDaySonnet => "seven_day_sonnet",
        RateLimitType.Overage => "overage",
        RateLimitType.SevenDayOverageIncluded => "seven_day_overage_included",
        _ => t.ToString().ToLowerInvariant()
    };

    public static RateLimitType? ParseRateLimitType(string? value) => value switch
    {
        "five_hour" => RateLimitType.FiveHour,
        "seven_day" => RateLimitType.SevenDay,
        "seven_day_opus" => RateLimitType.SevenDayOpus,
        "seven_day_sonnet" => RateLimitType.SevenDaySonnet,
        "overage" => RateLimitType.Overage,
        "seven_day_overage_included" => RateLimitType.SevenDayOverageIncluded,
        _ => null
    };
}

/// <summary>
/// Rate limit status emitted by the CLI when rate limit state changes.
/// Python commit 2d5c3cb.
/// </summary>
public record RateLimitInfo
{
    [JsonPropertyName("status")]
    public required RateLimitStatus Status { get; init; }

    [JsonPropertyName("resets_at")]
    public long? ResetsAt { get; init; }

    [JsonPropertyName("rate_limit_type")]
    public RateLimitType? RateLimitType { get; init; }

    [JsonPropertyName("utilization")]
    public double? Utilization { get; init; }

    [JsonPropertyName("overage_status")]
    public RateLimitStatus? OverageStatus { get; init; }

    [JsonPropertyName("overage_resets_at")]
    public long? OverageResetsAt { get; init; }

    [JsonPropertyName("overage_disabled_reason")]
    public string? OverageDisabledReason { get; init; }

    [JsonPropertyName("raw")]
    public JsonElement? Raw { get; init; }

    /// <summary>
    /// The exact <c>rateLimitType</c> string from the wire, preserved when
    /// <see cref="RateLimitType"/> is <c>null</c> because the value is new.
    /// </summary>
    [JsonPropertyName("rate_limit_type_raw")]
    public string? RateLimitTypeRaw { get; init; }

    /// <summary>TS <c>isUsingOverage</c>.</summary>
    [JsonPropertyName("is_using_overage")]
    public bool? IsUsingOverage { get; init; }

    /// <summary>TS <c>overageInUse</c>.</summary>
    [JsonPropertyName("overage_in_use")]
    public bool? OverageInUse { get; init; }

    /// <summary>Utilization threshold that was crossed. TS <c>surpassedThreshold</c>.</summary>
    [JsonPropertyName("surpassed_threshold")]
    public double? SurpassedThreshold { get; init; }

    /// <summary><c>service</c>, <c>channel</c> or <c>group_pool</c>. TS <c>limitScope</c>.</summary>
    [JsonPropertyName("limit_scope")]
    public string? LimitScope { get; init; }

    /// <summary>E.g. <c>credits_required</c>. TS <c>errorCode</c>.</summary>
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    /// <summary>TS <c>canUserPurchaseCredits</c>.</summary>
    [JsonPropertyName("can_user_purchase_credits")]
    public bool? CanUserPurchaseCredits { get; init; }

    /// <summary>TS <c>hasChargeableSavedPaymentMethod</c>.</summary>
    [JsonPropertyName("has_chargeable_saved_payment_method")]
    public bool? HasChargeableSavedPaymentMethod { get; init; }
}

/// <summary>
/// Rate limit event emitted when rate limit info changes. Python commit 2d5c3cb.
/// </summary>
public record RateLimitEvent : Message
{
    [JsonPropertyName("rate_limit_info")]
    public required RateLimitInfo RateLimitInfo { get; init; }

    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }
}

#endregion

#region HookEventMessage (T19)

/// <summary>
/// Hook event emitted by the CLI when include_hook_events is enabled.
/// Python commit c1182a4.
/// </summary>
public record HookEventMessage : SystemMessage
{
    [JsonPropertyName("hook_event_name")]
    public string HookEventName { get; init; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>Hook execution id (correlates started/progress/response). TS <c>hook_id</c>.</summary>
    [JsonPropertyName("hook_id")]
    public string? HookId { get; init; }

    /// <summary>Hook name. TS <c>hook_name</c>.</summary>
    [JsonPropertyName("hook_name")]
    public string? HookName { get; init; }

    /// <summary>Combined hook output (hook_progress / hook_response). TS <c>output</c>.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; init; }

    /// <summary>Hook stdout (hook_progress / hook_response). TS <c>stdout</c>.</summary>
    [JsonPropertyName("stdout")]
    public string? Stdout { get; init; }

    /// <summary>Hook stderr (hook_progress / hook_response). TS <c>stderr</c>.</summary>
    [JsonPropertyName("stderr")]
    public string? Stderr { get; init; }

    /// <summary>Process exit code (hook_response). TS <c>exit_code</c>.</summary>
    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    /// <summary><c>success</c>, <c>error</c> or <c>cancelled</c> (hook_response). TS <c>outcome</c>.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }
}

/// <summary>
/// Streaming hook output (<c>hook_progress</c>). Derives from
/// <see cref="HookEventMessage"/> so hook consumers see the whole lifecycle;
/// Python leaves this subtype as a plain <see cref="SystemMessage"/>.
/// TS <c>SDKHookProgressMessage</c>.
/// </summary>
public record HookProgressMessage : HookEventMessage
{
    /// <summary>Create an empty hook progress message.</summary>
    public HookProgressMessage()
    {
    }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    internal HookProgressMessage(HookEventMessage source) : base(source)
    {
    }
}

#endregion

#region MCP Status Types (T20)

/// <summary>Connection status values for an MCP server.</summary>
/// <remarks>Serialized as the CLI's strings: <c>"connected"</c>, <c>"failed"</c>,
/// <c>"needs-auth"</c>, <c>"pending"</c>, <c>"disabled"</c>.</remarks>
[JsonConverter(typeof(McpServerConnectionStatusJsonConverter))]
public enum McpServerConnectionStatus
{
    Connected,
    Failed,
    NeedsAuth,
    Pending,
    Disabled
}

/// <summary>
/// Maps <see cref="McpServerConnectionStatus"/> to and from the CLI's wire strings
/// (Python: <c>Literal["connected", "failed", "needs-auth", "pending", "disabled"]</c>).
/// </summary>
internal sealed class McpServerConnectionStatusJsonConverter : JsonConverter<McpServerConnectionStatus>
{
    public override McpServerConnectionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string MCP server status, got {reader.TokenType}.");
        var value = reader.GetString();
        return McpEnumHelpers.ParseConnectionStatus(value)
               ?? throw new JsonException($"Unknown MCP server status '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, McpServerConnectionStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToJsonString());
}

internal static class McpEnumHelpers
{
    public static McpServerConnectionStatus? ParseConnectionStatus(string? value) => value switch
    {
        "connected" => McpServerConnectionStatus.Connected,
        "failed" => McpServerConnectionStatus.Failed,
        "needs-auth" => McpServerConnectionStatus.NeedsAuth,
        "pending" => McpServerConnectionStatus.Pending,
        "disabled" => McpServerConnectionStatus.Disabled,
        _ => null
    };

    public static string ToJsonString(this McpServerConnectionStatus s) => s switch
    {
        McpServerConnectionStatus.Connected => "connected",
        McpServerConnectionStatus.Failed => "failed",
        McpServerConnectionStatus.NeedsAuth => "needs-auth",
        McpServerConnectionStatus.Pending => "pending",
        McpServerConnectionStatus.Disabled => "disabled",
        _ => s.ToString().ToLowerInvariant()
    };
}

/// <summary>Server info from MCP initialize handshake. Python commit 28f9b4b.
/// Named "Status" to disambiguate from <c>Claude.AgentSdk.Mcp</c>'s
/// in-process MCP server types.</summary>
public record McpStatusServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version
);

/// <summary>Tool annotations as returned in MCP server status. Python commit 28f9b4b.
/// Named "Status" to disambiguate from <c>Claude.AgentSdk.Mcp</c>'s
/// in-process MCP server <c>McpToolAnnotations</c>.</summary>
public record McpStatusToolAnnotations
{
    [JsonPropertyName("readOnly")]
    public bool? ReadOnly { get; init; }

    [JsonPropertyName("destructive")]
    public bool? Destructive { get; init; }

    [JsonPropertyName("openWorld")]
    public bool? OpenWorld { get; init; }
}

/// <summary>Information about a tool provided by an MCP server. Python commit 28f9b4b.
/// Named "Status" to disambiguate from <c>Claude.AgentSdk.Mcp</c>'s
/// in-process MCP server <c>McpToolDefinition</c>.</summary>
public record McpStatusToolInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("annotations")]
    public McpStatusToolAnnotations? Annotations { get; init; }

    /// <summary>Tool <c>_meta</c> object, raw. TS 0.3.283.</summary>
    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// SDK MCP server config as returned in status responses (no instance). Python commit 28f9b4b.
/// </summary>
public record McpSdkServerConfigStatus
{
    [JsonPropertyName("type")]
    public string Type => "sdk";

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// Claude.ai proxy MCP server config (output-only). Python commit 28f9b4b.
/// </summary>
public record McpClaudeAIProxyServerConfig
{
    [JsonPropertyName("type")]
    public string Type => "claudeai-proxy";

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

/// <summary>
/// Broader config type for status responses (includes claudeai-proxy).
/// In Python this is a union — modeled here as an opaque <see cref="JsonElement"/>.
/// Python commit 28f9b4b.
/// </summary>
public record McpServerStatusConfig
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Full raw config payload — opaque structural supertype.</summary>
    // `set`, not `init`: source-generated (AOT) metadata cannot bind extension data through an initializer.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}

/// <summary>Status information for an MCP server connection. Python commit 28f9b4b.</summary>
public record McpServerStatus
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("status")]
    public required McpServerConnectionStatus Status { get; init; }

    [JsonPropertyName("serverInfo")]
    public McpStatusServerInfo? ServerInfo { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("config")]
    public McpServerStatusConfig? Config { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>Where the server config came from. TS <c>McpServerStatus.source</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<McpStatusToolInfo>? Tools { get; init; }
}

/// <summary>Response from ClaudeSDKClient.GetMcpStatus(). Python commit 28f9b4b.</summary>
public record McpStatusResponse
{
    [JsonPropertyName("mcpServers")]
    public required IReadOnlyList<McpServerStatus> McpServers { get; init; }
}

#endregion

#region Context Usage Types (T21)

/// <summary>A single context usage category. Python commit ac900bd.</summary>
public record ContextUsageCategory
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("tokens")]
    public required int Tokens { get; init; }

    [JsonPropertyName("color")]
    public required string Color { get; init; }

    [JsonPropertyName("isDeferred")]
    public bool? IsDeferred { get; init; }
}

/// <summary>Response from ClaudeSDKClient.GetContextUsage(). Python commit ac900bd.</summary>
public record ContextUsageResponse
{
    [JsonPropertyName("categories")]
    public required IReadOnlyList<ContextUsageCategory> Categories { get; init; }

    [JsonPropertyName("totalTokens")]
    public required int TotalTokens { get; init; }

    [JsonPropertyName("maxTokens")]
    public required int MaxTokens { get; init; }

    [JsonPropertyName("rawMaxTokens")]
    public required int RawMaxTokens { get; init; }

    [JsonPropertyName("percentage")]
    public required double Percentage { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("isAutoCompactEnabled")]
    public required bool IsAutoCompactEnabled { get; init; }

    [JsonPropertyName("memoryFiles")]
    public IReadOnlyList<JsonElement> MemoryFiles { get; init; } = [];

    [JsonPropertyName("mcpTools")]
    public IReadOnlyList<JsonElement> McpTools { get; init; } = [];

    [JsonPropertyName("agents")]
    public IReadOnlyList<JsonElement> Agents { get; init; } = [];

    [JsonPropertyName("gridRows")]
    public IReadOnlyList<IReadOnlyList<JsonElement>> GridRows { get; init; } = [];

    [JsonPropertyName("autoCompactThreshold")]
    public int? AutoCompactThreshold { get; init; }

    [JsonPropertyName("deferredBuiltinTools")]
    public IReadOnlyList<JsonElement>? DeferredBuiltinTools { get; init; }

    [JsonPropertyName("systemTools")]
    public IReadOnlyList<JsonElement>? SystemTools { get; init; }

    [JsonPropertyName("systemPromptSections")]
    public IReadOnlyList<JsonElement>? SystemPromptSections { get; init; }

    [JsonPropertyName("slashCommands")]
    public JsonElement? SlashCommands { get; init; }

    [JsonPropertyName("skills")]
    public JsonElement? Skills { get; init; }

    [JsonPropertyName("messageBreakdown")]
    public JsonElement? MessageBreakdown { get; init; }

    [JsonPropertyName("apiUsage")]
    public JsonElement? ApiUsage { get; init; }
}

#endregion

#region SDK Control Permission Request (T11)

/// <summary>
/// Control protocol request for can_use_tool. Python commit reference:
/// SDKControlPermissionRequest in types.py. PermissionSuggestions is tightened
/// to <see cref="PermissionUpdate"/> rather than a raw object list.
/// </summary>
public record SDKControlPermissionRequest
{
    [JsonPropertyName("subtype")]
    public string Subtype => "can_use_tool";

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }

    [JsonPropertyName("permission_suggestions")]
    public IReadOnlyList<PermissionUpdate>? PermissionSuggestions { get; init; }

    [JsonPropertyName("blocked_path")]
    public string? BlockedPath { get; init; }

    [JsonPropertyName("decision_reason")]
    public string? DecisionReason { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }
}

#endregion

#region Typed Hook-Specific Outputs (T9)

/// <summary>
/// Hook-specific output for PreToolUse events. Includes "defer" decision.
/// Python commit f5a1b67.
/// </summary>
public record PreToolUseHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.PreToolUseHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PreToolUse";

    /// <summary>
    /// Permission decision: "allow", "deny", "ask", or "defer". Python commit f5a1b67.
    /// </summary>
    [JsonPropertyName("permissionDecision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionDecision { get; init; }

    [JsonPropertyName("permissionDecisionReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionDecisionReason { get; init; }

    [JsonPropertyName("updatedInput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedInput { get; init; }

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>
/// Hook-specific output for PostToolUse events. Python commit b0b652f.
/// </summary>
public record PostToolUseHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.PostToolUseHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PostToolUse";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }

    /// <summary>Extra context for the auto-mode classifier. TS 0.3.283.</summary>
    [JsonPropertyName("classifierContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassifierContext { get; init; }

    /// <summary>Replaces the tool output before it is sent to the model. Python commit b0b652f.</summary>
    [JsonPropertyName("updatedToolOutput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedToolOutput { get; init; }

    /// <summary>Replaces the output for MCP tools only. Prefer UpdatedToolOutput. Python commit b0b652f.</summary>
    [JsonPropertyName("updatedMCPToolOutput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedMCPToolOutput { get; init; }
}

/// <summary>Hook-specific output for PostToolUseFailure events. Python: <c>PostToolUseFailureHookSpecificOutput</c>.</summary>
public record PostToolUseFailureHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.PostToolUseFailureHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PostToolUseFailure";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for UserPromptSubmit events. Python: <c>UserPromptSubmitHookSpecificOutput</c>.</summary>
public record UserPromptSubmitHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.UserPromptSubmitHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "UserPromptSubmit";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }

    /// <summary>Sets the session title. TS 0.3.283.</summary>
    [JsonPropertyName("sessionTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionTitle { get; init; }

    /// <summary>When true, the original prompt is not sent to the model. TS 0.3.283.</summary>
    [JsonPropertyName("suppressOriginalPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SuppressOriginalPrompt { get; init; }
}

/// <summary>Hook-specific output for SessionStart events. Python: <c>SessionStartHookSpecificOutput</c>.</summary>
public record SessionStartHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.SessionStartHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "SessionStart";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }

    /// <summary>A user message to start the session with. TS 0.3.283.</summary>
    [JsonPropertyName("initialUserMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitialUserMessage { get; init; }

    /// <summary>Sets the session title. TS 0.3.283.</summary>
    [JsonPropertyName("sessionTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionTitle { get; init; }

    /// <summary>Paths to watch; changes fire <see cref="HookEvent.FileChanged"/>. TS 0.3.283.</summary>
    [JsonPropertyName("watchPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WatchPaths { get; init; }

    /// <summary>Reload skills after the hook runs. TS 0.3.283.</summary>
    [JsonPropertyName("reloadSkills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReloadSkills { get; init; }
}

/// <summary>Hook-specific output for Notification events. Python: <c>NotificationHookSpecificOutput</c>.</summary>
public record NotificationHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.NotificationHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "Notification";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for SubagentStart events. Python: <c>SubagentStartHookSpecificOutput</c>.</summary>
public record SubagentStartHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.SubagentStartHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "SubagentStart";

    [JsonPropertyName("additionalContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
}

/// <summary>Hook-specific output for PermissionRequest events. Python: <c>PermissionRequestHookSpecificOutput</c>.</summary>
public record PermissionRequestHookSpecificOutput
{
    /// <summary>
    /// This output as the JSON element for <see cref="HookOutput.HookSpecificOutput"/>, with unset
    /// members omitted. Uses source-generated metadata, so it is trim- and NativeAOT-safe.
    /// </summary>
    public JsonElement ToJsonElement() =>
        JsonSerializer.SerializeToElement(this, Internal.SdkJsonContext.Default.PermissionRequestHookSpecificOutput);

    [JsonPropertyName("hookEventName")]
    public string HookEventName => "PermissionRequest";

    /// <summary>
    /// The permission decision object, passed to the CLI as-is. Build it from a
    /// <see cref="PermissionRequestDecision"/> with <see cref="From"/>, or read
    /// it back with <see cref="TypedDecision"/>.
    /// </summary>
    [JsonPropertyName("decision")]
    public required JsonElement Decision { get; init; }

    /// <summary>Typed view of <see cref="Decision"/>; <c>null</c> if it isn't a recognized allow/deny object.</summary>
    [JsonIgnore]
    public PermissionRequestDecision? TypedDecision => PermissionRequestDecision.Parse(Decision);

    /// <summary>Create an output carrying a typed allow/deny decision (TS union).</summary>
    public static PermissionRequestHookSpecificOutput From(PermissionRequestDecision decision) =>
        new() { Decision = decision.ToJsonElement() };
}

#endregion
