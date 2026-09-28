// Claude Agent SDK for .NET
// Parsers for message shapes added for TypeScript SDK parity (0.3.283).
// All of them are lenient: a missing or mistyped field becomes a default so a
// shape change in a newer CLI can never fail the message stream. The full
// frame is always reachable via Message.Raw / SystemMessage.Data.

using System.Text.Json;
using static Claude.AgentSdk.Internal.JsonRead;

namespace Claude.AgentSdk.Internal;

internal static class TsParsing
{
    public static IReadOnlyList<PermissionDenial> ParsePermissionDenials(JsonElement? denials)
    {
        if (denials is not { ValueKind: JsonValueKind.Array } arr)
            return [];
        var list = new List<PermissionDenial>();
        foreach (var d in arr.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object)
                continue;
            list.Add(new PermissionDenial
            {
                ToolName = Str(d, "tool_name") ?? string.Empty,
                ToolUseId = Str(d, "tool_use_id") ?? string.Empty,
                ToolInput = d.TryGetProperty("tool_input", out var ti) ? ti.Clone() : default
            });
        }
        return list;
    }

    public static McpResourceLink? ParseResourceLink(JsonElement e) =>
        Str(e, "uri") is { } uri
            ? new McpResourceLink
            {
                Uri = uri,
                Name = Str(e, "name") ?? string.Empty,
                Title = Str(e, "title"),
                Description = Str(e, "description"),
                MimeType = Str(e, "mimeType"),
                Size = Long(e, "size"),
                Annotations = Raw(e, "annotations")
            }
            : null;

    #region Top-level messages

    public static ToolProgressMessage ParseToolProgress(JsonElement d)
    {
        SubagentRetryInfo? retry = null;
        if (Obj(d, "subagent_retry") is { } r)
        {
            retry = new SubagentRetryInfo
            {
                AgentId = Str(r, "agent_id") ?? string.Empty,
                Attempt = Int(r, "attempt") ?? 0,
                MaxRetries = Int(r, "max_retries") ?? 0,
                RetryDelayMs = Dbl(r, "retry_delay_ms") ?? 0,
                ErrorStatus = Int(r, "error_status"),
                ErrorCategory = Str(r, "error_category") ?? string.Empty
            };
        }
        return new ToolProgressMessage
        {
            ToolUseId = Str(d, "tool_use_id") ?? string.Empty,
            ToolName = Str(d, "tool_name") ?? string.Empty,
            ParentToolUseId = Str(d, "parent_tool_use_id"),
            ElapsedTimeSeconds = Dbl(d, "elapsed_time_seconds") ?? 0,
            TaskId = Str(d, "task_id"),
            Uuid = Str(d, "uuid"),
            SessionId = Str(d, "session_id"),
            Heartbeat = Bool(d, "heartbeat"),
            SubagentType = Str(d, "subagent_type"),
            SubagentRetry = retry
        };
    }

    public static ToolUseSummaryMessage ParseToolUseSummary(JsonElement d) => new()
    {
        Summary = Str(d, "summary") ?? string.Empty,
        PrecedingToolUseIds = StrList(d, "preceding_tool_use_ids") ?? [],
        Uuid = Str(d, "uuid"),
        SessionId = Str(d, "session_id")
    };

    public static AuthStatusMessage ParseAuthStatus(JsonElement d) => new()
    {
        IsAuthenticating = Bool(d, "isAuthenticating") ?? false,
        Output = StrList(d, "output") ?? [],
        Error = Str(d, "error"),
        Uuid = Str(d, "uuid"),
        SessionId = Str(d, "session_id")
    };

    public static PromptSuggestionMessage ParsePromptSuggestion(JsonElement d) => new()
    {
        Suggestion = Str(d, "suggestion") ?? string.Empty,
        Uuid = Str(d, "uuid"),
        SessionId = Str(d, "session_id")
    };

    public static ActiveGoalMessage ParseActiveGoal(JsonElement d)
    {
        ActiveGoal? goal = null;
        if (Obj(d, "value") is { } v)
        {
            goal = new ActiveGoal
            {
                Condition = Str(v, "condition") ?? string.Empty,
                Iterations = Int(v, "iterations") ?? 0,
                SetAt = Dbl(v, "set_at") ?? 0,
                TokensAtStart = Long(v, "tokens_at_start") ?? 0,
                LastReason = Str(v, "last_reason")
            };
        }
        return new ActiveGoalMessage { Value = goal, Uuid = Str(d, "uuid"), SessionId = Str(d, "session_id") };
    }

    #endregion

    #region System subtypes

    /// <summary>
    /// Typed message for a <c>system</c> subtype added for TS parity, or
    /// <c>null</c> when the subtype isn't one of them.
    /// </summary>
    public static SystemMessage? ParseSystemSubtype(string subtype, JsonElement d, JsonElement clone)
    {
        var uuid = Str(d, "uuid");
        var sessionId = Str(d, "session_id");
        switch (subtype)
        {
            case "init":
                return new SystemInitMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Agents = StrList(d, "agents"),
                    ApiKeySource = Str(d, "apiKeySource"),
                    Betas = StrList(d, "betas"),
                    ClaudeCodeVersion = Str(d, "claude_code_version"),
                    Cwd = Str(d, "cwd"),
                    Tools = StrList(d, "tools") ?? [],
                    McpServers = ObjList(d, "mcp_servers", m => Str(m, "name") is { } n
                        ? new InitMcpServer(n, Str(m, "status") ?? string.Empty, Str(m, "source"))
                        : null),
                    Model = Str(d, "model"),
                    PermissionMode = Str(d, "permissionMode"),
                    SlashCommands = StrList(d, "slash_commands") ?? [],
                    TerminalSlashCommands = StrList(d, "terminal_slash_commands"),
                    OutputStyle = Str(d, "output_style"),
                    Skills = StrList(d, "skills") ?? [],
                    Plugins = ObjList(d, "plugins", p => Str(p, "name") is { } n
                        ? new InitPlugin(n, Str(p, "path") ?? string.Empty, Str(p, "version"))
                        : null),
                    PluginErrors = ObjListOrNull(d, "plugin_errors", p => new InitPluginError(
                        Str(p, "plugin") ?? string.Empty, Str(p, "type") ?? string.Empty,
                        Str(p, "message") ?? string.Empty, Str(p, "path"))),
                    FastModeState = Str(d, "fast_mode_state"),
                    FastModeDisabledReason = Str(d, "fast_mode_disabled_reason"),
                    Effort = Str(d, "effort"),
                    ViewMode = Str(d, "view_mode"),
                    Capabilities = StrList(d, "capabilities")
                };
            case "compact_boundary":
            {
                CompactMetadata? meta = null;
                if (Obj(d, "compact_metadata") is { } m)
                {
                    meta = new CompactMetadata
                    {
                        Trigger = Str(m, "trigger") ?? string.Empty,
                        PreTokens = Long(m, "pre_tokens") ?? 0,
                        PostTokens = Long(m, "post_tokens"),
                        DurationMs = Dbl(m, "duration_ms"),
                        PreservedSegment = Obj(m, "preserved_segment") is { } ps
                            ? new CompactPreservedSegment(Str(ps, "head_uuid") ?? string.Empty,
                                Str(ps, "anchor_uuid") ?? string.Empty, Str(ps, "tail_uuid") ?? string.Empty)
                            : null,
                        PreservedMessages = Obj(m, "preserved_messages") is { } pm
                            ? new CompactPreservedMessages(Str(pm, "anchor_uuid") ?? string.Empty, StrList(pm, "uuids") ?? [])
                            : null
                    };
                }
                return new CompactBoundaryMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId, CompactMetadata = meta
                };
            }
            case "status":
                return new StatusMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Status = Str(d, "status"),
                    PermissionMode = Str(d, "permissionMode"),
                    CompactResult = Str(d, "compact_result"),
                    CompactError = Str(d, "compact_error")
                };
            case "api_retry":
            {
                var errorRaw = Str(d, "error");
                return new ApiRetryMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Attempt = Int(d, "attempt") ?? 0,
                    MaxRetries = Int(d, "max_retries") ?? 0,
                    RetryDelayMs = Dbl(d, "retry_delay_ms") ?? 0,
                    ErrorStatus = Int(d, "error_status"),
                    Error = AssistantMessageErrors.Parse(errorRaw),
                    ErrorRaw = errorRaw,
                    NoResponse = Obj(d, "no_response") is { } nr
                        ? new ApiRetryNoResponse(Dbl(nr, "waited_ms") ?? 0, Dbl(nr, "retry_wait_ms") ?? 0)
                        : null
                };
            }
            case "control_request_progress":
                return new ControlRequestProgressMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    RequestId = Str(d, "request_id") ?? string.Empty,
                    Status = Str(d, "status") ?? string.Empty,
                    Attempt = Int(d, "attempt"),
                    MaxRetries = Int(d, "max_retries"),
                    RetryDelayMs = Dbl(d, "retry_delay_ms"),
                    ErrorStatus = Int(d, "error_status")
                };
            case "model_refusal_fallback":
                return new ModelRefusalFallbackMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Trigger = Str(d, "trigger"),
                    Direction = Str(d, "direction"),
                    Scope = Str(d, "scope"),
                    OriginalModel = Str(d, "original_model") ?? string.Empty,
                    FallbackModel = Str(d, "fallback_model") ?? string.Empty,
                    RequestId = Str(d, "request_id"),
                    ApiRefusalCategory = Str(d, "api_refusal_category"),
                    ApiRefusalExplanation = Str(d, "api_refusal_explanation"),
                    RetractedMessageUuids = StrList(d, "retracted_message_uuids"),
                    RefusedUserMessageUuid = Str(d, "refused_user_message_uuid"),
                    Content = Str(d, "content") ?? string.Empty
                };
            case "model_refusal_no_fallback":
                return new ModelRefusalNoFallbackMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    OriginalModel = Str(d, "original_model") ?? string.Empty,
                    RequestId = Str(d, "request_id"),
                    ApiRefusalCategory = Str(d, "api_refusal_category"),
                    ApiRefusalExplanation = Str(d, "api_refusal_explanation"),
                    RefusedUserMessageUuid = Str(d, "refused_user_message_uuid"),
                    Content = Str(d, "content") ?? string.Empty
                };
            case "local_command_output":
                return new LocalCommandOutputMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Content = Str(d, "content") ?? string.Empty
                };
            case "plugin_install":
                return new PluginInstallMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Status = Str(d, "status") ?? string.Empty,
                    Name = Str(d, "name"),
                    Error = Str(d, "error")
                };
            case "background_tasks_changed":
                return new BackgroundTasksChangedMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Tasks = ObjList(d, "tasks", t => new BackgroundTaskInfo(
                        Str(t, "task_id") ?? string.Empty, Str(t, "task_type") ?? string.Empty,
                        Str(t, "description") ?? string.Empty, Bool(t, "ambient")))
                };
            case "thinking_tokens":
                return new ThinkingTokensMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    EstimatedTokens = Long(d, "estimated_tokens") ?? 0,
                    EstimatedTokensDelta = Long(d, "estimated_tokens_delta") ?? 0,
                    UserMessageUuid = Str(d, "user_message_uuid")
                };
            case "session_state_changed":
                return new SessionStateChangedMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    State = Str(d, "state") ?? string.Empty
                };
            case "worker_shutting_down":
                return new WorkerShuttingDownMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Reason = Str(d, "reason") ?? string.Empty
                };
            case "commands_changed":
                return new CommandsChangedMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Commands = ObjList(d, "commands", SlashCommand.Parse)
                };
            case "notification":
                return new NotificationMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Key = Str(d, "key") ?? string.Empty,
                    Text = Str(d, "text") ?? string.Empty,
                    Priority = Str(d, "priority"),
                    Color = Str(d, "color"),
                    TimeoutMs = Dbl(d, "timeout_ms")
                };
            case "files_persisted":
                return new FilesPersistedMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Files = ObjList(d, "files", f => new PersistedFile(Str(f, "filename") ?? string.Empty, Str(f, "file_id") ?? string.Empty)),
                    Failed = ObjList(d, "failed", f => new FailedPersistedFile(Str(f, "filename") ?? string.Empty, Str(f, "error") ?? string.Empty)),
                    ProcessedAt = Str(d, "processed_at")
                };
            case "memory_recall":
                return new MemoryRecallMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Mode = Str(d, "mode") ?? string.Empty,
                    Memories = ObjList(d, "memories", m => new RecalledMemory(
                        Str(m, "path") ?? string.Empty, Str(m, "scope") ?? string.Empty, Str(m, "content")))
                };
            case "elicitation_complete":
                return new ElicitationCompleteMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    McpServerName = Str(d, "mcp_server_name") ?? string.Empty,
                    ElicitationId = Str(d, "elicitation_id") ?? string.Empty
                };
            case "permission_denied":
                return new PermissionDeniedMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    ToolName = Str(d, "tool_name") ?? string.Empty,
                    ToolUseId = Str(d, "tool_use_id") ?? string.Empty,
                    AgentId = Str(d, "agent_id"),
                    DecisionReasonType = Str(d, "decision_reason_type"),
                    DecisionReason = Str(d, "decision_reason"),
                    Message = Str(d, "message") ?? string.Empty
                };
            case "informational":
                return new InformationalMessage
                {
                    Subtype = subtype, Data = clone, Uuid = uuid, SessionId = sessionId,
                    Content = Str(d, "content") ?? string.Empty,
                    Level = Str(d, "level") ?? string.Empty,
                    ToolUseId = Str(d, "tool_use_id"),
                    PreventContinuation = Bool(d, "prevent_continuation")
                };
            default:
                return null;
        }
    }

    #endregion
}
