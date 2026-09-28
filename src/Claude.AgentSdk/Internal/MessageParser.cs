// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/_internal/message_parser.py

using System.Diagnostics;
using System.Text.Json;

namespace Claude.AgentSdk.Internal;

/// <summary>
/// Parser for CLI output messages into typed Message objects.
/// </summary>
internal static class MessageParser
{
    /// <summary>
    /// Parse message from CLI output into typed Message objects. Every parsed
    /// message carries the full frame on <see cref="Message.Raw"/>.
    /// </summary>
    /// <remarks>
    /// Unrecognized top-level types become <see cref="UnknownMessage"/> (TS SDK
    /// behaviour). Python instead returns <c>None</c> for them (commit 146e3d6);
    /// this SDK follows TS so new CLI message types are never lost. Only
    /// internal frames (<c>keep_alive</c>) return <c>null</c>.
    /// </remarks>
    /// <param name="data">Raw message JSON from CLI output.</param>
    /// <returns>Parsed Message object, or <c>null</c> for internal frames.</returns>
    /// <exception cref="MessageParseException">If parsing fails for a known type.</exception>
    public static Message? ParseOrNull(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new MessageParseException(
                $"Invalid message data type (expected object, got {data.ValueKind})",
                data
            );
        }

        var msg = ParseCore(data);
        if (msg == null)
            return null;
        var raw = msg is SystemMessage sys && sys.Data.ValueKind == JsonValueKind.Object ? sys.Data : data.Clone();
        return msg with { Raw = raw };
    }

    private static Message? ParseCore(JsonElement data)
    {
        // Hook events arrive as system messages with subtype hook_started/hook_response
        // (Python commit c1182a4); hook_progress is routed here too (TS parity).
        if (Str(data, "type") == "system" &&
            Str(data, "subtype") is "hook_started" or "hook_response" or "hook_progress")
        {
            var subtype = Str(data, "subtype")!;
            var hookEventName =
                NonEmpty(Str(data, "hook_event"))
                ?? NonEmpty(Str(data, "hook_name"))
                ?? NonEmpty(Str(data, "hook_event_name"))
                ?? string.Empty;
            var hookMsg = new HookEventMessage
            {
                Subtype = subtype,
                Data = data.Clone(),
                HookEventName = hookEventName,
                SessionId = Str(data, "session_id"),
                Uuid = Str(data, "uuid"),
                HookId = Str(data, "hook_id"),
                HookName = Str(data, "hook_name"),
                Output = Str(data, "output"),
                Stdout = Str(data, "stdout"),
                Stderr = Str(data, "stderr"),
                ExitCode = Int(data, "exit_code"),
                Outcome = Str(data, "outcome")
            };
            return subtype == "hook_progress" ? new HookProgressMessage(hookMsg) : hookMsg;
        }

        var messageType = Str(data, "type");
        if (string.IsNullOrEmpty(messageType))
            throw new MessageParseException("Message missing 'type' field", data);

        return messageType switch
        {
            "user" => ParseUserMessage(data),
            "assistant" => ParseAssistantMessage(data),
            "system" => ParseSystemMessage(data),
            "result" => ParseResultMessage(data),
            "stream_event" => ParseStreamEvent(data),
            "rate_limit_event" => ParseRateLimitEvent(data),
            "conversation_reset" => ParseConversationReset(data),
            // TS 0.3.283 top-level types that Python drops.
            "tool_progress" => TsParsing.ParseToolProgress(data),
            "tool_use_summary" => TsParsing.ParseToolUseSummary(data),
            "auth_status" => TsParsing.ParseAuthStatus(data),
            "prompt_suggestion" => TsParsing.ParsePromptSuggestion(data),
            "active_goal" => TsParsing.ParseActiveGoal(data),
            // Internal liveness frame; the TS SDK swallows it too.
            "keep_alive" => SkipInternal(messageType),
            _ => new UnknownMessage { Type = messageType }
        };
    }

    private static Message? SkipInternal(string messageType)
    {
        Debug.WriteLine($"[MessageParser] Skipping internal message type: {messageType}");
        return null;
    }

    /// <summary>
    /// Legacy entry point: parse message and throw when the frame is internal
    /// (<c>keep_alive</c>). Unknown types parse as <see cref="UnknownMessage"/>.
    /// </summary>
    public static Message Parse(JsonElement data)
    {
        var msg = ParseOrNull(data);
        if (msg == null)
        {
            var t = data.TryGetProperty("type", out var te) ? te.GetString() : "<missing>";
            throw new MessageParseException($"Unknown message type: {t}", data);
        }
        return msg;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static JsonElement? Raw(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    /// <summary>Required string field; a missing key is a parse error (Python <c>KeyError</c>).</summary>
    private static string Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.GetString() ?? throw new KeyNotFoundException($"'{name}'")
            : throw new KeyNotFoundException($"'{name}'");

    /// <summary>
    /// Return <c>data.origin</c> if it is a well-formed origin object (an
    /// object with a string <c>kind</c>). Unmodelled keys are preserved in
    /// <see cref="MessageOrigin.AdditionalProperties"/>. Python: <c>_parse_origin</c>.
    /// </summary>
    internal static MessageOrigin? ParseOrigin(JsonElement data)
    {
        if (!data.TryGetProperty("origin", out var o) || o.ValueKind != JsonValueKind.Object)
            return null;
        var kind = Str(o, "kind");
        if (kind == null)
            return null;

        Dictionary<string, JsonElement>? extras = null;
        foreach (var prop in o.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "kind" or "server" or "from" or "name" or "fromSession" or "senderTaskId" or "body" or "subkind":
                    break;
                case "verifiedPeerPid" when prop.Value.ValueKind == JsonValueKind.Number:
                    break;
                default:
                    (extras ??= new())[prop.Name] = prop.Value.Clone();
                    break;
            }
        }

        return new MessageOrigin
        {
            Kind = kind,
            Server = Str(o, "server"),
            From = Str(o, "from"),
            Name = Str(o, "name"),
            FromSession = Str(o, "fromSession"),
            SenderTaskId = Str(o, "senderTaskId"),
            Body = Str(o, "body"),
            VerifiedPeerPid = o.TryGetProperty("verifiedPeerPid", out var pid) &&
                              pid.ValueKind == JsonValueKind.Number && pid.TryGetInt64(out var p)
                ? p
                : null,
            Subkind = Str(o, "subkind"),
            AdditionalProperties = extras
        };
    }

    /// <summary>
    /// Parse the blocks of a user message's content array. Python's user
    /// branch materializes only text / tool_use / tool_result blocks and skips
    /// anything else (images, documents, ...).
    /// </summary>
    internal static IReadOnlyList<ContentBlock> ParseUserContentBlocks(JsonElement content)
    {
        var blocks = new List<ContentBlock>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                throw new MessageParseException($"Invalid content block (expected object, got {block.ValueKind})", content);
            ContentBlock? parsed = Req(block, "type") switch
            {
                "text" => new TextBlock(Req(block, "text")),
                "tool_use" => new ToolUseBlock(Req(block, "id"), Req(block, "name"), block.GetProperty("input").Clone()),
                "tool_result" => new ToolResultBlock(Req(block, "tool_use_id"), Raw(block, "content"), Bool(block, "is_error")),
                _ => null
            };
            if (parsed != null)
                blocks.Add(parsed);
        }
        return blocks;
    }

    private static UserMessage ParseUserMessage(JsonElement data)
    {
        try
        {
            var message = data.GetProperty("message");
            var content = message.GetProperty("content");

            // Validate block shapes like Python does (a malformed block is a parse
            // error), while keeping the raw content on UserMessage.Content.
            if (content.ValueKind == JsonValueKind.Array)
                ParseUserContentBlocks(content);

            return new UserMessage
            {
                Content = content.Clone(),
                Uuid = Str(data, "uuid"),
                ParentToolUseId = Str(data, "parent_tool_use_id"),
                ToolUseResult = Raw(data, "tool_use_result"),
                Origin = ParseOrigin(data),
                SessionId = Str(data, "session_id"),
                IsSynthetic = Bool(data, "isSynthetic"),
                IsReplay = Bool(data, "isReplay"),
                Priority = Str(data, "priority"),
                Timestamp = Str(data, "timestamp"),
                ShouldQuery = Bool(data, "shouldQuery"),
                ClientComposed = Bool(data, "client_composed"),
                FileAttachments = Raw(data, "file_attachments"),
                PastedContent = Raw(data, "pasted_content"),
                InlinePastes = JsonRead.StrList(data, "inline_pastes"),
                SubagentType = Str(data, "subagent_type"),
                TaskDescription = Str(data, "task_description")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new MessageParseException($"Missing required field in user message: {ex.Message}", data);
        }
    }

    private static AssistantMessage ParseAssistantMessage(JsonElement data)
    {
        try
        {
            var message = data.GetProperty("message");
            var contentArray = message.GetProperty("content");
            if (contentArray.ValueKind != JsonValueKind.Array)
                throw new MessageParseException(
                    $"Invalid assistant content (expected list, got {contentArray.ValueKind})", data);
            var model = Req(message, "model");

            var contentBlocks = new List<ContentBlock>();

            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    throw new MessageParseException($"Invalid content block (expected object, got {block.ValueKind})", data);
                var blockType = Req(block, "type");
                ContentBlock contentBlock = blockType switch
                {
                    "text" => new TextBlock(Req(block, "text")),
                    "thinking" => new ThinkingBlock(Req(block, "thinking"), Req(block, "signature")),
                    "tool_use" => new ToolUseBlock(Req(block, "id"), Req(block, "name"), block.GetProperty("input").Clone()),
                    "tool_result" => new ToolResultBlock(Req(block, "tool_use_id"), Raw(block, "content"), Bool(block, "is_error")),
                    // Python commit 6ab97b4: server_tool_use / advisor_tool_result.
                    "server_tool_use" => new ServerToolUseBlock(Req(block, "id"), Req(block, "name"), block.GetProperty("input").Clone()),
                    "advisor_tool_result" => new ServerToolResultBlock(Req(block, "tool_use_id"), block.GetProperty("content").Clone())
                    {
                        ResultType = blockType
                    },
                    // TS parity: redacted thinking and the other server-tool result blocks.
                    "redacted_thinking" when Str(block, "data") is { } redacted => new RedactedThinkingBlock(redacted),
                    "web_search_tool_result" or "web_fetch_tool_result" or "code_execution_tool_result"
                        or "bash_code_execution_tool_result" or "text_editor_code_execution_tool_result"
                        or "tool_search_tool_result"
                        when Str(block, "tool_use_id") is { } resultFor && block.TryGetProperty("content", out var resultContent)
                        => new ServerToolResultBlock(resultFor, resultContent.Clone()) { ResultType = blockType },
                    // Python skips block types it doesn't know; this SDK keeps them
                    // raw (TS parity: content is passed through untouched).
                    _ => new RawContentBlock(blockType, block.Clone())
                };
                contentBlocks.Add(contentBlock);
            }

            // Python reads `error` from the top-level frame (data.get("error")),
            // not from the inner API message. The inner location is kept as a
            // fallback for frames produced by older SDK builds.
            var errorStr = Str(data, "error") ?? Str(message, "error");
            var error = AssistantMessageErrors.Parse(errorStr);

            return new AssistantMessage
            {
                Content = contentBlocks,
                Model = model,
                ParentToolUseId = Str(data, "parent_tool_use_id"),
                Error = error,
                // Python commit fc82420: preserve per-turn usage.
                Usage = Raw(message, "usage"),
                MessageId = Str(message, "id"),
                StopReason = Str(message, "stop_reason"),
                SessionId = Str(data, "session_id"),
                Uuid = Str(data, "uuid"),
                ErrorRaw = errorStr,
                RequestId = Str(data, "request_id"),
                UserMessageUuid = Str(data, "user_message_uuid"),
                UserMessageUuids = JsonRead.StrList(data, "user_message_uuids"),
                ResumeReason = Str(data, "resume_reason"),
                ResumedFromIncompleteThinking = Bool(data, "resumed_from_incomplete_thinking"),
                Supersedes = JsonRead.StrList(data, "supersedes"),
                Aborted = Bool(data, "aborted"),
                SubagentType = Str(data, "subagent_type"),
                TaskDescription = Str(data, "task_description"),
                Timestamp = Str(data, "timestamp"),
                ContextUsage = data.TryGetProperty("context_usage", out var cu) ? SdkContextUsage.Parse(cu) : null,
                UsageReport = data.TryGetProperty("usage_report", out var ur) ? SdkUsageReport.Parse(ur) : null,
                StopSequence = Str(message, "stop_sequence")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new MessageParseException($"Missing required field in assistant message: {ex.Message}", data);
        }
    }

    private static TaskUsage ParseTaskUsage(JsonElement u) => new(
        u.GetProperty("total_tokens").GetInt32(),
        u.GetProperty("tool_uses").GetInt32(),
        u.GetProperty("duration_ms").GetInt32());

    private static SystemMessage ParseSystemMessage(JsonElement data)
    {
        try
        {
            var subtype = Req(data, "subtype");
            var clone = data.Clone();

            // Python commit 9af27d7: task_started / task_progress / task_notification.
            switch (subtype)
            {
                case "task_started":
                    return new TaskStartedMessage
                    {
                        Subtype = subtype,
                        Data = clone,
                        TaskId = Req(data, "task_id"),
                        Description = Req(data, "description"),
                        Uuid = Req(data, "uuid"),
                        SessionId = Req(data, "session_id"),
                        ToolUseId = Str(data, "tool_use_id"),
                        TaskType = Str(data, "task_type"),
                        SubagentType = Str(data, "subagent_type"),
                        IsBackgrounded = Bool(data, "is_backgrounded"),
                        SpawnDepth = Int(data, "spawn_depth"),
                        WorkflowName = Str(data, "workflow_name"),
                        Prompt = Str(data, "prompt"),
                        SkipTranscript = Bool(data, "skip_transcript"),
                        Ambient = Bool(data, "ambient")
                    };
                case "task_progress":
                    return new TaskProgressMessage
                    {
                        Subtype = subtype,
                        Data = clone,
                        TaskId = Req(data, "task_id"),
                        Description = Req(data, "description"),
                        Usage = ParseTaskUsage(data.GetProperty("usage")),
                        Uuid = Req(data, "uuid"),
                        SessionId = Req(data, "session_id"),
                        ToolUseId = Str(data, "tool_use_id"),
                        LastToolName = Str(data, "last_tool_name"),
                        SubagentType = Str(data, "subagent_type"),
                        Summary = Str(data, "summary")
                    };
                case "task_notification":
                    var statusRaw = Req(data, "status");
                    var status = statusRaw switch
                    {
                        "completed" => TaskNotificationStatus.Completed,
                        "failed" => TaskNotificationStatus.Failed,
                        "stopped" => TaskNotificationStatus.Stopped,
                        "killed" => TaskNotificationStatus.Killed,
                        // Python passes the raw status through; never fail the stream on a new value.
                        _ => TaskNotificationStatus.Unknown
                    };
                    return new TaskNotificationMessage
                    {
                        Subtype = subtype,
                        Data = clone,
                        TaskId = Req(data, "task_id"),
                        Status = status,
                        OutputFile = Req(data, "output_file"),
                        Summary = Req(data, "summary"),
                        Uuid = Req(data, "uuid"),
                        SessionId = Req(data, "session_id"),
                        ToolUseId = Str(data, "tool_use_id"),
                        Usage = data.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                            ? ParseTaskUsage(u)
                            : null,
                        StatusRaw = statusRaw,
                        Reason = Str(data, "reason"),
                        ResourceLinks = JsonRead.ObjListOrNull(data, "resource_links", TsParsing.ParseResourceLink),
                        SkipTranscript = Bool(data, "skip_transcript"),
                        Ambient = Bool(data, "ambient")
                    };
                case "task_updated":
                    // Terminal task completion sometimes arrives only as a
                    // task_updated patch (no task_notification), so expose it as
                    // a typed lifecycle message. Parsed defensively: the patch may
                    // omit uuid/session_id and parsing must never raise on a
                    // lifecycle event. Python: TaskUpdatedMessage branch.
                    var patch = data.TryGetProperty("patch", out var p) && p.ValueKind == JsonValueKind.Object
                        ? p.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    return new TaskUpdatedMessage
                    {
                        Subtype = subtype,
                        Data = clone,
                        TaskId = Str(data, "task_id") ?? string.Empty,
                        Patch = patch,
                        Status = Str(patch, "status"),
                        SessionId = Str(data, "session_id"),
                        Uuid = Str(data, "uuid"),
                        TypedPatch = new TaskUpdatedPatch
                        {
                            Status = Str(patch, "status"),
                            Description = Str(patch, "description"),
                            EndTime = JsonRead.Dbl(patch, "end_time"),
                            TotalPausedMs = JsonRead.Dbl(patch, "total_paused_ms"),
                            Error = Str(patch, "error"),
                            IsBackgrounded = Bool(patch, "is_backgrounded")
                        }
                    };
                case "mirror_error":
                    // Python commit 6e3d54f: SDK-synthesized; never emitted by the CLI directly.
                    SessionKey? key = null;
                    if (data.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.Object)
                    {
                        key = new SessionKey
                        {
                            ProjectKey = Str(k, "project_key") ?? string.Empty,
                            SessionId = Str(k, "session_id") ?? string.Empty,
                            Subpath = Str(k, "subpath")
                        };
                    }
                    return new MirrorErrorMessage
                    {
                        Subtype = subtype,
                        Data = clone,
                        Key = key,
                        Error = Str(data, "error") ?? string.Empty
                    };
                default:
                    // TS 0.3.283 typed subtypes; anything else stays a plain SystemMessage.
                    return TsParsing.ParseSystemSubtype(subtype, data, clone)
                           ?? new SystemMessage
                           {
                               Subtype = subtype,
                               Data = clone
                           };
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MessageParseException($"Missing required field in system message: {ex.Message}", data);
        }
    }

    internal static ModelUsage ParseModelUsage(JsonElement e)
    {
        int I(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        return new ModelUsage
        {
            InputTokens = I("inputTokens"),
            OutputTokens = I("outputTokens"),
            CacheReadInputTokens = I("cacheReadInputTokens"),
            CacheCreationInputTokens = I("cacheCreationInputTokens"),
            WebSearchRequests = I("webSearchRequests"),
            CostUSD = e.TryGetProperty("costUSD", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0,
            ContextWindow = I("contextWindow"),
            MaxOutputTokens = I("maxOutputTokens"),
            CanonicalModel = Str(e, "canonicalModel"),
            Provider = Str(e, "provider"),
            ThinkingTokens = Int(e, "thinkingTokens"),
            CostBasis = Str(e, "costBasis")
        };
    }

    private static ResultMessage ParseResultMessage(JsonElement data)
    {
        try
        {
            DeferredToolUse? deferred = null;
            if (data.TryGetProperty("deferred_tool_use", out var dtu) && dtu.ValueKind == JsonValueKind.Object)
            {
                deferred = new DeferredToolUse(Req(dtu, "id"), Req(dtu, "name"), dtu.GetProperty("input").Clone());
            }

            IReadOnlyList<string>? errors = null;
            if (data.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(errs.GetArrayLength());
                foreach (var item in errs.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString()!);
                }
                errors = list;
            }

            Dictionary<string, ModelUsage>? modelUsage = null;
            if (data.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
            {
                modelUsage = new Dictionary<string, ModelUsage>();
                foreach (var entry in mu.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object)
                        modelUsage[entry.Name] = ParseModelUsage(entry.Value);
                }
            }

            return new ResultMessage
            {
                Subtype = Req(data, "subtype"),
                DurationMs = data.GetProperty("duration_ms").GetInt32(),
                DurationApiMs = data.GetProperty("duration_api_ms").GetInt32(),
                IsError = data.GetProperty("is_error").GetBoolean(),
                NumTurns = data.GetProperty("num_turns").GetInt32(),
                SessionId = Req(data, "session_id"),
                TotalCostUsd = data.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number
                    ? cost.GetDecimal()
                    : null,
                Usage = Raw(data, "usage"),
                Result = Str(data, "result"),
                StructuredOutput = Raw(data, "structured_output"),
                StopReason = Str(data, "stop_reason"),
                ModelUsage = modelUsage,
                PermissionDenials = Raw(data, "permission_denials"),
                DeferredToolUse = deferred,
                Errors = errors,
                ApiErrorStatus = Int(data, "api_error_status"),
                Uuid = Str(data, "uuid"),
                TerminalReason = Str(data, "terminal_reason"),
                Origin = ParseOrigin(data),
                QueuedTurnCount = Int(data, "queued_turn_count"),
                ResultIndex = Int(data, "result_index"),
                FastModeState = Str(data, "fast_mode_state"),
                FastModeDisabledReason = Str(data, "fast_mode_disabled_reason"),
                StartupFailureReason = Str(data, "startup_failure_reason"),
                UserMessageUuid = Str(data, "user_message_uuid"),
                UserMessageUuids = JsonRead.StrList(data, "user_message_uuids"),
                ResumeReason = Str(data, "resume_reason"),
                LocalCommand = Str(data, "local_command"),
                TtftMs = JsonRead.Dbl(data, "ttft_ms"),
                TtftStreamMs = JsonRead.Dbl(data, "ttft_stream_ms"),
                TimeToRequestMs = JsonRead.Dbl(data, "time_to_request_ms")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MessageParseException($"Missing required field in result message: {ex.Message}", data);
        }
    }

    private static StreamEvent ParseStreamEvent(JsonElement data)
    {
        try
        {
            return new StreamEvent
            {
                Uuid = Req(data, "uuid"),
                SessionId = Req(data, "session_id"),
                Event = data.GetProperty("event").Clone(),
                ParentToolUseId = Str(data, "parent_tool_use_id"),
                TtftMs = JsonRead.Dbl(data, "ttft_ms"),
                UserMessageUuid = Str(data, "user_message_uuid"),
                UserMessageUuids = JsonRead.StrList(data, "user_message_uuids"),
                ResumeReason = Str(data, "resume_reason")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new MessageParseException($"Missing required field in stream_event message: {ex.Message}", data);
        }
    }

    private static RateLimitEvent ParseRateLimitEvent(JsonElement data)
    {
        try
        {
            var info = data.GetProperty("rate_limit_info");
            // Python passes the raw status through; an unrecognized value must not
            // fail the stream (the raw payload stays on RateLimitInfo.Raw).
            var statusStr = Req(info, "status");
            var status = RateLimitEnumHelpers.ParseRateLimitStatus(statusStr) ?? RateLimitStatus.Unknown;

            var rlTypeRaw = Str(info, "rateLimitType");
            var rlType = RateLimitEnumHelpers.ParseRateLimitType(rlTypeRaw);

            var overageStr = Str(info, "overageStatus");
            RateLimitStatus? overageStatus = overageStr == null
                ? null
                : RateLimitEnumHelpers.ParseRateLimitStatus(overageStr) ?? RateLimitStatus.Unknown;

            var rli = new RateLimitInfo
            {
                Status = status,
                ResetsAt = info.TryGetProperty("resetsAt", out var ra) && ra.ValueKind == JsonValueKind.Number
                    ? ra.GetInt64() : null,
                RateLimitType = rlType,
                Utilization = info.TryGetProperty("utilization", out var ut) && ut.ValueKind == JsonValueKind.Number
                    ? ut.GetDouble() : null,
                OverageStatus = overageStatus,
                OverageResetsAt = info.TryGetProperty("overageResetsAt", out var ora) && ora.ValueKind == JsonValueKind.Number
                    ? ora.GetInt64() : null,
                OverageDisabledReason = Str(info, "overageDisabledReason"),
                Raw = info.Clone(),
                RateLimitTypeRaw = rlTypeRaw,
                IsUsingOverage = Bool(info, "isUsingOverage"),
                OverageInUse = Bool(info, "overageInUse"),
                SurpassedThreshold = JsonRead.Dbl(info, "surpassedThreshold"),
                LimitScope = Str(info, "limitScope"),
                ErrorCode = Str(info, "errorCode"),
                CanUserPurchaseCredits = Bool(info, "canUserPurchaseCredits"),
                HasChargeableSavedPaymentMethod = Bool(info, "hasChargeableSavedPaymentMethod")
            };

            return new RateLimitEvent
            {
                RateLimitInfo = rli,
                Uuid = Req(data, "uuid"),
                SessionId = Req(data, "session_id")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new MessageParseException($"Missing required field in rate_limit_event message: {ex.Message}", data);
        }
    }

    private static ConversationResetMessage ParseConversationReset(JsonElement data)
    {
        try
        {
            return new ConversationResetMessage
            {
                NewConversationId = Req(data, "new_conversation_id"),
                Uuid = Req(data, "uuid"),
                SessionId = Req(data, "session_id"),
                Trigger = Str(data, "trigger"),
                UserMessageUuid = Str(data, "user_message_uuid"),
                Timestamp = Str(data, "timestamp")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new MessageParseException($"Missing required field in conversation_reset message: {ex.Message}", data);
        }
    }
}
