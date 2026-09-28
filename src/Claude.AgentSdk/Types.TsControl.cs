// Claude Agent SDK for .NET — TypeScript SDK parity for the control protocol:
// result records for the TS-only Query methods (interrupt receipt, rewind
// preview, read_file, reload_*, mcp_read_resource, mcp_set_servers, get_usage,
// side_question) and the SDK-side callbacks the CLI can invoke (elicitation,
// user dialogs, OAuth / host-auth token refresh).
// Reference: @anthropic-ai/claude-agent-sdk 0.3.283 sdk.d.ts / sdk.mjs.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Claude.AgentSdk;

/// <summary>Diagnostic ids for <see cref="ExperimentalAttribute"/> APIs.</summary>
public static class ExperimentalDiagnostics
{
    /// <summary>
    /// TS marks the API as unstable (<c>usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET</c>).
    /// </summary>
    public const string UnstableControlApi = "CLAUDESDK001";

    /// <summary>
    /// Undocumented control subtypes the TS SDK sends for claude.ai / IDE hosts;
    /// their wire shapes can change without notice.
    /// </summary>
    public const string InternalControlApi = "CLAUDESDK002";
}

#region Outbound control results

/// <summary>
/// The CLI's interrupt receipt (<c>interrupt_receipt_v1</c> capability). TS:
/// <c>SDKControlInterruptResponse</c>.
/// </summary>
/// <param name="StillQueued">Uuids of async user messages that will still run unless cancelled.</param>
/// <param name="Cancelled">With <c>cancelQueued</c>: uuids this interrupt cancelled.</param>
public sealed record InterruptReceipt(IReadOnlyList<string> StillQueued, IReadOnlyList<string>? Cancelled);

/// <summary>Result of <c>rewind_files</c>. TS: <c>RewindFilesResult</c>.</summary>
public sealed record RewindFilesResult
{
    /// <summary>Whether the rewind can be (or was) applied.</summary>
    public bool CanRewind { get; init; }

    /// <summary>Why the rewind cannot be applied.</summary>
    public string? Error { get; init; }

    /// <summary>Files the rewind changes.</summary>
    public IReadOnlyList<string>? FilesChanged { get; init; }

    /// <summary>Lines added by the rewind.</summary>
    public int? Insertions { get; init; }

    /// <summary>Lines removed by the rewind.</summary>
    public int? Deletions { get; init; }

    /// <summary>Tracked files skipped for link safety (real rewinds only).</summary>
    public int? SkippedLinks { get; init; }

    /// <summary>The raw response payload.</summary>
    public JsonElement Raw { get; init; }
}

/// <summary>How <c>get_context_usage</c> counts tokens. TS: <c>detail</c>.</summary>
public enum ContextUsageDetail
{
    /// <summary>Answer from the last response's usage and local estimates.</summary>
    Summary,

    /// <summary>Count each category with the token-count API (the CLI default).</summary>
    Full
}

/// <summary>Thinking display mode for <c>set_max_thinking_tokens</c>.</summary>
public enum ThinkingDisplayMode
{
    /// <summary><c>summarized</c></summary>
    Summarized,

    /// <summary><c>omitted</c></summary>
    Omitted,

    /// <summary><c>highlights</c> (Anthropic-hosted remote sessions only).</summary>
    Highlights
}

/// <summary>Settings file written by <c>update_settings</c>.</summary>
public enum SettingsFileSource
{
    /// <summary><c>localSettings</c> (<c>.claude/settings.local.json</c>).</summary>
    LocalSettings,

    /// <summary><c>userSettings</c> (<c>~/.claude/settings.json</c>).</summary>
    UserSettings
}

/// <summary>Per-MCP-server permission-mode override (tighten-only).</summary>
public enum McpPermissionModeOverride
{
    /// <summary>Force per-action prompts.</summary>
    Default,

    /// <summary>Route through the auto-mode classifier.</summary>
    Auto
}

/// <summary>Encoding for <c>read_file</c>.</summary>
public enum ReadFileEncoding
{
    /// <summary><c>utf-8</c> (default).</summary>
    Utf8,

    /// <summary><c>base64</c>, for binary files.</summary>
    Base64
}

/// <summary>Result of <c>read_file</c>. TS: <c>SDKControlReadFileResponse</c>.</summary>
/// <param name="Contents">File contents (base64 when <see cref="Encoding"/> is <c>base64</c>).</param>
/// <param name="AbsPath">The resolved absolute path.</param>
/// <param name="Truncated">Whether <c>max_bytes</c> cut the contents.</param>
/// <param name="Encoding"><c>base64</c> when requested; absent means utf-8.</param>
public sealed record ReadFileResult(string Contents, string AbsPath, bool? Truncated, string? Encoding);

/// <summary>A plugin reported by <c>reload_plugins</c>.</summary>
public sealed record ReloadedPlugin(string Name, string Path, string? Source, string? Version);

/// <summary>What applying a held plugin reload would change.</summary>
/// <param name="McpServersAdded">Plugin MCP servers the reload would register.</param>
/// <param name="McpServersRemoved">Plugin MCP servers the reload would drop.</param>
/// <param name="LspToolChange"><c>adds</c>, <c>may-add</c>, <c>removes</c>, <c>may-remove</c> or null.</param>
public sealed record PluginCacheImpact(
    IReadOnlyList<string> McpServersAdded,
    IReadOnlyList<string> McpServersRemoved,
    string? LspToolChange);

/// <summary>Result of <c>reload_plugins</c>. TS: <c>SDKControlReloadPluginsResponse</c>.</summary>
public sealed record ReloadPluginsResult
{
    /// <summary>Refreshed commands.</summary>
    public IReadOnlyList<SlashCommand> Commands { get; init; } = [];

    /// <summary>Refreshed agents.</summary>
    public IReadOnlyList<AgentInfo> Agents { get; init; } = [];

    /// <summary>Loaded plugins.</summary>
    public IReadOnlyList<ReloadedPlugin> Plugins { get; init; } = [];

    /// <summary>MCP server status entries (raw; see <see cref="McpServerStatus"/>).</summary>
    public IReadOnlyList<JsonElement> McpServers { get; init; } = [];

    /// <summary>Number of plugin load errors.</summary>
    public int ErrorCount { get; init; }

    /// <summary>With <c>holdOnCacheImpact</c>: whether the reload was held (not applied).</summary>
    public bool? Held { get; init; }

    /// <summary>With <see cref="Held"/> true: what applying would change.</summary>
    public PluginCacheImpact? CacheImpact { get; init; }

    /// <summary>The raw response payload.</summary>
    public JsonElement Raw { get; init; }
}

/// <summary>One content item of <c>mcp_read_resource</c>.</summary>
/// <param name="Uri">Resource URI.</param>
/// <param name="MimeType">MIME type.</param>
/// <param name="Text">Text contents.</param>
/// <param name="Blob">Base64 contents, for a binary item.</param>
/// <param name="Meta">The item's <c>_meta</c> (untrusted, server-supplied).</param>
public sealed record McpReadResourceContent(string Uri, string? MimeType, string? Text, string? Blob, JsonElement? Meta);

/// <summary>Result of <c>mcp_read_resource</c>. TS: <c>SDKControlMcpReadResourceResponse</c>.</summary>
public sealed record McpReadResourceResult(IReadOnlyList<McpReadResourceContent> Contents);

/// <summary>Result of <c>mcp_set_servers</c>. TS: <c>McpSetServersResult</c>.</summary>
/// <param name="Added">Names of servers that were added.</param>
/// <param name="Removed">Names of servers that were removed.</param>
/// <param name="Errors">Server name → connection error.</param>
public sealed record McpSetServersResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Structured data behind <c>/usage</c>. TS: <c>SDKControlGetUsageResponse</c>
/// (unstable; nested sections are kept as raw JSON).
/// </summary>
[Experimental(ExperimentalDiagnostics.UnstableControlApi)]
public sealed record UsageReport
{
    /// <summary>Session cost and token totals (<c>session</c>).</summary>
    public JsonElement? Session { get; init; }

    /// <summary>claude.ai subscription type, or null for API key / 3P sessions.</summary>
    public string? SubscriptionType { get; init; }

    /// <summary>False when plan rate limits do not apply.</summary>
    public bool RateLimitsAvailable { get; init; }

    /// <summary>Plan rate-limit windows (<c>rate_limits</c>), or null.</summary>
    public JsonElement? RateLimits { get; init; }

    /// <summary>Local transcript behaviors (<c>behaviors</c>), null when skipped.</summary>
    public JsonElement? Behaviors { get; init; }

    /// <summary>The raw response payload.</summary>
    public JsonElement Raw { get; init; }
}

/// <summary>A refusal fallback reported by <c>side_question</c>.</summary>
public sealed record SideQuestionRefusalFallback(string? OriginalModel, string? FallbackModel, JsonElement? Content);

/// <summary>Answer to <c>side_question</c>.</summary>
/// <param name="Response">The answer text.</param>
/// <param name="Synthetic">Whether the answer was synthesized rather than model-generated.</param>
/// <param name="RefusalFallback">Set when the question was answered by a fallback model.</param>
[Experimental(ExperimentalDiagnostics.InternalControlApi)]
public sealed record SideQuestionResult(string Response, bool Synthetic, SideQuestionRefusalFallback? RefusalFallback);

#endregion

#region Inbound callbacks (CLI -> SDK)

/// <summary>
/// Identifies an inbound control request. A response sent out-of-band (after the
/// callback returned null) must echo <see cref="RequestId"/>.
/// </summary>
public sealed record ControlRequestContext(string RequestId);

/// <summary>
/// An MCP elicitation the CLI forwards to the host. TS: <c>ElicitationRequest</c>.
/// </summary>
/// <param name="ServerName">MCP server requesting input (<c>mcp_server_name</c>).</param>
/// <param name="Message">Message to show the user.</param>
/// <param name="Mode"><c>form</c> or <c>url</c>.</param>
/// <param name="Url">URL to open (url mode).</param>
/// <param name="ElicitationId">Correlates url-mode elicitations with completion notifications.</param>
/// <param name="RequestedSchema">JSON Schema for the input (form mode).</param>
/// <param name="Title">Permission-display title.</param>
/// <param name="DisplayName">Short tool/server label.</param>
/// <param name="Description">Permission-display subtitle.</param>
public sealed record ElicitationRequest(
    string ServerName,
    string Message,
    string? Mode,
    string? Url,
    string? ElicitationId,
    JsonElement? RequestedSchema,
    string? Title,
    string? DisplayName,
    string? Description);

/// <summary>The host's answer to an elicitation.</summary>
public enum ElicitationAction
{
    /// <summary><c>accept</c> (with <see cref="ElicitationResult.Content"/> for forms).</summary>
    Accept,

    /// <summary><c>decline</c> (the default when no callback is set).</summary>
    Decline,

    /// <summary><c>cancel</c></summary>
    Cancel
}

/// <summary>Answer to an elicitation. TS: MCP <c>ElicitResult</c>.</summary>
/// <param name="Action">accept / decline / cancel.</param>
/// <param name="Content">Form values when accepting a form elicitation.</param>
public sealed record ElicitationResult(ElicitationAction Action, JsonElement? Content = null);

/// <summary>
/// Handles an MCP elicitation. Return null only after answering out-of-band
/// (echoing <see cref="ControlRequestContext.RequestId"/>); the SDK then writes nothing.
/// </summary>
public delegate Task<ElicitationResult?> ElicitationCallback(
    ElicitationRequest request,
    ControlRequestContext context,
    CancellationToken cancellationToken);

/// <summary>A <c>request_user_dialog</c> from the CLI. TS: <c>UserDialogRequest</c>.</summary>
/// <param name="DialogKind">Dialog kind; answer unrecognized kinds with <see cref="UserDialogResult.Cancelled"/>.</param>
/// <param name="Payload">Dialog-specific data.</param>
/// <param name="ToolUseId">Tool invocation the dialog belongs to, if any.</param>
public sealed record UserDialogRequest(string DialogKind, JsonElement Payload, string? ToolUseId);

/// <summary>The host's answer to a user dialog. TS: <c>UserDialogResult</c>.</summary>
public abstract record UserDialogResult
{
    private protected UserDialogResult() { }

    /// <summary><c>{"behavior":"completed","result":...}</c></summary>
    public sealed record Completed(JsonElement Result) : UserDialogResult;

    /// <summary><c>{"behavior":"cancelled"}</c>: the CLI applies the dialog's default.</summary>
    public sealed record Cancelled : UserDialogResult;

    /// <summary>A completed dialog with the given result.</summary>
    public static UserDialogResult Complete(JsonElement result) => new Completed(result);

    /// <summary>A cancelled dialog.</summary>
    public static UserDialogResult Cancel() => new Cancelled();
}

/// <summary>
/// Renders a dialog the CLI asks for. Return null only after answering
/// out-of-band; the SDK then writes nothing.
/// </summary>
public delegate Task<UserDialogResult?> UserDialogCallback(
    UserDialogRequest request,
    ControlRequestContext context,
    CancellationToken cancellationToken);

/// <summary>Why an OAuth refresh was declined (TS <c>onDecline</c> reasons).</summary>
public static class OAuthDeclineReason
{
    /// <summary>The user signed out.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>The signed-in identity changed.</summary>
    public const string IdentityChanged = "identity_changed";

    /// <summary>A transient failure; the CLI may retry.</summary>
    public const string Transient = "transient";

    /// <summary>The refresh itself failed.</summary>
    public const string RefreshFailed = "refresh_failed";

    internal static bool IsKnown(string? reason) =>
        reason is SignedOut or IdentityChanged or Transient or RefreshFailed;
}

/// <summary>Answer to <c>oauth_token_refresh</c>.</summary>
/// <param name="AccessToken">The fresh access token, or null when declining.</param>
/// <param name="DeclineReason">
/// With a null token: one of <see cref="OAuthDeclineReason"/>; unknown values are dropped.
/// </param>
public sealed record OAuthTokenResult(string? AccessToken, string? DeclineReason = null)
{
    /// <summary>A fresh token.</summary>
    public static OAuthTokenResult Token(string accessToken) => new(accessToken);

    /// <summary>No token, with a reason.</summary>
    public static OAuthTokenResult Decline(string? reason = null) => new(null, reason);

    /// <summary>A token string converts to a successful result.</summary>
    public static implicit operator OAuthTokenResult(string? accessToken) => new(accessToken);
}

/// <summary>Supplies an OAuth access token when the CLI asks (<c>oauth_token_refresh</c>).</summary>
public delegate Task<OAuthTokenResult?> OAuthTokenCallback(CancellationToken cancellationToken);

/// <summary>Supplies a host auth token when the CLI asks (<c>host_auth_token_refresh</c>).</summary>
public delegate Task<string?> HostAuthTokenCallback(CancellationToken cancellationToken);

#endregion

#region can_use_tool context and result extras

/// <summary>The ask rule that triggered a permission prompt (<c>matched_ask_rule</c>).</summary>
public sealed record MatchedAskRule(string? Source, string? ToolName, string? RuleContent);

/// <summary>A folder on a remote computer (<c>computer_folder</c>).</summary>
public sealed record ComputerFolder(string Path, string ComputerName);

/// <summary>How the user classified a permission decision (<c>decisionClassification</c>).</summary>
public enum PermissionDecisionClassification
{
    /// <summary><c>user_temporary</c></summary>
    UserTemporary,

    /// <summary><c>user_permanent</c></summary>
    UserPermanent,

    /// <summary><c>user_reject</c></summary>
    UserReject
}

/// <summary>TS-only fields of the can_use_tool context.</summary>
public partial record ToolPermissionContext
{
    /// <summary>The control request id (echo it when answering out-of-band).</summary>
    public string? RequestId { get; init; }

    /// <summary>MCP server the tool belongs to.</summary>
    public McpServerProvenance? McpServer { get; init; }

    /// <summary>The CLI suggests defaulting to "no".</summary>
    public bool? DefaultToNo { get; init; }

    /// <summary>Do not offer an "always allow" rule.</summary>
    public bool? SuppressAlwaysAllowRule { get; init; }

    /// <summary>The ask rule that triggered the prompt.</summary>
    public MatchedAskRule? MatchedAskRule { get; init; }

    /// <summary>The prompt needs a human (no auto-approval).</summary>
    public bool? RequiresUserInteraction { get; init; }

    /// <summary>Category of <see cref="DecisionReason"/> (<c>decision_reason_type</c>).</summary>
    public string? DecisionReasonType { get; init; }

    /// <summary>Whether a classifier could approve this prompt.</summary>
    public bool? ClassifierApprovable { get; init; }

    /// <summary>Server-supplied prompt text.</summary>
    public string? ServerPrompt { get; init; }

    /// <summary>Remote folder the tool operates in.</summary>
    public ComputerFolder? ComputerFolder { get; init; }
}

/// <summary>TS-only fields of an allow decision.</summary>
public partial record PermissionResultAllow
{
    /// <summary>
    /// Tool use id echoed as <c>toolUseID</c>; the request's <c>tool_use_id</c>
    /// takes precedence (TS parity).
    /// </summary>
    public string? ToolUseId { get; init; }

    /// <summary>Sent as <c>decisionClassification</c>.</summary>
    public PermissionDecisionClassification? DecisionClassification { get; init; }
}

/// <summary>TS-only fields of a deny decision.</summary>
public partial record PermissionResultDeny
{
    /// <summary>
    /// Tool use id echoed as <c>toolUseID</c>; the request's <c>tool_use_id</c>
    /// takes precedence (TS parity).
    /// </summary>
    public string? ToolUseId { get; init; }

    /// <summary>Sent as <c>decisionClassification</c>.</summary>
    public PermissionDecisionClassification? DecisionClassification { get; init; }
}

#endregion

internal static class TsControlWire
{
    public static string ToWire(this PermissionDecisionClassification c) => c switch
    {
        PermissionDecisionClassification.UserTemporary => "user_temporary",
        PermissionDecisionClassification.UserPermanent => "user_permanent",
        PermissionDecisionClassification.UserReject => "user_reject",
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    };

    public static string ToWire(this ElicitationAction a) => a switch
    {
        ElicitationAction.Accept => "accept",
        ElicitationAction.Decline => "decline",
        ElicitationAction.Cancel => "cancel",
        _ => throw new ArgumentOutOfRangeException(nameof(a))
    };

    public static string ToWire(this ContextUsageDetail d) => d == ContextUsageDetail.Summary ? "summary" : "full";

    public static string ToWire(this ThinkingDisplayMode m) => m switch
    {
        ThinkingDisplayMode.Summarized => "summarized",
        ThinkingDisplayMode.Omitted => "omitted",
        ThinkingDisplayMode.Highlights => "highlights",
        _ => throw new ArgumentOutOfRangeException(nameof(m))
    };

    public static string ToWire(this SettingsFileSource s) =>
        s == SettingsFileSource.LocalSettings ? "localSettings" : "userSettings";

    public static string ToWire(this McpPermissionModeOverride m) =>
        m == McpPermissionModeOverride.Default ? "default" : "auto";

    public static string ToWire(this ReadFileEncoding e) => e == ReadFileEncoding.Base64 ? "base64" : "utf-8";
}
