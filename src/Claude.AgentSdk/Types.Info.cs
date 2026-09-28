// Claude Agent SDK for .NET — informational types from the TypeScript SDK
// (@anthropic-ai/claude-agent-sdk 0.3.283, sdk.d.ts): initialize response,
// slash commands, agents, models, account, context usage and usage reports,
// plus string-constant sets for open string unions.
//
// Every type has a lenient, reflection-free Parse(JsonElement) (missing or
// mistyped members become null/defaults; nothing throws on shape drift), and
// JsonPropertyName attributes matching the wire so source-generated metadata
// can round-trip them.

using System.Text.Json;
using System.Text.Json.Serialization;
using Claude.AgentSdk.Internal;

namespace Claude.AgentSdk;

#region Initialize response

/// <summary>A slash command. TS <c>SlashCommand</c>.</summary>
public record SlashCommand
{
    /// <summary>Command name without the leading slash.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Argument hint shown after the name (e.g. <c>"&lt;file&gt;"</c>).</summary>
    [JsonPropertyName("argumentHint")]
    public string ArgumentHint { get; init; } = string.Empty;

    [JsonPropertyName("aliases")]
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>True for commands built into Claude Code.</summary>
    [JsonPropertyName("builtin")]
    public bool? Builtin { get; init; }

    /// <summary>Parse a <c>SlashCommand</c> object; <c>null</c> if it isn't an object with a name.</summary>
    public static SlashCommand? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || JsonRead.Str(e, "name") is not { } name)
            return null;
        return new SlashCommand
        {
            Name = name,
            Description = JsonRead.Str(e, "description") ?? string.Empty,
            ArgumentHint = JsonRead.Str(e, "argumentHint") ?? string.Empty,
            Aliases = JsonRead.StrList(e, "aliases"),
            Builtin = JsonRead.Bool(e, "builtin")
        };
    }
}

/// <summary>A sub-agent available to the session. TS <c>AgentInfo</c>.</summary>
public record AgentInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Parse an <c>AgentInfo</c> object; <c>null</c> if it isn't an object with a name.</summary>
    public static AgentInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || JsonRead.Str(e, "name") is not { } name)
            return null;
        return new AgentInfo
        {
            Name = name,
            Description = JsonRead.Str(e, "description") ?? string.Empty,
            Model = JsonRead.Str(e, "model")
        };
    }
}

/// <summary>A model the session can use. TS <c>ModelInfo</c>.</summary>
public record ModelInfo
{
    /// <summary>Value to pass as the model (alias or id).</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("resolvedModel")]
    public string? ResolvedModel { get; init; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("supportsEffort")]
    public bool? SupportsEffort { get; init; }

    /// <summary>Effort wire values (<c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>).</summary>
    [JsonPropertyName("supportedEffortLevels")]
    public IReadOnlyList<string>? SupportedEffortLevels { get; init; }

    [JsonPropertyName("supportsAdaptiveThinking")]
    public bool? SupportsAdaptiveThinking { get; init; }

    [JsonPropertyName("supportsFastMode")]
    public bool? SupportsFastMode { get; init; }

    [JsonPropertyName("supportsAutoMode")]
    public bool? SupportsAutoMode { get; init; }

    /// <summary>Parse a <c>ModelInfo</c> object; <c>null</c> if it isn't an object with a value.</summary>
    public static ModelInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || JsonRead.Str(e, "value") is not { } value)
            return null;
        return new ModelInfo
        {
            Value = value,
            ResolvedModel = JsonRead.Str(e, "resolvedModel"),
            DisplayName = JsonRead.Str(e, "displayName") ?? string.Empty,
            Description = JsonRead.Str(e, "description") ?? string.Empty,
            SupportsEffort = JsonRead.Bool(e, "supportsEffort"),
            SupportedEffortLevels = JsonRead.StrList(e, "supportedEffortLevels"),
            SupportsAdaptiveThinking = JsonRead.Bool(e, "supportsAdaptiveThinking"),
            SupportsFastMode = JsonRead.Bool(e, "supportsFastMode"),
            SupportsAutoMode = JsonRead.Bool(e, "supportsAutoMode")
        };
    }
}

/// <summary>The signed-in account. TS <c>AccountInfo</c>.</summary>
public record AccountInfo
{
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; init; }

    [JsonPropertyName("tokenSource")]
    public string? TokenSource { get; init; }

    /// <summary>See <see cref="ApiKeySources"/>.</summary>
    [JsonPropertyName("apiKeySource")]
    public string? ApiKeySource { get; init; }

    /// <summary>
    /// <c>firstParty</c>, <c>bedrock</c>, <c>vertex</c>, <c>foundry</c>,
    /// <c>anthropicAws</c>, <c>anthropicGoogleCloud</c>, <c>mantle</c> or <c>gateway</c>.
    /// </summary>
    [JsonPropertyName("apiProvider")]
    public string? ApiProvider { get; init; }

    /// <summary>Parse an <c>AccountInfo</c> object; <c>null</c> if it isn't an object.</summary>
    public static AccountInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        return new AccountInfo
        {
            Email = JsonRead.Str(e, "email"),
            Organization = JsonRead.Str(e, "organization"),
            SubscriptionType = JsonRead.Str(e, "subscriptionType"),
            TokenSource = JsonRead.Str(e, "tokenSource"),
            ApiKeySource = JsonRead.Str(e, "apiKeySource"),
            ApiProvider = JsonRead.Str(e, "apiProvider")
        };
    }
}

/// <summary>
/// Typed response to the <c>initialize</c> control request. TS
/// <c>SDKControlInitializeResponse</c>. Build it from the raw server-info
/// element with <see cref="Parse"/>.
/// </summary>
public record InitializeResponse
{
    [JsonPropertyName("commands")]
    public IReadOnlyList<SlashCommand> Commands { get; init; } = [];

    [JsonPropertyName("agents")]
    public IReadOnlyList<AgentInfo> Agents { get; init; } = [];

    [JsonPropertyName("output_style")]
    public string? OutputStyle { get; init; }

    [JsonPropertyName("available_output_styles")]
    public IReadOnlyList<string> AvailableOutputStyles { get; init; } = [];

    [JsonPropertyName("models")]
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];

    [JsonPropertyName("account")]
    public AccountInfo? Account { get; init; }

    [JsonPropertyName("hooks_applied")]
    public bool? HooksApplied { get; init; }

    [JsonPropertyName("plugins_applied")]
    public bool? PluginsApplied { get; init; }

    /// <summary>See <see cref="FastModeStates"/>.</summary>
    [JsonPropertyName("fast_mode_state")]
    public string? FastModeState { get; init; }

    /// <summary>See <see cref="FastModeDisabledReasons"/>.</summary>
    [JsonPropertyName("fast_mode_disabled_reason")]
    public string? FastModeDisabledReason { get; init; }

    /// <summary>The response object as received (for fields this SDK doesn't model).</summary>
    [JsonIgnore]
    public JsonElement Raw { get; init; }

    /// <summary>
    /// Parse the initialize response. Lenient: unrecognized or malformed
    /// entries are skipped rather than failing. A non-object yields an empty
    /// response (with <see cref="Raw"/> set).
    /// </summary>
    public static InitializeResponse Parse(JsonElement e)
    {
        var raw = e.ValueKind == JsonValueKind.Undefined ? default : e.Clone();
        if (e.ValueKind != JsonValueKind.Object)
            return new InitializeResponse { Raw = raw };
        return new InitializeResponse
        {
            Commands = JsonRead.ObjList(e, "commands", SlashCommand.Parse),
            Agents = JsonRead.ObjList(e, "agents", AgentInfo.Parse),
            OutputStyle = JsonRead.Str(e, "output_style"),
            AvailableOutputStyles = JsonRead.StrList(e, "available_output_styles") ?? [],
            Models = JsonRead.ObjList(e, "models", ModelInfo.Parse),
            Account = e.TryGetProperty("account", out var a) ? AccountInfo.Parse(a) : null,
            HooksApplied = JsonRead.Bool(e, "hooks_applied"),
            PluginsApplied = JsonRead.Bool(e, "plugins_applied"),
            FastModeState = JsonRead.Str(e, "fast_mode_state"),
            FastModeDisabledReason = JsonRead.Str(e, "fast_mode_disabled_reason"),
            Raw = raw
        };
    }
}

#endregion

#region Context usage / usage report

/// <summary>
/// Context-window usage snapshot attached to assistant messages. TS
/// <c>SDKContextUsage</c> (snake_case; distinct from the camelCase
/// <see cref="ContextUsageResponse"/> returned by <c>GetContextUsage</c>).
/// </summary>
public record SdkContextUsage
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("raw_max_tokens")]
    public long RawMaxTokens { get; init; }

    [JsonPropertyName("percentage")]
    public double Percentage { get; init; }

    [JsonPropertyName("over_limit")]
    public SdkContextOverLimit? OverLimit { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<SdkContextUsageCategory> Categories { get; init; } = [];

    [JsonPropertyName("mcp_tools")]
    public IReadOnlyList<SdkContextMcpTool> McpTools { get; init; } = [];

    [JsonPropertyName("memory_files")]
    public IReadOnlyList<SdkContextMemoryFile> MemoryFiles { get; init; } = [];

    [JsonPropertyName("agents")]
    public IReadOnlyList<SdkContextAgent> Agents { get; init; } = [];

    [JsonPropertyName("skills")]
    public IReadOnlyList<SdkContextSkill>? Skills { get; init; }

    /// <summary>Parse an <c>SDKContextUsage</c> object; <c>null</c> if it isn't an object.</summary>
    public static SdkContextUsage? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        SdkContextOverLimit? over = null;
        if (e.TryGetProperty("over_limit", out var o) && o.ValueKind == JsonValueKind.Object)
            over = new SdkContextOverLimit(JsonRead.Long(o, "tokens_over") ?? 0, JsonRead.Str(o, "kind") ?? string.Empty);
        return new SdkContextUsage
        {
            Model = JsonRead.Str(e, "model"),
            TotalTokens = JsonRead.Long(e, "total_tokens") ?? 0,
            RawMaxTokens = JsonRead.Long(e, "raw_max_tokens") ?? 0,
            Percentage = JsonRead.Dbl(e, "percentage") ?? 0,
            OverLimit = over,
            Categories = JsonRead.ObjList(e, "categories", c => new SdkContextUsageCategory(
                JsonRead.Str(c, "name") ?? string.Empty, JsonRead.Long(c, "tokens") ?? 0, JsonRead.Str(c, "kind") ?? string.Empty)),
            McpTools = JsonRead.ObjList(e, "mcp_tools", c => new SdkContextMcpTool(
                JsonRead.Str(c, "name") ?? string.Empty, JsonRead.Str(c, "server_name") ?? string.Empty, JsonRead.Long(c, "tokens") ?? 0)),
            MemoryFiles = JsonRead.ObjList(e, "memory_files", c => new SdkContextMemoryFile(
                JsonRead.Str(c, "path") ?? string.Empty, JsonRead.Str(c, "type") ?? string.Empty, JsonRead.Long(c, "tokens") ?? 0)),
            Agents = JsonRead.ObjList(e, "agents", c => new SdkContextAgent(
                JsonRead.Str(c, "agent_type") ?? string.Empty, JsonRead.Str(c, "source") ?? string.Empty, JsonRead.Long(c, "tokens") ?? 0)),
            Skills = e.TryGetProperty("skills", out var s) && s.ValueKind == JsonValueKind.Array
                ? JsonRead.ObjList(e, "skills", c => new SdkContextSkill(
                    JsonRead.Str(c, "name") ?? string.Empty, JsonRead.Str(c, "source") ?? string.Empty,
                    JsonRead.Long(c, "tokens") ?? 0, JsonRead.Str(c, "plugin_name")))
                : null
        };
    }
}

/// <summary>How far over a limit the context is. <see cref="Kind"/> is <c>hard_limit</c> or <c>compaction_window</c>.</summary>
public record SdkContextOverLimit(
    [property: JsonPropertyName("tokens_over")] long TokensOver,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>A context category. <see cref="Kind"/> is <c>used</c>, <c>free</c>, <c>buffer</c> or <c>deferred</c>.</summary>
public record SdkContextUsageCategory(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tokens")] long Tokens,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>Tokens used by one MCP tool definition.</summary>
public record SdkContextMcpTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("server_name")] string ServerName,
    [property: JsonPropertyName("tokens")] long Tokens);

/// <summary>Tokens used by one memory file.</summary>
public record SdkContextMemoryFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("tokens")] long Tokens);

/// <summary>Tokens used by one agent definition.</summary>
public record SdkContextAgent(
    [property: JsonPropertyName("agent_type")] string AgentType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("tokens")] long Tokens);

/// <summary>Tokens used by one skill.</summary>
public record SdkContextSkill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("tokens")] long Tokens,
    [property: JsonPropertyName("plugin_name")] string? PluginName = null);

/// <summary>Session cost and rate-limit report. TS <c>SDKUsageReport</c>.</summary>
public record SdkUsageReport
{
    [JsonPropertyName("session")]
    public SdkUsageSession? Session { get; init; }

    /// <summary>Rate-limit report; <c>null</c> when unavailable.</summary>
    [JsonPropertyName("rate_limits")]
    public SdkUsageRateLimits? RateLimits { get; init; }

    /// <summary>Parse an <c>SDKUsageReport</c> object; <c>null</c> if it isn't an object.</summary>
    public static SdkUsageReport? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;

        SdkUsageSession? session = null;
        if (e.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            var mu = new Dictionary<string, ModelUsage>();
            if (s.TryGetProperty("model_usage", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in m.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Object)
                        mu[p.Name] = MessageParser.ParseModelUsage(p.Value);
                }
            }
            session = new SdkUsageSession
            {
                TotalCostUsd = JsonRead.Dbl(s, "total_cost_usd") ?? 0,
                TotalApiDurationMs = JsonRead.Dbl(s, "total_api_duration_ms") ?? 0,
                TotalDurationMs = JsonRead.Dbl(s, "total_duration_ms") ?? 0,
                TotalLinesAdded = JsonRead.Long(s, "total_lines_added") ?? 0,
                TotalLinesRemoved = JsonRead.Long(s, "total_lines_removed") ?? 0,
                ModelUsage = mu
            };
        }

        SdkUsageRateLimits? rateLimits = null;
        if (e.TryGetProperty("rate_limits", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            IReadOnlyList<SdkUsageRateLimit>? limits = null;
            if (r.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                limits = JsonRead.ObjList(r, "limits", x => new SdkUsageRateLimit
                {
                    Kind = JsonRead.Str(x, "kind") ?? string.Empty,
                    Group = JsonRead.Str(x, "group") ?? string.Empty,
                    Percent = JsonRead.Dbl(x, "percent") ?? 0,
                    ResetsAt = JsonRead.Str(x, "resets_at"),
                    ScopeModelDisplayName = JsonRead.Path(x, "scope", "model", "display_name"),
                    ScopeSurfaceDisplayName = JsonRead.Path(x, "scope", "surface", "display_name"),
                    Severity = JsonRead.Str(x, "severity") ?? string.Empty,
                    IsActive = JsonRead.Bool(x, "is_active") ?? false
                });
            }
            SdkUsageExtraUsage? extra = null;
            if (r.TryGetProperty("extra_usage", out var x2) && x2.ValueKind == JsonValueKind.Object)
            {
                extra = new SdkUsageExtraUsage
                {
                    IsEnabled = JsonRead.Bool(x2, "is_enabled") ?? false,
                    MonthlyLimit = JsonRead.Dbl(x2, "monthly_limit"),
                    UsedCredits = JsonRead.Dbl(x2, "used_credits"),
                    Utilization = JsonRead.Dbl(x2, "utilization"),
                    Currency = JsonRead.Str(x2, "currency")
                };
            }
            rateLimits = new SdkUsageRateLimits { Limits = limits, ExtraUsage = extra };
        }

        return new SdkUsageReport { Session = session, RateLimits = rateLimits };
    }
}

/// <summary>Session totals in an <see cref="SdkUsageReport"/>.</summary>
public record SdkUsageSession
{
    [JsonPropertyName("total_cost_usd")]
    public double TotalCostUsd { get; init; }

    [JsonPropertyName("total_api_duration_ms")]
    public double TotalApiDurationMs { get; init; }

    [JsonPropertyName("total_duration_ms")]
    public double TotalDurationMs { get; init; }

    [JsonPropertyName("total_lines_added")]
    public long TotalLinesAdded { get; init; }

    [JsonPropertyName("total_lines_removed")]
    public long TotalLinesRemoved { get; init; }

    [JsonPropertyName("model_usage")]
    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } = new Dictionary<string, ModelUsage>();
}

/// <summary>Rate-limit section of an <see cref="SdkUsageReport"/>.</summary>
public record SdkUsageRateLimits
{
    /// <summary>Individual limits; <c>null</c> when not reported.</summary>
    [JsonPropertyName("limits")]
    public IReadOnlyList<SdkUsageRateLimit>? Limits { get; init; }

    [JsonPropertyName("extra_usage")]
    public SdkUsageExtraUsage? ExtraUsage { get; init; }
}

/// <summary>One rate limit in an <see cref="SdkUsageReport"/>.</summary>
public record SdkUsageRateLimit
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public string Group { get; init; } = string.Empty;

    [JsonPropertyName("percent")]
    public double Percent { get; init; }

    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; init; }

    /// <summary><c>scope.model.display_name</c>, when the limit is model-scoped.</summary>
    [JsonPropertyName("scope_model_display_name")]
    public string? ScopeModelDisplayName { get; init; }

    /// <summary><c>scope.surface.display_name</c>, when the limit is surface-scoped.</summary>
    [JsonPropertyName("scope_surface_display_name")]
    public string? ScopeSurfaceDisplayName { get; init; }

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }
}

/// <summary>Extra-usage (pay-as-you-go) state in an <see cref="SdkUsageReport"/>.</summary>
public record SdkUsageExtraUsage
{
    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; init; }

    [JsonPropertyName("monthly_limit")]
    public double? MonthlyLimit { get; init; }

    [JsonPropertyName("used_credits")]
    public double? UsedCredits { get; init; }

    [JsonPropertyName("utilization")]
    public double? Utilization { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

#endregion

#region String-constant sets (open string unions)

/// <summary>Known <see cref="ResultMessage.TerminalReason"/> values. TS <c>TerminalReason</c>.</summary>
public static class TerminalReasons
{
    public const string BlockingLimit = "blocking_limit";
    public const string RapidRefillBreaker = "rapid_refill_breaker";
    public const string PromptTooLong = "prompt_too_long";
    public const string ImageError = "image_error";
    public const string ModelError = "model_error";
    public const string ApiError = "api_error";
    public const string MalformedToolUseExhausted = "malformed_tool_use_exhausted";
    public const string AbortedStreaming = "aborted_streaming";
    public const string AbortedTools = "aborted_tools";
    public const string StopHookPrevented = "stop_hook_prevented";
    public const string HookStopped = "hook_stopped";
    public const string ToolDeferred = "tool_deferred";
    public const string MaxTurns = "max_turns";
    public const string BackgroundRequested = "background_requested";
    public const string Completed = "completed";
    public const string BudgetExhausted = "budget_exhausted";
    public const string StructuredOutputRetryExhausted = "structured_output_retry_exhausted";
    public const string ToolDeferredUnavailable = "tool_deferred_unavailable";
    public const string TurnSetupFailed = "turn_setup_failed";
}

/// <summary>Known fast-mode states. TS <c>FastModeState</c>.</summary>
public static class FastModeStates
{
    public const string Off = "off";
    public const string Cooldown = "cooldown";
    public const string On = "on";
}

/// <summary>Known reasons fast mode is disabled. TS <c>FastModeDisabledReason</c>.</summary>
public static class FastModeDisabledReasons
{
    public const string Free = "free";
    public const string Preference = "preference";
    public const string ExtraUsageDisabled = "extra_usage_disabled";
    public const string NetworkError = "network_error";
    public const string Unknown = "unknown";
    public const string NotFirstParty = "not_first_party";
    public const string DisabledByEnv = "disabled_by_env";
    public const string ModelNotAllowed = "model_not_allowed";
    public const string SdkOptInRequired = "sdk_opt_in_required";
    public const string Pending = "pending";
}

/// <summary>Known API key sources. TS <c>ApiKeySource</c>.</summary>
public static class ApiKeySources
{
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string ApiKeyHelper = "apiKeyHelper";
    public const string LoginManagedKey = "/login managed key";
    public const string None = "none";
    public const string User = "user";
    public const string Project = "project";
    public const string Org = "org";
    public const string Temporary = "temporary";
    public const string OAuth = "oauth";
}

/// <summary>Known <see cref="ResultMessage.StartupFailureReason"/> values. TS <c>SDKStartupFailureReason</c>.</summary>
public static class StartupFailureReasons
{
    public const string OrgPinApiKeyConflict = "org_pin_api_key_conflict";
    public const string OrgVerifyFailed = "org_verify_failed";
    public const string OrgPinMismatch = "org_pin_mismatch";
    public const string ManagedSettingsInvalid = "managed_settings_invalid";
    public const string RemoteSettingsRequiredUnavailable = "remote_settings_required_unavailable";
    public const string GatewaySigninRequired = "gateway_signin_required";
    public const string GatewayAccessDenied = "gateway_access_denied";
    public const string ProxyInvalid = "proxy_invalid";
    public const string TempDirUnusable = "temp_dir_unusable";
    public const string CwdUnavailable = "cwd_unavailable";
    public const string ShellToolMissing = "shell_tool_missing";
    public const string SessionHeldByBackground = "session_held_by_background";
    public const string WorktreeResumeRefused = "worktree_resume_refused";
    public const string WorktreeUnverified = "worktree_unverified";
    public const string CliVersionTooOld = "cli_version_too_old";
    public const string BypassRoot = "bypass_root";
}

/// <summary>Known <see cref="StatusMessage.Status"/> values. TS <c>SDKStatus</c> (also <c>null</c>).</summary>
public static class SdkStatuses
{
    public const string Compacting = "compacting";
    public const string Requesting = "requesting";
}

#endregion
