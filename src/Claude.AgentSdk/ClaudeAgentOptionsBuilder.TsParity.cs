using System.Text.Json;
using Claude.AgentSdk.Internal;

namespace Claude.AgentSdk;

/// <summary>Builder support for the TypeScript-SDK options.</summary>
public sealed partial class ClaudeAgentOptionsBuilder
{
    private readonly List<Func<ClaudeAgentOptions, ClaudeAgentOptions>> _tsOptions = [];

    private ClaudeAgentOptionsBuilder Ts(Func<ClaudeAgentOptions, ClaudeAgentOptions> apply)
    {
        _tsOptions.Add(apply);
        return this;
    }

    private ClaudeAgentOptions ApplyTsOptions(ClaudeAgentOptions options)
    {
        foreach (var apply in _tsOptions)
            options = apply(options);
        return options;
    }

    /// <summary>Answer MCP elicitations (<see cref="ClaudeAgentOptions.OnElicitation"/>).</summary>
    public ClaudeAgentOptionsBuilder OnElicitation(ElicitationCallback callback) =>
        Ts(o => o with { OnElicitation = callback });

    /// <summary>
    /// Render CLI dialogs (<see cref="ClaudeAgentOptions.OnUserDialog"/>) and declare
    /// the dialog kinds this host supports.
    /// </summary>
    public ClaudeAgentOptionsBuilder OnUserDialog(UserDialogCallback callback, params string[] supportedDialogKinds) =>
        Ts(o => o with
        {
            OnUserDialog = callback,
            SupportedDialogKinds = supportedDialogKinds.Length > 0 ? supportedDialogKinds : o.SupportedDialogKinds
        });

    /// <summary>Supply OAuth tokens on refresh (<see cref="ClaudeAgentOptions.GetOAuthToken"/>).</summary>
    public ClaudeAgentOptionsBuilder GetOAuthToken(OAuthTokenCallback callback) =>
        Ts(o => o with { GetOAuthToken = callback });

    /// <summary>Supply host auth tokens on refresh (<see cref="ClaudeAgentOptions.GetHostAuthToken"/>).</summary>
    public ClaudeAgentOptionsBuilder GetHostAuthToken(HostAuthTokenCallback callback) =>
        Ts(o => o with { GetHostAuthToken = callback });

    /// <summary>Observe every raw stdout frame (<see cref="ClaudeAgentOptions.OnRawMessage"/>).</summary>
    public ClaudeAgentOptionsBuilder OnRawMessage(Action<JsonElement> tap) =>
        Ts(o => o with { OnRawMessage = tap });

    /// <summary>Persist the session to disk (false → <c>--no-session-persistence</c>).</summary>
    public ClaudeAgentOptionsBuilder PersistSession(bool value = true) =>
        Ts(o => o with { PersistSession = value });

    /// <summary><c>--allow-dangerously-skip-permissions</c>.</summary>
    public ClaudeAgentOptionsBuilder AllowDangerouslySkipPermissions(bool value = true) =>
        Ts(o => o with { AllowDangerouslySkipPermissions = value });

    /// <summary>Main-thread agent (<c>--agent</c>).</summary>
    public ClaudeAgentOptionsBuilder Agent(string name) => Ts(o => o with { Agent = name });

    /// <summary><c>--debug</c>.</summary>
    public ClaudeAgentOptionsBuilder Debug(bool value = true) => Ts(o => o with { Debug = value });

    /// <summary><c>--debug-file &lt;path&gt;</c>.</summary>
    public ClaudeAgentOptionsBuilder DebugFile(string path) => Ts(o => o with { DebugFile = path });

    /// <summary>Inline settings object, serialized to JSON for <c>--settings</c>.</summary>
    public ClaudeAgentOptionsBuilder Settings(JsonElement settings)
    {
        _settings = settings.GetRawText();
        return this;
    }

    /// <summary>Inline settings object, serialized to JSON for <c>--settings</c>.</summary>
    public ClaudeAgentOptionsBuilder Settings(IReadOnlyDictionary<string, object?> settings)
    {
        _settings = SdkJson.Serialize(new Dictionary<string, object?>(settings));
        return this;
    }

    /// <summary>Policy-tier settings (<c>--managed-settings</c>).</summary>
    public ClaudeAgentOptionsBuilder ManagedSettings(JsonElement settings) =>
        Ts(o => o with { ManagedSettings = settings.Clone() });

    /// <summary>Policy-tier settings (<c>--managed-settings</c>).</summary>
    public ClaudeAgentOptionsBuilder ManagedSettings(IReadOnlyDictionary<string, object?> settings)
    {
        var element = SdkJson.SerializeToElement(new Dictionary<string, object?>(settings));
        return Ts(o => o with { ManagedSettings = element });
    }

    /// <summary><c>--project-config-root=&lt;path&gt;</c>.</summary>
    public ClaudeAgentOptionsBuilder ProjectConfigRoot(string path) => Ts(o => o with { ProjectConfigRoot = path });

    /// <summary><c>--permission-prompts host|none</c>.</summary>
    public ClaudeAgentOptionsBuilder PermissionPrompts(PermissionPromptsMode mode) =>
        Ts(o => o with { PermissionPrompts = mode });

    /// <summary>How plugins are delivered to the CLI.</summary>
    public ClaudeAgentOptionsBuilder PluginDelivery(PluginDelivery delivery) =>
        Ts(o => o with { PluginDelivery = delivery });

    /// <summary>Add a local plugin, optionally without MCP discovery (<c>--plugin-dir-no-mcp</c>).</summary>
    public ClaudeAgentOptionsBuilder Plugin(string path, bool skipMcpDiscovery)
    {
        _plugins.Add(new SdkPluginConfig("local", path) { SkipMcpDiscovery = skipMcpDiscovery });
        return this;
    }

    /// <summary><c>--channels &lt;c&gt;</c> per channel.</summary>
    public ClaudeAgentOptionsBuilder Channels(params string[] channels) => Ts(o => o with { Channels = channels });

    /// <summary><c>--workload &lt;v&gt;</c>.</summary>
    public ClaudeAgentOptionsBuilder Workload(string workload) => Ts(o => o with { Workload = workload });

    /// <summary>Built-in tool configuration.</summary>
    public ClaudeAgentOptionsBuilder ToolConfig(ToolConfig config) => Ts(o => o with { ToolConfig = config });

    /// <summary>AskUserQuestion configuration (env <c>CLAUDE_CODE_QUESTION_*</c>).</summary>
    public ClaudeAgentOptionsBuilder AskUserQuestion(
        AskUserQuestionPreviewFormat? previewFormat = null,
        bool? extendedQuestions = null,
        bool? optionalDescriptions = null) =>
        Ts(o => o with
        {
            ToolConfig = new ToolConfig
            {
                AskUserQuestion = new AskUserQuestionConfig
                {
                    PreviewFormat = previewFormat,
                    ExtendedQuestions = extendedQuestions,
                    OptionalDescriptions = optionalDescriptions
                }
            }
        });

    /// <summary>False: the CLI sees only <see cref="ClaudeAgentOptions.Env"/> plus SDK variables.</summary>
    public ClaudeAgentOptionsBuilder InheritEnvironment(bool value = true) =>
        Ts(o => o with { InheritEnvironment = value });

    /// <summary>A block-list system prompt (see <see cref="SystemPromptConfig.DynamicBoundary"/>).</summary>
    public ClaudeAgentOptionsBuilder SystemPromptBlocks(params string[] blocks)
    {
        _systemPrompt = new SystemPromptBlocks(blocks);
        return this;
    }

    /// <summary>Session title (initialize <c>title</c>).</summary>
    public ClaudeAgentOptionsBuilder Title(string title) => Ts(o => o with { Title = title });

    /// <summary>Plan-mode instructions (initialize <c>planModeInstructions</c>).</summary>
    public ClaudeAgentOptionsBuilder PlanModeInstructions(string instructions) =>
        Ts(o => o with { PlanModeInstructions = instructions });

    /// <summary>Add a tool alias (initialize <c>toolAliases</c>).</summary>
    public ClaudeAgentOptionsBuilder ToolAlias(string toolName, string alias) =>
        Ts(o =>
        {
            var aliases = o.ToolAliases != null
                ? new Dictionary<string, string>(o.ToolAliases)
                : new Dictionary<string, string>();
            aliases[toolName] = alias;
            return o with { ToolAliases = aliases };
        });

    /// <summary>Emit prompt suggestions (initialize <c>promptSuggestions</c>).</summary>
    public ClaudeAgentOptionsBuilder PromptSuggestions(bool value = true) =>
        Ts(o => o with { PromptSuggestions = value });

    /// <summary>Progress summaries for subagents (initialize <c>agentProgressSummaries</c>).</summary>
    public ClaudeAgentOptionsBuilder AgentProgressSummaries(bool value = true) =>
        Ts(o => o with { AgentProgressSummaries = value });

    /// <summary>Interrupts spare background tasks (initialize <c>perTaskStopAffordance</c>).</summary>
    public ClaudeAgentOptionsBuilder PerTaskStopAffordance(bool value = true) =>
        Ts(o => o with { PerTaskStopAffordance = value });

    /// <summary>Appended to every subagent system prompt (undocumented initialize key).</summary>
    public ClaudeAgentOptionsBuilder AppendSubagentSystemPrompt(string text) =>
        Ts(o => o with { AppendSubagentSystemPrompt = text });

    /// <summary>MCP servers exempt from web-search isolation (undocumented initialize key).</summary>
    public ClaudeAgentOptionsBuilder WebSearchIsolationExemptMcpServers(params string[] servers) =>
        Ts(o => o with { WebSearchIsolationExemptMcpServers = servers });

    /// <summary>Let rapid follow-ups preempt the running turn (undocumented initialize key).</summary>
    public ClaudeAgentOptionsBuilder RapidFollowupPreempt(bool value = true) =>
        Ts(o => o with { RapidFollowupPreempt = value });

    /// <summary>Pre-accepted workspace trust for an absolute directory (undocumented initialize key).</summary>
    public ClaudeAgentOptionsBuilder WorkspaceTrust(string directory, bool accepted = true) =>
        Ts(o => o with { WorkspaceTrust = new WorkspaceTrust(accepted, directory) });
}
