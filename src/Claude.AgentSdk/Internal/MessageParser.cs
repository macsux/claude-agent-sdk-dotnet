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
    /// Parse message from CLI output into typed Message objects.
    /// Returns <c>null</c> for unknown top-level message types so newer CLI
    /// versions don't break older SDK versions. Python commit 146e3d6.
    /// </summary>
    /// <param name="data">Raw message JSON from CLI output.</param>
    /// <returns>Parsed Message object, or <c>null</c> if the message type is unknown.</returns>
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

        // Hook events arrive as system messages with subtype hook_started/hook_response.
        // Python commit c1182a4.
        if (Str(data, "type") == "system" &&
            Str(data, "subtype") is "hook_started" or "hook_response")
        {
            var hookEventName =
                NonEmpty(Str(data, "hook_event"))
                ?? NonEmpty(Str(data, "hook_name"))
                ?? NonEmpty(Str(data, "hook_event_name"))
                ?? string.Empty;
            return new HookEventMessage
            {
                Subtype = Str(data, "subtype")!,
                Data = data.Clone(),
                HookEventName = hookEventName,
                SessionId = Str(data, "session_id"),
                Uuid = Str(data, "uuid")
            };
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
            _ => LogAndSkipUnknown(messageType)
        };
    }

    private static Message? LogAndSkipUnknown(string? messageType)
    {
        Debug.WriteLine($"[MessageParser] Skipping unknown message type: {messageType}");
        return null;
    }

    /// <summary>
    /// Legacy entry point: parse message and throw on unknown types.
    /// Prefer <see cref="ParseOrNull"/>, which mirrors Python's forward-compatible
    /// behavior of skipping unknown types.
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
                Origin = ParseOrigin(data)
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
                ContentBlock? contentBlock = Req(block, "type") switch
                {
                    "text" => new TextBlock(Req(block, "text")),
                    "thinking" => new ThinkingBlock(Req(block, "thinking"), Req(block, "signature")),
                    "tool_use" => new ToolUseBlock(Req(block, "id"), Req(block, "name"), block.GetProperty("input").Clone()),
                    "tool_result" => new ToolResultBlock(Req(block, "tool_use_id"), Raw(block, "content"), Bool(block, "is_error")),
                    // Python commit 6ab97b4: server_tool_use / advisor_tool_result.
                    "server_tool_use" => new ServerToolUseBlock(Req(block, "id"), Req(block, "name"), block.GetProperty("input").Clone()),
                    "advisor_tool_result" => new ServerToolResultBlock(Req(block, "tool_use_id"), block.GetProperty("content").Clone()),
                    // Python skips block types it doesn't know (forward compatibility
                    // with newer CLIs) instead of failing the whole message.
                    _ => null
                };
                if (contentBlock != null)
                    contentBlocks.Add(contentBlock);
            }

            // Python reads `error` from the top-level frame (data.get("error")),
            // not from the inner API message. The inner location is kept as a
            // fallback for frames produced by older SDK builds.
            var errorStr = Str(data, "error") ?? Str(message, "error");
            AssistantMessageError? error = errorStr switch
            {
                null => null,
                "authentication_failed" => AssistantMessageError.AuthenticationFailed,
                "billing_error" => AssistantMessageError.BillingError,
                "rate_limit" => AssistantMessageError.RateLimit,
                "invalid_request" => AssistantMessageError.InvalidRequest,
                "server_error" => AssistantMessageError.ServerError,
                _ => AssistantMessageError.Unknown
            };

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
                Uuid = Str(data, "uuid")
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
                        TaskType = Str(data, "task_type")
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
                        LastToolName = Str(data, "last_tool_name")
                    };
                case "task_notification":
                    var status = Req(data, "status") switch
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
                            : null
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
                        Uuid = Str(data, "uuid")
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
                    return new SystemMessage
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

    private static ModelUsage ParseModelUsage(JsonElement e)
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
            Provider = Str(e, "provider")
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
                Origin = ParseOrigin(data)
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
                ParentToolUseId = Str(data, "parent_tool_use_id")
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

            RateLimitType? rlType = Str(info, "rateLimitType") switch
            {
                "five_hour" => RateLimitType.FiveHour,
                "seven_day" => RateLimitType.SevenDay,
                "seven_day_opus" => RateLimitType.SevenDayOpus,
                "seven_day_sonnet" => RateLimitType.SevenDaySonnet,
                "overage" => RateLimitType.Overage,
                _ => null
            };

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
                Raw = info.Clone()
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
                SessionId = Req(data, "session_id")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new MessageParseException($"Missing required field in conversation_reset message: {ex.Message}", data);
        }
    }
}
