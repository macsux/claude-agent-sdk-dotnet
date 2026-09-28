// Claude Agent SDK for .NET — TypeScript SDK parity for ClaudeAgentOptions and
// the option records it carries (sandbox, MCP server configs, plugins, agent
// definitions, system prompt blocks).
// Reference: @anthropic-ai/claude-agent-sdk 0.3.283 (sdk.d.ts `Options`, sdk.mjs
// option intake, ProcessTransport argv builder and Query.buildInitializeRequest).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claude.AgentSdk;

/// <summary>How plugins reach the CLI. TS: <c>pluginDelivery</c>.</summary>
public enum PluginDelivery
{
    /// <summary><c>--plugin-dir</c> / <c>--plugin-dir-no-mcp</c> per plugin (default).</summary>
    Argv,

    /// <summary>
    /// <c>--await-initialize</c> plus an initialize <c>plugins</c> array (CLI 2.1.261+).
    /// Avoids the Windows 32K command-line limit.
    /// </summary>
    Initialize
}

/// <summary>Who answers permission prompts. TS: <c>permissionPrompts</c>.</summary>
public enum PermissionPromptsMode
{
    /// <summary><c>host</c></summary>
    Host,

    /// <summary><c>none</c></summary>
    None
}

/// <summary>Format of the AskUserQuestion <c>preview</c> field.</summary>
public enum AskUserQuestionPreviewFormat
{
    /// <summary><c>markdown</c> (CLI default).</summary>
    Markdown,

    /// <summary><c>html</c>, for web-based hosts.</summary>
    Html
}

/// <summary>AskUserQuestion tool configuration. TS: <c>toolConfig.askUserQuestion</c>.</summary>
public sealed record AskUserQuestionConfig
{
    /// <summary>Env <c>CLAUDE_CODE_QUESTION_PREVIEW_FORMAT</c>.</summary>
    public AskUserQuestionPreviewFormat? PreviewFormat { get; init; }

    /// <summary>
    /// Env <c>CLAUDE_CODE_QUESTION_EXTENDED=1</c> when true (undocumented in TS).
    /// When not true, an inherited value is removed unless <see cref="ClaudeAgentOptions.Env"/> sets it.
    /// </summary>
    public bool? ExtendedQuestions { get; init; }

    /// <summary>
    /// Env <c>CLAUDE_CODE_QUESTION_OPTIONAL_DESCRIPTIONS=1</c> when true (undocumented in TS).
    /// When not true, an inherited value is removed unless <see cref="ClaudeAgentOptions.Env"/> sets it.
    /// </summary>
    public bool? OptionalDescriptions { get; init; }
}

/// <summary>Built-in tool configuration. TS: <c>ToolConfig</c>.</summary>
public sealed record ToolConfig
{
    /// <summary>AskUserQuestion settings.</summary>
    public AskUserQuestionConfig? AskUserQuestion { get; init; }
}

/// <summary>
/// Pre-accepted workspace trust (undocumented TS <c>workspaceTrust</c>). Dropped
/// with a warning when <see cref="Accepted"/> is true and <see cref="Directory"/>
/// is not absolute.
/// </summary>
public sealed record WorkspaceTrust(bool? Accepted, string? Directory);

/// <summary>Per-tool policy for SSE / HTTP MCP servers. TS: <c>McpServerToolPolicy</c>.</summary>
public sealed record McpServerToolPolicy
{
    /// <summary>Tool name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary><c>always_allow</c>, <c>always_ask</c> or <c>always_deny</c>.</summary>
    [JsonPropertyName("permission_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionPolicy { get; init; }

    /// <summary>Org admin ceiling: <c>allow</c>, <c>ask</c> or <c>blocked</c>.</summary>
    [JsonPropertyName("org_max_permission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrgMaxPermission { get; init; }
}

public partial record McpStdioServerConfig
{
    /// <summary>Per-call tool timeout in milliseconds (values below 1000 are ignored by the CLI).</summary>
    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Timeout { get; init; }

    /// <summary>Always include this server's tools in the prompt (never deferred).</summary>
    [JsonPropertyName("alwaysLoad")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AlwaysLoad { get; init; }
}

public partial record McpSSEServerConfig
{
    /// <summary>Per-tool policies.</summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<McpServerToolPolicy>? Tools { get; init; }

    /// <summary>Per-call tool timeout in milliseconds.</summary>
    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Timeout { get; init; }

    /// <summary>Always include this server's tools in the prompt (never deferred).</summary>
    [JsonPropertyName("alwaysLoad")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AlwaysLoad { get; init; }
}

public partial record McpHttpServerConfig
{
    /// <summary>Per-tool policies.</summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<McpServerToolPolicy>? Tools { get; init; }

    /// <summary>Per-call tool timeout in milliseconds.</summary>
    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Timeout { get; init; }

    /// <summary>Always include this server's tools in the prompt (never deferred).</summary>
    [JsonPropertyName("alwaysLoad")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AlwaysLoad { get; init; }
}

public partial record McpSdkServerConfig
{
    /// <summary>
    /// Per-call tool timeout in milliseconds, sent in the initialize request as
    /// <c>sdkMcpServerConfigs: {name: {timeout}}</c> (and in <c>mcp_set_servers</c>).
    /// </summary>
    [JsonIgnore]
    public int? Timeout { get; init; }
}

public partial record SdkPluginConfig
{
    /// <summary>
    /// Load the plugin's skills/hooks/agents/commands but not its MCP servers:
    /// <c>--plugin-dir-no-mcp</c> instead of <c>--plugin-dir</c>.
    /// </summary>
    [JsonPropertyName("skipMcpDiscovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SkipMcpDiscovery { get; init; }
}

public partial record AgentDefinition
{
    /// <summary>Experimental critical reminder (<c>criticalSystemReminder_EXPERIMENTAL</c>).</summary>
    [JsonPropertyName("criticalSystemReminder_EXPERIMENTAL")]
    public string? CriticalSystemReminderExperimental { get; init; }

    /// <summary>Run without user/project/local CLAUDE.md files as a subagent (<c>omitClaudeMd</c>).</summary>
    [JsonPropertyName("omitClaudeMd")]
    public bool? OmitClaudeMd { get; init; }

    /// <summary>Agent type auto-spawned as a background observer (<c>observer</c>).</summary>
    [JsonPropertyName("observer")]
    public string? Observer { get; init; }

    /// <summary>Postamble appended to each observer digest (<c>observerMessage</c>).</summary>
    [JsonPropertyName("observerMessage")]
    public string? ObserverMessage { get; init; }
}

/// <summary>TLS termination for the sandbox network proxy.</summary>
public sealed record SandboxTlsTerminate
{
    [JsonPropertyName("caCertPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaCertPath { get; init; }

    [JsonPropertyName("caKeyPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaKeyPath { get; init; }
}

public partial record SandboxNetworkConfig
{
    /// <summary>Only allowedDomains are reachable (no implicit allowances).</summary>
    [JsonPropertyName("strictAllowlist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StrictAllowlist { get; init; }

    /// <summary>TLS termination CA.</summary>
    [JsonPropertyName("tlsTerminate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxTlsTerminate? TlsTerminate { get; init; }
}

/// <summary>Sandbox filesystem rules. TS: <c>SandboxFilesystemConfig</c>.</summary>
public sealed record SandboxFilesystemConfig
{
    [JsonPropertyName("allowWrite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowWrite { get; init; }

    [JsonPropertyName("denyWrite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DenyWrite { get; init; }

    [JsonPropertyName("denyRead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DenyRead { get; init; }

    [JsonPropertyName("allowRead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowRead { get; init; }

    [JsonPropertyName("allowManagedReadPathsOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowManagedReadPathsOnly { get; init; }

    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Disabled { get; init; }
}

/// <summary>Custom ripgrep binary for the sandbox.</summary>
public sealed record SandboxRipgrepConfig
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Args { get; init; }
}

public partial record SandboxSettings
{
    /// <summary>
    /// Fail instead of running unsandboxed when the sandbox is unavailable. When
    /// <see cref="Enabled"/> is true and this is unset, the SDK sends <c>true</c> (TS parity).
    /// </summary>
    [JsonPropertyName("failIfUnavailable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FailIfUnavailable { get; init; }

    /// <summary>Filesystem rules.</summary>
    [JsonPropertyName("filesystem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxFilesystemConfig? Filesystem { get; init; }

    /// <summary>Credential masking/denial rules (TS <c>SandboxCredentialsConfig</c>, kept as JSON).</summary>
    [JsonPropertyName("credentials")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Credentials { get; init; }

    [JsonPropertyName("enableWeakerNetworkIsolation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableWeakerNetworkIsolation { get; init; }

    [JsonPropertyName("allowAppleEvents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowAppleEvents { get; init; }

    [JsonPropertyName("ripgrep")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxRipgrepConfig? Ripgrep { get; init; }

    [JsonPropertyName("bwrapPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BwrapPath { get; init; }

    [JsonPropertyName("socatPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SocatPath { get; init; }

    /// <summary>Any other sandbox keys (the TS schema is loose), written verbatim.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// A system prompt given as blocks (TS <c>systemPrompt: string[]</c> or the
/// custom form with <c>prompt: string[]</c>). Sent in the initialize request as
/// <c>systemPrompt: [...]</c>; no <c>--system-prompt</c> flag is passed. Put
/// <see cref="SystemPromptConfig.DynamicBoundary"/> between the static and the
/// per-user blocks to mark the cacheable prefix.
/// </summary>
public sealed record SystemPromptBlocks(IReadOnlyList<string> Blocks) : SystemPromptConfig
{
    /// <summary>Same as <see cref="SystemPromptPreset.Snapshot"/>.</summary>
    public bool? Snapshot { get; init; }

    internal override bool? SnapshotForInitialize => Snapshot;
}

public partial record SystemPromptConfig
{
    /// <summary>
    /// Separates the static (cacheable) blocks of a <see cref="SystemPromptBlocks"/>
    /// prompt from the dynamic ones. TS: <c>SYSTEM_PROMPT_DYNAMIC_BOUNDARY</c>.
    /// </summary>
    public const string DynamicBoundary = "__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__";

    /// <summary>A block-list system prompt.</summary>
    public static implicit operator SystemPromptConfig(string[] blocks) =>
        blocks is null ? null! : new SystemPromptBlocks(blocks);

    /// <summary>A block-list system prompt.</summary>
    public static implicit operator SystemPromptConfig(List<string> blocks) =>
        blocks is null ? null! : new SystemPromptBlocks(blocks);
}

/// <summary>TypeScript-SDK options (see <c>docs/PARITY.md</c>, TS section).</summary>
public partial record ClaudeAgentOptions
{
    // ---- control-protocol callbacks ------------------------------------------------

    /// <summary>
    /// Answers MCP elicitations (control <c>elicitation</c>). Without it the SDK
    /// replies <c>{"action":"decline"}</c>. TS: <c>onElicitation</c>.
    /// </summary>
    public ElicitationCallback? OnElicitation { get; init; }

    /// <summary>
    /// Renders dialogs the CLI asks for (control <c>request_user_dialog</c>).
    /// Without it the SDK leaves the request unanswered. TS: <c>onUserDialog</c>.
    /// </summary>
    public UserDialogCallback? OnUserDialog { get; init; }

    /// <summary>
    /// Dialog kinds this host can render (initialize <c>supportedDialogKinds</c>).
    /// Requires <see cref="OnUserDialog"/> when non-empty.
    /// </summary>
    public IReadOnlyList<string>? SupportedDialogKinds { get; init; }

    /// <summary>
    /// Supplies OAuth tokens on <c>oauth_token_refresh</c>; also sets env
    /// <c>CLAUDE_CODE_SDK_HAS_OAUTH_REFRESH=1</c>. Undocumented TS <c>getOAuthToken</c>.
    /// </summary>
    public OAuthTokenCallback? GetOAuthToken { get; init; }

    /// <summary>
    /// Supplies host auth tokens on <c>host_auth_token_refresh</c>; also sets env
    /// <c>CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH=1</c>. Undocumented TS <c>getHostAuthToken</c>.
    /// </summary>
    public HostAuthTokenCallback? GetHostAuthToken { get; init; }

    /// <summary>
    /// Raw frame tap: invoked with every stdout frame the CLI sends, before the SDK
    /// routes it, including control requests/responses, keep-alives and transcript
    /// mirror frames that never reach the message stream. Runs on the reader loop;
    /// keep it fast and non-throwing (exceptions are logged and swallowed). .NET addition.
    /// </summary>
    public Action<JsonElement>? OnRawMessage { get; init; }

    // ---- argv ------------------------------------------------------------------------

    /// <summary>Persist the session to disk; false → <c>--no-session-persistence</c>. Incompatible with <see cref="SessionStore"/>.</summary>
    public bool PersistSession { get; init; } = true;

    /// <summary><c>--allow-dangerously-skip-permissions</c> (required by the CLI for bypassPermissions).</summary>
    public bool AllowDangerouslySkipPermissions { get; init; }

    /// <summary>Main-thread agent: <c>--agent &lt;name&gt;</c>.</summary>
    public string? Agent { get; init; }

    /// <summary><c>--debug</c> (ignored when <see cref="DebugFile"/> is set).</summary>
    public bool Debug { get; init; }

    /// <summary><c>--debug-file &lt;path&gt;</c>.</summary>
    public string? DebugFile { get; init; }

    /// <summary>
    /// Policy-tier settings: <c>--managed-settings &lt;json&gt;</c>. The CLI keeps only
    /// restrictive keys.
    /// </summary>
    public JsonElement? ManagedSettings { get; init; }

    /// <summary>Trusted config root for worktrees: <c>--project-config-root=&lt;path&gt;</c>.</summary>
    public string? ProjectConfigRoot { get; init; }

    /// <summary><c>--permission-prompts host|none</c>.</summary>
    public PermissionPromptsMode? PermissionPrompts { get; init; }

    /// <summary>How <see cref="Plugins"/> are delivered.</summary>
    public PluginDelivery PluginDelivery { get; init; } = PluginDelivery.Argv;

    /// <summary><c>--channels &lt;c&gt;</c> per channel (undocumented TS option).</summary>
    public IReadOnlyList<string>? Channels { get; init; }

    /// <summary><c>--workload &lt;v&gt;</c> (undocumented TS option).</summary>
    public string? Workload { get; init; }

    // ---- env ------------------------------------------------------------------------

    /// <summary>Built-in tool configuration (env <c>CLAUDE_CODE_QUESTION_*</c>).</summary>
    public ToolConfig? ToolConfig { get; init; }

    /// <summary>
    /// When false, the CLI gets only <see cref="Env"/> plus the SDK's own variables
    /// (TS semantics: <c>env</c> replaces <c>process.env</c>). Default true: <see cref="Env"/>
    /// is merged over the inherited environment (Python semantics).
    /// </summary>
    public bool InheritEnvironment { get; init; } = true;

    // ---- initialize request ---------------------------------------------------------

    /// <summary>Session title (initialize <c>title</c>).</summary>
    public string? Title { get; init; }

    /// <summary>Plan-mode instructions (initialize <c>planModeInstructions</c>).</summary>
    public string? PlanModeInstructions { get; init; }

    /// <summary>Tool name aliases (initialize <c>toolAliases</c>), e.g. <c>{"Bash": "mcp__x__bash"}</c>.</summary>
    public IReadOnlyDictionary<string, string>? ToolAliases { get; init; }

    /// <summary>Emit <c>prompt_suggestion</c> messages after results (initialize <c>promptSuggestions</c>).</summary>
    public bool? PromptSuggestions { get; init; }

    /// <summary>Summaries in <c>task_progress</c> (initialize <c>agentProgressSummaries</c>).</summary>
    public bool? AgentProgressSummaries { get; init; }

    /// <summary>Interrupts spare background tasks (initialize <c>perTaskStopAffordance</c>).</summary>
    public bool? PerTaskStopAffordance { get; init; }

    /// <summary>Appended to every subagent's system prompt (undocumented initialize key).</summary>
    public string? AppendSubagentSystemPrompt { get; init; }

    /// <summary>MCP servers exempt from web-search isolation (undocumented initialize key).</summary>
    public IReadOnlyList<string>? WebSearchIsolationExemptMcpServers { get; init; }

    /// <summary>Let a rapid follow-up preempt the running turn (undocumented initialize key).</summary>
    public bool? RapidFollowupPreempt { get; init; }

    /// <summary>Pre-accepted workspace trust (undocumented initialize key).</summary>
    public WorkspaceTrust? WorkspaceTrust { get; init; }
}
