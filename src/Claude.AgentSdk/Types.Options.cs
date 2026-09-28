// Claude Agent SDK for .NET — strongly typed unions for ClaudeAgentOptions /
// AgentDefinition members that Python models as ``A | B | ...`` unions.
// Reference: claude-agent-sdk-python src/claude_agent_sdk/types.py (v0.2.158).
//
// Each union is a small closed record hierarchy with implicit conversions from
// the shapes the previous ``object``-typed members accepted, so existing code
// such as ``SystemPrompt = "x"``, ``Skills = "all"``,
// ``Skills = new List<string> { ... }`` and
// ``McpServers = new Dictionary<string, object> { ... }`` keeps compiling.

using System.Text.Json.Serialization;

namespace Claude.AgentSdk;

#region System prompt

/// <summary>
/// System prompt configuration. Python:
/// <c>str | SystemPromptPreset | SystemPromptCustom | SystemPromptFile</c>.
/// </summary>
/// <remarks>
/// The cases are <see cref="SystemPromptText"/> (a plain string),
/// <see cref="SystemPromptPreset"/>, <see cref="SystemPromptCustom"/> and
/// <see cref="SystemPromptFile"/>. A <see cref="string"/> converts implicitly
/// to <see cref="SystemPromptText"/>.
/// </remarks>
public abstract partial record SystemPromptConfig
{
    private protected SystemPromptConfig() { }

    /// <summary>A plain custom system prompt (Python: <c>system_prompt="..."</c>).</summary>
    public static implicit operator SystemPromptConfig(string prompt) =>
        prompt is null ? null! : new SystemPromptText(prompt);

    /// <summary>Create a plain custom system prompt.</summary>
    public static SystemPromptConfig FromText(string prompt) => new SystemPromptText(prompt);

    /// <summary>
    /// The value sent as <c>systemPromptSnapshot</c> on the initialize request.
    /// Python reads <c>snapshot</c> only from the preset and custom forms.
    /// </summary>
    internal virtual bool? SnapshotForInitialize => null;
}

/// <summary>
/// A plain custom system prompt, equivalent to Python's
/// <c>system_prompt="..."</c>. Maps to <c>--system-prompt &lt;text&gt;</c>.
/// </summary>
public sealed record SystemPromptText(string Text) : SystemPromptConfig
{
    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// A custom system prompt in the form that can also set <see cref="Snapshot"/>.
/// Python: <c>SystemPromptCustom</c> (<c>{"type": "custom", "prompt", "snapshot"}</c>).
/// Maps to <c>--system-prompt &lt;prompt&gt;</c>; <see cref="Snapshot"/> rides
/// in the initialize request as <c>systemPromptSnapshot</c>.
/// </summary>
public sealed record SystemPromptCustom : SystemPromptConfig
{
    /// <summary>Type identifier. Always "custom".</summary>
    [JsonPropertyName("type")]
    public string Type => "custom";

    /// <summary>The system prompt text.</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>Same as <see cref="SystemPromptPreset.Snapshot"/>, applied to <see cref="Prompt"/>.</summary>
    [JsonPropertyName("snapshot")]
    public bool? Snapshot { get; init; }

    internal override bool? SnapshotForInitialize => Snapshot;
}

#endregion

#region Skills

/// <summary>
/// Skills to enable for the main session. Python:
/// <c>list[str] | Literal["all"] | None</c>.
/// </summary>
/// <remarks>
/// <para><c>"all"</c> converts implicitly to <see cref="All"/>; a
/// <see cref="List{T}"/> or array of names converts to <see cref="Named"/>.
/// Any other string is rejected (Python raises <c>TypeError</c>, since a bare
/// string would otherwise be iterated as characters).</para>
/// <para><c>null</c> (the default) means no SDK auto-configuration — it is not
/// "skills off"; use an empty list to suppress every skill.</para>
/// </remarks>
public abstract record SkillsConfig
{
    private protected SkillsConfig() { }

    /// <summary>The <c>"all"</c> literal (Python <c>_SKILLS_ALL</c>).</summary>
    public const string AllLiteral = "all";

    /// <summary>Enable every discovered skill (Python <c>skills="all"</c>).</summary>
    public static SkillsConfig All { get; } = new AllSkills();

    /// <summary>Enable only the listed skills.</summary>
    public static SkillsConfig Only(params string[] names) => new Named(names.ToList());

    /// <summary>Enable only the listed skills.</summary>
    public static SkillsConfig Only(IEnumerable<string> names) => new Named(names.ToList());

    /// <summary>
    /// <c>"all"</c> → <see cref="All"/>. Any other string throws: Python's
    /// <c>_reject_non_list_skills</c> raises for a bare skill name.
    /// </summary>
    public static implicit operator SkillsConfig(string value)
    {
        if (value is null)
            return null!;
        if (value == AllLiteral)
            return All;
        throw new ArgumentException(
            $"ClaudeAgentOptions.Skills must be a list of skill names or \"all\", got \"{value}\". " +
            $"Did you mean [\"{value}\"]?");
    }

    /// <summary>A list of skill names.</summary>
    public static implicit operator SkillsConfig(List<string> names) =>
        names is null ? null! : new Named(names.ToList());

    /// <summary>An array of skill names.</summary>
    public static implicit operator SkillsConfig(string[] names) =>
        names is null ? null! : new Named(names.ToList());

    /// <summary>Every discovered skill.</summary>
    public sealed record AllSkills : SkillsConfig
    {
        internal AllSkills() { }

        /// <inheritdoc />
        public override string ToString() => AllLiteral;
    }

    /// <summary>
    /// Only the listed skills. Names match the SKILL.md <c>name</c> / directory
    /// name, or <c>plugin:skill</c>; they are validated at connect.
    /// </summary>
    public sealed record Named(IReadOnlyList<string> Names) : SkillsConfig;
}

#endregion

#region MCP servers

/// <summary>
/// MCP server configuration. Python:
/// <c>dict[str, McpServerConfig] | str | Path</c>.
/// </summary>
/// <remarks>
/// A <see cref="Dictionary{TKey,TValue}"/> of name → config (including the
/// <c>McpServerRegistry</c> returned by the <c>McpServers</c> helpers)
/// converts implicitly to <see cref="ServerMap"/>; a string (a path to an MCP
/// config file, or a JSON string) converts to <see cref="ConfigPath"/>.
/// Map values are <see cref="McpStdioServerConfig"/>,
/// <see cref="McpSSEServerConfig"/>, <see cref="McpHttpServerConfig"/>,
/// <see cref="McpSdkServerConfig"/>, or any JSON-serializable object.
/// </remarks>
public abstract record McpServersConfig
{
    private protected McpServersConfig() { }

    /// <summary>Server name → server configuration.</summary>
    public static implicit operator McpServersConfig(Dictionary<string, object> servers) =>
        servers is null ? null! : new ServerMap(servers);

    /// <summary>A path to an MCP config JSON file, or a JSON string.</summary>
    public static implicit operator McpServersConfig(string pathOrJson) =>
        pathOrJson is null ? null! : new ConfigPath(pathOrJson);

    /// <summary>Create from any read-only name → config map.</summary>
    public static McpServersConfig FromServers(IReadOnlyDictionary<string, object> servers) => new ServerMap(servers);

    /// <summary>Inline server configurations keyed by server name.</summary>
    public sealed record ServerMap(IReadOnlyDictionary<string, object> Servers) : McpServersConfig;

    /// <summary>
    /// A path to an MCP config file, or a JSON string; passed to
    /// <c>--mcp-config</c> verbatim.
    /// </summary>
    public sealed record ConfigPath(string Value) : McpServersConfig
    {
        /// <inheritdoc />
        public override string ToString() => Value;
    }

    /// <summary>The in-process (<c>type: "sdk"</c>) servers in this configuration.</summary>
    internal IEnumerable<KeyValuePair<string, McpSdkServerConfig>> SdkServers() =>
        this is ServerMap map
            ? map.Servers
                .Where(kv => kv.Value is McpSdkServerConfig)
                .Select(kv => new KeyValuePair<string, McpSdkServerConfig>(kv.Key, (McpSdkServerConfig)kv.Value))
            : [];
}

#endregion

#region Agent definition unions

/// <summary>
/// Effort for a sub-agent. Python: <c>EffortLevel | int</c>.
/// </summary>
/// <remarks>
/// An <see cref="EffortLevel"/> converts implicitly to <see cref="Level"/>, an
/// <see cref="int"/> to <see cref="Tokens"/>.
/// </remarks>
public abstract record AgentEffort
{
    private protected AgentEffort() { }

    /// <summary>An effort level.</summary>
    public static implicit operator AgentEffort(EffortLevel level) => new Level(level);

    /// <summary>A numeric effort value (passed through to the CLI as a number).</summary>
    public static implicit operator AgentEffort(int value) => new Tokens(value);

    /// <summary>The value placed in the initialize request's agent definition.</summary>
    internal abstract object ToWire();

    /// <summary>An <see cref="EffortLevel"/> (serialized as its string form).</summary>
    public sealed record Level(EffortLevel Value) : AgentEffort
    {
        internal override object ToWire() => Value.ToJsonString();
    }

    /// <summary>A numeric effort value.</summary>
    public sealed record Tokens(int Value) : AgentEffort
    {
        internal override object ToWire() => Value;
    }
}

/// <summary>
/// One entry of <see cref="AgentDefinition.McpServers"/>. Python:
/// <c>str | dict[str, Any]</c> — a server name, or an inline
/// <c>{name: config}</c> map.
/// </summary>
public abstract record AgentMcpServer
{
    private protected AgentMcpServer() { }

    /// <summary>Reference a configured server by name.</summary>
    public static implicit operator AgentMcpServer(string name) =>
        name is null ? null! : new Reference(name);

    /// <summary>An inline <c>{name: config}</c> server map.</summary>
    public static implicit operator AgentMcpServer(Dictionary<string, object> servers) =>
        servers is null ? null! : new Inline(servers);

    /// <summary>The value placed in the initialize request's agent definition.</summary>
    internal abstract object ToWire();

    /// <summary>A server referenced by name.</summary>
    public sealed record Reference(string Name) : AgentMcpServer
    {
        internal override object ToWire() => Name;
    }

    /// <summary>An inline <c>{name: config}</c> server map.</summary>
    public sealed record Inline(IReadOnlyDictionary<string, object> Servers) : AgentMcpServer
    {
        internal override object ToWire() => Servers;
    }
}

#endregion
