// Claude Agent SDK for .NET
// Port of the can_use_tool helpers in claude-agent-sdk-python/types.py:
// _whole_tool_allowed, _get_can_use_tool_shadowed_warning,
// _warn_if_can_use_tool_shadowed and _configure_can_use_tool.

using System.Collections.Concurrent;

namespace Claude.AgentSdk.Internal;

internal static class CanUseToolConfiguration
{
    // Python's default warnings filter shows a given message once per process.
    private static readonly ConcurrentDictionary<string, byte> EmittedWarnings = new();

    /// <summary>Where shadowing warnings go (stderr, like Python's <c>warnings.warn</c>).</summary>
    internal static Action<string> WarningSink { get; set; } = message => Console.Error.WriteLine($"Warning: {message}");

    /// <summary>
    /// The tool an <c>AllowedTools</c> entry allows outright, else <c>null</c>.
    /// Mirrors the CLI's rule parser: an entry allows a whole tool when it has
    /// no <c>(...)</c> specifier (<c>"Read"</c>), or when the specifier is empty
    /// or a lone wildcard (<c>"Read()"</c>, <c>"Read(*)"</c>). Python:
    /// <c>_whole_tool_allowed</c>.
    /// </summary>
    internal static string? WholeToolAllowed(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;
        var openIndex = entry.IndexOf('(');
        if (openIndex == -1)
            return entry;
        if (openIndex == 0 || !entry.EndsWith(')'))
            return null;
        var inner = entry[(openIndex + 1)..^1];
        return inner is "" or "*" ? entry[..openIndex] : null;
    }

    /// <summary>The shadowing warning for these options, or <c>null</c>. Python: <c>_get_can_use_tool_shadowed_warning</c>.</summary>
    internal static string? GetShadowedWarning(PermissionMode? permissionMode, IEnumerable<string> allowedTools)
    {
        if (permissionMode == PermissionMode.BypassPermissions)
        {
            return "can_use_tool will not be invoked: permission_mode 'bypassPermissions' auto-approves " +
                   "every tool call (except explicit deny rules) before the callback is consulted. To gate " +
                   "every tool call, use a PreToolUse hook instead.";
        }

        // Distinct() preserves first-seen order: ["Read", "Read()"] resolve to the
        // same tool and must not report it twice.
        var shadowed = allowedTools
            .Select(WholeToolAllowed)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (shadowed.Count == 0)
            return null;
        return $"can_use_tool will not be invoked for: {string.Join(", ", shadowed)}. " +
               "An allowed_tools entry that allows a whole tool auto-approves it before the callback is " +
               "consulted. To gate every tool call, use a PreToolUse hook; or narrow the entry so calls fall " +
               "through to can_use_tool. Allow rules from settings files can also shadow the callback but are " +
               "not visible here.";
    }

    /// <summary>
    /// The shadowing warning for <paramref name="options"/>, accounting for
    /// <c>Skills = "all"</c> (which appends a bare <c>Skill</c> allow rule).
    /// Python: <c>_warn_if_can_use_tool_shadowed</c>.
    /// </summary>
    internal static string? GetShadowedWarning(ClaudeAgentOptions options)
    {
        if (options.CanUseTool == null)
            return null;
        IEnumerable<string> allowed = options.AllowedTools;
        if (options.Skills is SkillsConfig.AllSkills && !options.AllowedTools.Contains("Skill"))
            allowed = allowed.Append("Skill");
        return GetShadowedWarning(options.PermissionMode, allowed);
    }

    /// <summary>
    /// Validate <see cref="ClaudeAgentOptions.CanUseTool"/> and route permission
    /// prompts over stdio. Shared by <c>Claude.QueryAsync</c> and
    /// <c>ClaudeSDKClient.ConnectAsync</c> so both enforce the same rules.
    /// Python: <c>_configure_can_use_tool</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Both CanUseTool and PermissionPromptToolName are set.</exception>
    internal static ClaudeAgentOptions Configure(ClaudeAgentOptions options)
    {
        if (options.CanUseTool == null)
            return options;
        if (!string.IsNullOrEmpty(options.PermissionPromptToolName))
        {
            throw new ArgumentException(
                "can_use_tool callback cannot be used with permission_prompt_tool_name. " +
                "Please use one or the other.");
        }

        // Advisory only (no throw): shadowing can be intentional.
        var warning = GetShadowedWarning(options);
        if (warning != null && EmittedWarnings.TryAdd(warning, 0))
        {
            if (options.Logger != null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(options.Logger, "{Warning}", warning);
            else
                WarningSink(warning);
        }

        return options with { PermissionPromptToolName = "stdio" };
    }
}
