// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/_internal/transport/subprocess_cli.py

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claude.AgentSdk.Transport;

/// <summary>
/// Subprocess transport implementation using Claude Code CLI.
/// </summary>
public class SubprocessTransport : ITransport
{
    private const int DefaultMaxBufferSize = 1024 * 1024; // 1MB
    private const int ReadChunkSize = 64 * 1024;
    private const string MinimumClaudeCodeVersion = "2.0.0";

    /// <summary>
    /// First Claude Code version that honors <c>client_composed</c> on user
    /// messages, which <see cref="ClaudeAgentOptions.VerbatimPrompts"/> relies on.
    /// Python: <c>VERBATIM_PROMPTS_MINIMUM_CLAUDE_CODE_VERSION</c>.
    /// </summary>
    internal const string VerbatimPromptsMinimumClaudeCodeVersion = "2.1.248";

    // Python commit f2389ec: track live subprocesses so we can terminate them
    // when the parent process exits. Mirrors Python atexit cleanup.
    private static readonly ConcurrentDictionary<int, Process> _activeChildren = new();

    // Python commit 6384c69: dedupe the unsupported-CLI-version warning per process.
    private static int _versionWarningEmitted; // Interlocked flag

    static SubprocessTransport()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillActiveChildren();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillActiveChildren();
    }

    private static void KillActiveChildren()
    {
        foreach (var (pid, proc) in _activeChildren)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { }
            _activeChildren.TryRemove(pid, out _);
        }
    }

    private readonly ClaudeAgentOptions _options;
    private string? _cliPath; // Python commit 19e1f53: deferred CLI discovery to ConnectAsync.
    private CliLaunch? _launch; // _cliPath with Windows .cmd shims resolved (see CliLauncher)
    private readonly string? _cwd;
    private readonly int _maxBufferSize;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private Task? _stderrTask;
    private CancellationTokenSource? _stderrCts;
    private bool _ready;
    private Exception? _exitError;

    public bool IsReady => _ready;

    /// <summary>
    /// Create a new subprocess transport.
    /// </summary>
    /// <param name="prompt">
    /// Unused; kept for source compatibility. The CLI always runs in streaming
    /// mode (Python parity) and prompts are written to stdin by the caller.
    /// </param>
    /// <param name="options">Configuration options.</param>
    public SubprocessTransport(object prompt, ClaudeAgentOptions options)
    {
        _options = options;
        // Python commit 19e1f53: defer CLI discovery to ConnectAsync so tests
        // and dry-run builders can construct without an installed CLI.
        _cliPath = options.CliPath;
        _cwd = options.Cwd;
        _maxBufferSize = options.MaxBufferSize ?? DefaultMaxBufferSize;
        _logger = options.Logger ?? NullLogger.Instance;
    }

    private static string FindCli()
    {
        // Check bundled CLI first
        var bundledCli = FindBundledCli();
        if (bundledCli != null)
            return bundledCli;

        // Check environment variable
        var cliPathEnv = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH");
        if (!string.IsNullOrEmpty(cliPathEnv) && File.Exists(cliPathEnv))
            return cliPathEnv;

        // Check PATH. Python: shutil.which("claude"), preferring a native
        // executable on Windows over npm's claude.cmd shim (which Launch
        // resolves to its target rather than running — see CliLauncher).
        string? whichHit = null;
        var hit = Which("claude");
        if (hit != null)
        {
            if (!OperatingSystem.IsWindows() || IsWindowsNativeExe(hit))
                return hit;
            var exe = Which("claude.exe");
            if (exe != null && IsWindowsNativeExe(exe))
                return exe;
            whichHit = hit;
        }

        // Check common locations
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Windows: the native installer's claude.exe, then npm's default global
        // prefix (its claude.cmd is resolved by CliLauncher, never run).
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var locations = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(appData, "npm", "claude.cmd") }
            : new[]
            {
                Path.Combine(home, ".npm-global", "bin", "claude"),
                "/usr/local/bin/claude",
                Path.Combine(home, ".local", "bin", "claude"),
                Path.Combine(home, "node_modules", ".bin", "claude"),
                Path.Combine(home, ".yarn", "bin", "claude"),
                Path.Combine(home, ".claude", "local", "claude"),
            };

        foreach (var path in locations)
        {
            if (File.Exists(path))
                return path;
        }

        // No native executable anywhere: return the shim; CliLauncher resolves
        // it to its target (or explains why it can't).
        if (whichHit != null)
            return whichHit;

        if (OperatingSystem.IsWindows())
        {
            throw new CliNotFoundException(
                "Claude Code not found. Install the native claude.exe with (PowerShell):\n" +
                "  irm https://claude.ai/install.ps1 | iex\n" +
                "\nOr provide the path to a claude.exe via ClaudeAgentOptions:\n" +
                "  new ClaudeAgentOptions { CliPath = @\"C:\\path\\to\\claude.exe\" }\n" +
                "\nor install with npm (npm install -g @anthropic-ai/claude-code)."
            );
        }

        throw new CliNotFoundException(
            "Claude Code not found. Install with:\n" +
            "  npm install -g @anthropic-ai/claude-code\n" +
            "\nOr provide the path via ClaudeAgentOptions:\n" +
            "  new ClaudeAgentOptions { CliPath = \"/path/to/claude\" }"
        );
    }

    private static string? FindBundledCli()
    {
        var cliName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var assemblyDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(assemblyDir)) return null;

        var bundledPath = Path.Combine(assemblyDir, "_bundled", cliName);
        return File.Exists(bundledPath) ? bundledPath : null;
    }

    /// <summary>
    /// Minimal <c>shutil.which</c>: search PATH for an executable file named
    /// <paramref name="name"/> (applying PATHEXT on Windows when the name has
    /// no extension).
    /// </summary>
    private static string? Which(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] candidates = [name];
        if (OperatingSystem.IsWindows() && !Path.HasExtension(name))
        {
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            var exts = string.IsNullOrEmpty(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt;
            candidates = exts.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => name + ext.ToLowerInvariant())
                .ToArray();
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (File.Exists(fullPath) && IsExecutable(fullPath))
                    return fullPath;
            }
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try { return (File.GetUnixFileMode(path) & anyExecute) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// Whether the path's final component names an image CreateProcess runs
    /// directly (.exe / .com). Only used to rank discovery results; not a
    /// security gate. Python: <c>_is_windows_native_exe</c>.
    /// </summary>
    internal static bool IsWindowsNativeExe(string cliPath)
    {
        var name = cliPath.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].TrimEnd('.', ' ').ToLowerInvariant();
        return name.EndsWith(".exe") || name.EndsWith(".com");
    }

    /// <summary>
    /// Whether any component of <paramref name="cliPath"/> carries a .bat/.cmd
    /// extension. Classifies every component (split on separators and on ':'
    /// for NTFS stream specs / drive prefixes) with trailing dots and spaces
    /// stripped, so Win32 path normalization can't smuggle a batch file past
    /// the check. Python: <c>_is_windows_batch_cli</c> (platform check applied
    /// by the caller so this is testable off Windows).
    /// </summary>
    internal static bool IsBatchScriptPath(string cliPath)
    {
        foreach (var component in cliPath.Replace('\\', '/').Split('/'))
        {
            foreach (var segment in component.Split(':'))
            {
                var s = segment.TrimEnd('.', ' ').ToLowerInvariant();
                if (s.EndsWith(".bat") || s.EndsWith(".cmd"))
                    return true;
            }
        }
        return false;
    }

    // cmd.exe metacharacters, the quote that toggles its quoting state, and "!"
    // (delayed expansion). Python: _CMD_EXE_METACHARACTERS.
    private const string CmdExeMetacharacters = "&|<>^%!\"";

    /// <summary>
    /// Defense in depth on Windows: reject cmd.exe metacharacters in values that
    /// applications commonly take from external input (resume / session id).
    /// Python: <c>_reject_windows_cmd_metacharacters</c>.
    /// </summary>
    internal static void RejectWindowsCmdMetacharacters(string optionName, string value, bool isWindows)
    {
        if (!isWindows)
            return;
        var bad = value.Where(c => CmdExeMetacharacters.Contains(c) || c == '\r' || c == '\n')
            .Distinct()
            .OrderBy(c => c)
            .ToArray();
        if (bad.Length > 0)
        {
            throw new ArgumentException(
                $"{optionName} value '{value}' contains characters that are unsafe to pass on a " +
                $"Windows command line: {string.Join(", ", bad.Select(c => $"'{c}'"))}");
        }
    }

    // Parentheses and commas delimit --allowedTools rules; control characters
    // (C0, DEL, C1) and U+FEFF never appear in a skill directory name.
    // Python: _SKILL_NAME_INVALID_CHARS.
    private static readonly System.Text.RegularExpressions.Regex SkillNameInvalidChars =
        new(@"[(),\x00-\x1f\x7f-\x9f﻿]");

    /// <summary>
    /// Reject skill names that can't ride safely in a <c>Skill(name)</c> rule
    /// inside the comma-separated <c>--allowedTools</c> value (e.g. a name like
    /// <c>x),Bash,Skill(y</c> would otherwise grant Bash). Python: <c>_validate_skill_name</c>.
    /// </summary>
    internal static void ValidateSkillName(string? name)
    {
        if (name is null)
            throw new ArgumentException("Skill names must be strings, got null");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Skill names must be non-empty strings");
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(name[i]))
                throw new ArgumentException(
                    $"Invalid skill name '{name}': contains an unpaired surrogate, which can never match a skill the CLI discovered.");
        }
        if (name != name.Trim())
            throw new ArgumentException(
                $"Invalid skill name '{name}': leading or trailing whitespace can never match — the Skill tool trims the invoked name.");
        if (SkillNameInvalidChars.IsMatch(name))
            throw new ArgumentException(
                $"Invalid skill name '{name}': parentheses, commas, control characters, and byte-order marks are not allowed. " +
                "Names match the skill's directory name, or 'plugin:skill' for plugin-qualified skills.");
        if (name == "*")
            throw new ArgumentException("Invalid skill name '*': use Skills = \"all\" to enable every skill.");
        if (name.EndsWith(":*") || name.EndsWith(" *"))
            throw new ArgumentException(
                $"Invalid skill name '{name}': wildcard-suffix names are not allowed; list each skill by its exact name.");
        if (name.StartsWith('/'))
            throw new ArgumentException(
                $"Invalid skill name '{name}': skill names may not start with '/'. The skills option takes the canonical name, not the slash-command form.");
        if (name.Contains(@"\\"))
            throw new ArgumentException(
                $"Invalid skill name '{name}': consecutive backslashes are not allowed — the per-rule parser collapses them, so the rule would name a different skill.");
        if (name.EndsWith('\\'))
            throw new ArgumentException($"Invalid skill name '{name}': names may not end with an unpaired backslash.");
    }

    /// <summary>
    /// The explicit skill allowlist, or <c>null</c> for unset / <c>"all"</c>
    /// ('all' and omitted are equivalent at the wire level: no filter). Bare
    /// strings other than "all" are already rejected when converted to
    /// <see cref="SkillsConfig"/> (Python: <c>_reject_non_list_skills</c>).
    /// </summary>
    internal static IReadOnlyList<string>? NormalizeSkills(SkillsConfig? skills) => skills switch
    {
        SkillsConfig.Named named => named.Names ?? throw new ArgumentException(
            "ClaudeAgentOptions.Skills must be a list of skill names or \"all\", got null."),
        _ => null
    };

    /// <summary>
    /// Python commit 1c26bd3 + e621929: compute effective allowed_tools and
    /// setting_sources from Skills option.
    /// </summary>
    /// <remarks>
    /// When Skills == "all" → inject bare "Skill" tool. When Skills is a list
    /// → inject Skill(name) for each entry. setting_sources defaults to
    /// ["user","project"] when unset so CLI discovers installed skills.
    /// Skills==null is a no-op.
    /// </remarks>
    internal (List<string> AllowedTools, List<string>? SettingSources) ApplySkillsDefaults()
    {
        var allowedTools = new List<string>(_options.AllowedTools);
        List<string>? settingSources = _options.SettingSources != null
            ? _options.SettingSources.Select(s => s.ToString().ToLowerInvariant()).ToList()
            : null;

        if (_options.Skills == null)
            return (allowedTools, settingSources);

        var names = NormalizeSkills(_options.Skills);
        if (names == null)
        {
            if (!allowedTools.Contains("Skill"))
                allowedTools.Add("Skill");
        }
        else
        {
            foreach (var name in names)
            {
                ValidateSkillName(name);
                var pattern = $"Skill({name})";
                if (!allowedTools.Contains(pattern))
                    allowedTools.Add(pattern);
            }
        }

        settingSources ??= new List<string> { "user", "project" };
        return (allowedTools, settingSources);
    }

    /// <summary>
    /// CLI value for a permission mode. Covers every Python <c>PermissionMode</c>
    /// literal, including <c>dontAsk</c> and <c>auto</c>.
    /// </summary>
    internal static string PermissionModeToCliValue(PermissionMode mode) => mode switch
    {
        PermissionMode.Default => "default",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.DontAsk => "dontAsk",
        PermissionMode.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported permission mode")
    };

    private string? BuildSettingsValue()
    {
        var hasSettings = _options.Settings != null;
        var hasSandbox = _options.Sandbox != null;

        if (!hasSettings && !hasSandbox)
            return null;

        if (hasSettings && !hasSandbox)
            return _options.Settings;

        // TS (CG): an enabled sandbox fails closed unless the caller opted out.
        var sandbox = _options.Sandbox!;
        if (sandbox.Enabled == true && sandbox.FailIfUnavailable is null)
            sandbox = sandbox with { FailIfUnavailable = true };

        // Merge settings with sandbox
        var settingsObj = new Dictionary<string, object?>();

        if (hasSettings)
        {
            var settingsStr = _options.Settings!.Trim();
            if (settingsStr.StartsWith('{') && settingsStr.EndsWith('}'))
            {
                try
                {
                    var parsed = ParseSettingsObject(settingsStr);
                    if (parsed != null)
                        settingsObj = parsed;
                }
                catch (JsonException)
                {
                    // Try as file path
                    if (File.Exists(settingsStr))
                    {
                        var content = File.ReadAllText(settingsStr);
                        var parsed = ParseSettingsObject(content);
                        if (parsed != null)
                            settingsObj = parsed;
                    }
                }
            }
            else if (File.Exists(settingsStr))
            {
                var content = File.ReadAllText(settingsStr);
                var parsed = ParseSettingsObject(content);
                if (parsed != null)
                    settingsObj = parsed;
            }
        }

        settingsObj["sandbox"] = sandbox;

        return SdkJson.Serialize(settingsObj);
    }

    private static Dictionary<string, object?>? ParseSettingsObject(string json)
    {
        var parsed = JsonSerializer.Deserialize(json, SdkJsonContext.Default.DictionaryStringJsonElement);
        return parsed?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
    }

    /// <summary>
    /// The process to start for the CLI: discovered on first use, then run
    /// through <see cref="CliLauncher"/> so a Windows npm shim launches its
    /// target directly instead of via cmd.exe.
    /// </summary>
    private CliLaunch Launch()
    {
        _cliPath ??= FindCli();
        return _launch ??= CliLauncher.Resolve(_cliPath, LaunchPath());
    }

    /// <summary>PATH the child will see (options.Env overrides this process's).</summary>
    private string? LaunchPath() =>
        _options.Env.FirstOrDefault(kv => string.Equals(kv.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;

    internal List<string> BuildCommand()
    {
        var cmd = Launch().Command(["--output-format", "stream-json", "--verbose"]);

        // System prompt (Python _build_command). The preset's
        // exclude_dynamic_sections / snapshot and the custom form's snapshot
        // ride in the initialize request, never on argv.
        switch (_options.SystemPrompt)
        {
            case null:
                cmd.AddRange(["--system-prompt", ""]);
                break;
            case SystemPromptText text:
                cmd.AddRange(["--system-prompt", text.Text]);
                break;
            case SystemPromptFile spFile:
                cmd.AddRange(["--system-prompt-file", spFile.Path]);
                break;
            case SystemPromptCustom custom:
                // The custom form reaches the CLI the same way a plain string does.
                cmd.AddRange(["--system-prompt", custom.Prompt]);
                break;
            case SystemPromptPreset { Append: { } append }:
                cmd.AddRange(["--append-system-prompt", append]);
                break;
            case SystemPromptBlocks:
                // No argv form: the blocks ride in the initialize request (TS parity).
                break;
        }

        // Tools
        if (_options.Tools != null)
        {
            if (_options.Tools.Count == 0)
                cmd.AddRange(["--tools", ""]);
            else
                cmd.AddRange(["--tools", string.Join(",", _options.Tools)]);
        }
        else if (_options.ToolsPreset != null)
        {
            // Python: the 'claude_code' tools preset maps to `--tools default`.
            cmd.AddRange(["--tools", "default"]);
        }

        // Python commit 1c26bd3 + e621929: effective allowedTools and
        // setting-sources account for the Skills option.
        var (effectiveAllowedTools, effectiveSettingSources) = ApplySkillsDefaults();

        if (effectiveAllowedTools.Count > 0)
            cmd.AddRange(["--allowedTools", string.Join(",", effectiveAllowedTools)]);

        // Python: `if self._options.max_turns:` -- 0 is omitted.
        if (_options.MaxTurns is { } maxTurns and not 0)
            cmd.AddRange(["--max-turns", maxTurns.ToString(CultureInfo.InvariantCulture)]);

        if (_options.MaxBudgetUsd.HasValue)
            cmd.AddRange(["--max-budget-usd", _options.MaxBudgetUsd.Value.ToString(CultureInfo.InvariantCulture)]);

        if (_options.DisallowedTools.Count > 0)
            cmd.AddRange(["--disallowedTools", string.Join(",", _options.DisallowedTools)]);

        // Python commit 2e60cec: --task-budget <total>.
        if (_options.TaskBudget != null)
            cmd.AddRange(["--task-budget", _options.TaskBudget.Total.ToString(CultureInfo.InvariantCulture)]);

        if (!string.IsNullOrEmpty(_options.Model))
            cmd.AddRange(["--model", _options.Model]);

        // TS: main-thread agent.
        if (!string.IsNullOrEmpty(_options.Agent))
            cmd.AddRange(["--agent", _options.Agent]);

        if (!string.IsNullOrEmpty(_options.FallbackModel))
        {
            // TS rejects a fallback identical to the main model.
            if (!string.IsNullOrEmpty(_options.Model) && _options.FallbackModel == _options.Model)
                throw new ArgumentException(
                    "Fallback model cannot be the same as the main model. Please specify a different model for FallbackModel.");
            cmd.AddRange(["--fallback-model", _options.FallbackModel]);
        }

        // TS: --debug-file takes precedence over --debug.
        if (!string.IsNullOrEmpty(_options.DebugFile))
            cmd.AddRange(["--debug-file", _options.DebugFile]);
        else if (_options.Debug)
            cmd.Add("--debug");

        if (_options.Betas.Count > 0)
            cmd.AddRange(["--betas", string.Join(",", _options.Betas)]);

        var permissionPromptToolName = _options.PermissionPromptToolName;
        if (permissionPromptToolName == null && _options.CanUseTool != null)
            permissionPromptToolName = "stdio";

        if (permissionPromptToolName != null)
            cmd.AddRange(["--permission-prompt-tool", permissionPromptToolName]);

        if (_options.PermissionPrompts is { } prompts)
            cmd.AddRange(["--permission-prompts", prompts == PermissionPromptsMode.Host ? "host" : "none"]);

        if (_options.PermissionMode.HasValue)
            cmd.AddRange(["--permission-mode", PermissionModeToCliValue(_options.PermissionMode.Value)]);

        if (_options.AllowDangerouslySkipPermissions)
            cmd.Add("--allow-dangerously-skip-permissions");

        if (_options.ContinueConversation)
            cmd.Add("--continue");

        // Pass these as --flag=value rather than as two argv tokens. The CLI
        // declares --resume with an optional value, so in the two-token form a
        // dash-leading value is not bound to the flag and is instead parsed as
        // a separate CLI flag -- letting an untrusted value inject arbitrary
        // flags. The equals form always binds the value to the flag. (Python parity.)
        if (!string.IsNullOrEmpty(_options.Resume))
        {
            RejectWindowsCmdMetacharacters("Resume", _options.Resume, OperatingSystem.IsWindows());
            cmd.Add($"--resume={_options.Resume}");
        }

        // Python commit 5656d20: --session-id forwarding.
        if (!string.IsNullOrEmpty(_options.SessionId))
        {
            RejectWindowsCmdMetacharacters("SessionId", _options.SessionId, OperatingSystem.IsWindows());
            cmd.Add($"--session-id={_options.SessionId}");
        }

        var settingsValue = BuildSettingsValue();
        if (settingsValue != null)
            cmd.AddRange(["--settings", settingsValue]);

        foreach (var dir in _options.AddDirs)
            cmd.AddRange(["--add-dir", dir]);

        // TS: equals form (worktree support: settings, .mcp.json and .claude
        // come from the trusted root).
        if (_options.ProjectConfigRoot != null)
            cmd.Add($"--project-config-root={_options.ProjectConfigRoot}");

        if (_options.ManagedSettings is { } managed)
            cmd.AddRange(["--managed-settings",
                managed.ValueKind == JsonValueKind.String ? managed.GetString()! : managed.GetRawText()]);

        if (!_options.PersistSession)
            cmd.Add("--no-session-persistence");

        if (_options.Channels is { Count: > 0 } channels)
        {
            foreach (var channel in channels)
                AddFlagValue(cmd, "channels", channel);
        }

        // MCP servers. Python: `if self._options.mcp_servers:` -- an empty map
        // (or empty path) emits nothing.
        switch (_options.McpServers)
        {
            case McpServersConfig.ServerMap { Servers.Count: > 0 } map:
            {
                // Strip the "instance" field from SDK server configs before
                // serializing — the live SdkMcpServerConfig.Handlers list is
                // delegate state that must not cross the process boundary.
                var serversForCli = new Dictionary<string, object>();
                foreach (var (name, config) in map.Servers)
                {
                    if (config is McpSdkServerConfig sdk)
                    {
                        serversForCli[name] = new Dictionary<string, object?>
                        {
                            ["type"] = "sdk",
                            ["name"] = sdk.Name
                        };
                    }
                    else
                    {
                        serversForCli[name] = config;
                    }
                }
                var mcpConfig = new Dictionary<string, object?> { ["mcpServers"] = serversForCli };
                cmd.AddRange(["--mcp-config", SdkJson.Serialize(mcpConfig)]);
                break;
            }
            case McpServersConfig.ConfigPath { Value.Length: > 0 } path:
                // String or path form: passed through as a file path or JSON string.
                cmd.AddRange(["--mcp-config", path.Value]);
                break;
        }

        if (_options.IncludePartialMessages)
            cmd.Add("--include-partial-messages");

        // Python commit c1182a4.
        if (_options.IncludeHookEvents)
            cmd.Add("--include-hook-events");

        // Python commit 32bcc4e.
        if (_options.StrictMcpConfig)
            cmd.Add("--strict-mcp-config");

        if (_options.ForkSession)
            cmd.Add("--fork-session");

        // Equals form so the value can never be parsed as a separate flag, even
        // if the CLI's declaration of these options ever changes (Python parity).
        if (!string.IsNullOrEmpty(_options.ResumeSessionAt))
        {
            RejectWindowsCmdMetacharacters("ResumeSessionAt", _options.ResumeSessionAt, OperatingSystem.IsWindows());
            cmd.Add($"--resume-session-at={_options.ResumeSessionAt}");
        }

        // `is not null`, not truthiness: an empty string is forwarded so the CLI
        // rejects it as a malformed declaration instead of the SDK silently
        // disarming the guard the caller believes is armed (Python parity).
        if (_options.ResumeDropsTurn is not null)
        {
            RejectWindowsCmdMetacharacters("ResumeDropsTurn", _options.ResumeDropsTurn, OperatingSystem.IsWindows());
            cmd.Add($"--resume-drops-turn={_options.ResumeDropsTurn}");
        }

        // Python commit 6e3d54f: session mirroring flag (paired with SessionStore).
        if (_options.SessionStore != null)
            cmd.Add("--session-mirror");

        // Agents are sent via the initialize request body, not as a CLI flag
        // (Python commit 7c6902b — matches TypeScript SDK). See QueryHandler.InitializeAsync.

        if (effectiveSettingSources != null)
        {
            // Python commits ab9bcab / e621929: pass through as `--setting-sources=a,b`
            // (single arg). Empty list disables filesystem settings; null means omit
            // the flag entirely.
            cmd.Add($"--setting-sources={string.Join(",", effectiveSettingSources)}");
        }

        if (_options.PluginDelivery == PluginDelivery.Initialize && _options.Plugins.Count > 0)
        {
            // TS pluginDelivery 'initialize': the plugins ride in the initialize
            // request; the CLI waits for it before loading them.
            foreach (var plugin in _options.Plugins)
            {
                if (plugin.Type != "local")
                    throw new ArgumentException($"Unsupported plugin type: {plugin.Type}");
            }
            cmd.Add("--await-initialize");
        }
        else
        {
            foreach (var plugin in _options.Plugins)
            {
                if (plugin.Type == "local")
                    cmd.AddRange([plugin.SkipMcpDiscovery == true ? "--plugin-dir-no-mcp" : "--plugin-dir", plugin.Path]);
                else
                    throw new ArgumentException($"Unsupported plugin type: {plugin.Type}"); // Python: ValueError
            }
        }

        // TS routes `workload` through the extra-args map.
        var extraArgs = _options.Workload is { } workload
            ? new Dictionary<string, string?>(_options.ExtraArgs) { ["workload"] = workload }
            : _options.ExtraArgs;
        foreach (var (flag, value) in extraArgs)
        {
            if (value == null)
                cmd.Add($"--{flag}");
            else
                AddFlagValue(cmd, flag, value);
        }

        // Python commit 6617b9e: emit `--thinking adaptive` / `--thinking disabled`
        // for those variants (not just --max-thinking-tokens). Python commit 32f09c1:
        // forward `--thinking-display` for adaptive/enabled variants.
#pragma warning disable CS0618 // MaxThinkingTokens is obsolete
        if (_options.Thinking is not null)
        {
            switch (_options.Thinking)
            {
                case ThinkingConfigAdaptive:
                    cmd.AddRange(["--thinking", "adaptive"]);
                    break;
                case ThinkingConfigEnabled { BudgetTokens: { } budget }:
                    cmd.AddRange(["--max-thinking-tokens", budget.ToString(CultureInfo.InvariantCulture)]);
                    break;
                case ThinkingConfigEnabled:
                    // TS: an enabled config without a budget means adaptive.
                    cmd.AddRange(["--thinking", "adaptive"]);
                    break;
                case ThinkingConfigDisabled:
                    cmd.AddRange(["--thinking", "disabled"]);
                    break;
            }

            if (_options.Thinking is not ThinkingConfigDisabled &&
                _options.Thinking.Display is { } display)
            {
                cmd.AddRange(["--thinking-display", display]);
            }
        }
        else if (_options.MaxThinkingTokens.HasValue)
        {
            cmd.AddRange(["--max-thinking-tokens", _options.MaxThinkingTokens.Value.ToString()]);
        }
#pragma warning restore CS0618

        if (_options.Effort.HasValue)
            cmd.AddRange(["--effort", _options.Effort.Value.ToJsonString()]);

        if (JsonSchemaOf(_options.OutputFormat) is { } schema)
            cmd.AddRange(["--json-schema", schema.GetRawText()]);

        // Always use streaming mode with stdin (matching Python/TypeScript SDKs).
        // The prompt is written to stdin as a user message, never placed on the
        // command line, and agents/skills ride in the initialize request.
        cmd.AddRange(["--input-format", "stream-json"]);

        return cmd;
    }

    /// <summary>
    /// The schema of a <c>{"type":"json_schema","schema":...}</c> output format,
    /// or null. Sent as <c>--json-schema</c> and (TS parity) initialize <c>jsonSchema</c>.
    /// </summary>
    internal static JsonElement? JsonSchemaOf(JsonElement? outputFormat)
    {
        if (outputFormat is { ValueKind: JsonValueKind.Object } format &&
            format.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            typeElement.GetString() == "json_schema" &&
            format.TryGetProperty("schema", out var schema))
            return schema;
        return null;
    }

    /// <summary>
    /// <c>--flag value</c>, or <c>--flag=value</c> when the value starts with a
    /// dash: in the two-token form a dash-leading value is not bound to its flag
    /// when the CLI declares the option with an optional value -- it parses as
    /// a separate flag instead. The equals form always binds. (TS: <c>Kx</c>.)
    /// </summary>
    private static void AddFlagValue(List<string> cmd, string flag, string value)
    {
        if (value.StartsWith('-'))
            cmd.Add($"--{flag}={value}");
        else
            cmd.AddRange([$"--{flag}", value]);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_process != null)
            return;

        // Python passes `user=` to process creation (setuid on POSIX). .NET's
        // Process API can't switch users on Unix, so fail loudly instead of
        // silently running the CLI with the host's privileges.
        if (_options.User != null)
        {
            throw new NotSupportedException(
                "ClaudeAgentOptions.User is not supported by the .NET SDK: the CLI would run as the " +
                "current user. Run the host process as the desired user instead.");
        }

        // Python commit 19e1f53: defer CLI discovery to ConnectAsync.
        _cliPath ??= await Task.Run(FindCli, cancellationToken);

        // Resolve (or refuse) a Windows batch shim before anything is spawned
        // with it -- this guards the version probe below as well as the main spawn.
        Launch();

        // Check CLI version
        if (Environment.GetEnvironmentVariable("CLAUDE_AGENT_SDK_SKIP_VERSION_CHECK") == null)
            await CheckClaudeVersionAsync(cancellationToken);

        var cmd = BuildCommand();

        var shouldReadStderr = _options.StderrCallback != null ||
                               _options.ExtraArgs.ContainsKey("debug-to-stderr");

        var startInfo = new ProcessStartInfo
        {
            FileName = cmd[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = shouldReadStderr,
            CreateNoWindow = true
        };

        // Add arguments
        for (int i = 1; i < cmd.Count; i++)
            startInfo.ArgumentList.Add(cmd[i]);

        // Set working directory
        if (_cwd != null)
            startInfo.WorkingDirectory = _cwd;

        BuildEnvironment(startInfo.Environment);

        try
        {
            _process = Process.Start(startInfo)
                ?? throw new CliConnectionException("Failed to start Claude Code process");

            // Python commit f2389ec: track for parent-exit cleanup.
            _activeChildren[_process.Id] = _process;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                // Flag names only: values can carry prompts, settings JSON and MCP secrets.
                var flags = cmd.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal))
                    .Select(a => a.Split('=', 2)[0]);
                _logger.LogDebug("Started Claude Code CLI {CliPath} (pid {Pid}) with flags {Flags}",
                    _cliPath, _process.Id, string.Join(" ", flags));
            }

            // Always streaming (Python parity): stdin stays open for stream-json input.
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
            if (shouldReadStderr)
            {
                _stderr = _process.StandardError;
                // Own token, not the caller's connect token: stderr must keep
                // draining for the life of the process or the CLI blocks on a
                // full pipe. Cancelled in CloseAsync.
                _stderrCts = new CancellationTokenSource();
                var stderrToken = _stderrCts.Token;
                _stderrTask = Task.Run(() => HandleStderrAsync(stderrToken), CancellationToken.None);
            }

            _ready = true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (_cwd != null && !Directory.Exists(_cwd))
            {
                _exitError = new CliConnectionException($"Working directory does not exist: {_cwd}", ex);
                throw _exitError;
            }
            // ENOENT (2) / ERROR_FILE_NOT_FOUND (2), ERROR_PATH_NOT_FOUND (3)
            _exitError = ex.NativeErrorCode is 2 or 3
                ? new CliNotFoundException($"Claude Code not found at: {_cliPath}", _cliPath)
                : new CliConnectionException($"Failed to start Claude Code: {ex.Message}", ex);
            throw _exitError;
        }
        catch (Exception ex) when (ex is not CliConnectionException)
        {
            _exitError = new CliConnectionException($"Failed to start Claude Code: {ex.Message}", ex);
            throw _exitError;
        }
    }

    /// <summary>
    /// Build the CLI's environment in place: <paramref name="env"/> starts as the
    /// inherited environment (cleared first when
    /// <see cref="ClaudeAgentOptions.InheritEnvironment"/> is false).
    /// </summary>
    internal void BuildEnvironment(IDictionary<string, string?> env)
    {
        // TS semantics: `env` replaces process.env instead of merging over it.
        if (!_options.InheritEnvironment)
            env.Clear();

        // Python commit 5839ff9: filter out CLAUDECODE so SDK-spawned subprocesses
        // don't think they're running inside a Claude Code parent.
        env.Remove("CLAUDECODE");

        // Set environment
        foreach (var (key, value) in _options.Env)
            env[key] = value;

        // Python commit 6d77aef: default CLAUDE_CODE_ENTRYPOINT only if absent
        // (caller-provided value via options.Env wins).
        if (!_options.Env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"))
            env["CLAUDE_CODE_ENTRYPOINT"] = "sdk-dotnet";

        env["CLAUDE_AGENT_SDK_VERSION"] =
            GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0";

        // Python commit bbec84d: propagate W3C trace context (TRACEPARENT/TRACESTATE)
        // from the current Activity to the subprocess. No-op when there's no active
        // Activity; options.Env always wins.
        var activity = System.Diagnostics.Activity.Current;
        if (activity != null && !string.IsNullOrEmpty(activity.Id))
        {
            if (!_options.Env.ContainsKey("TRACEPARENT"))
                env["TRACEPARENT"] = activity.Id;
            else if (_options.Env.TryGetValue("TRACEPARENT", out var tp))
                env["TRACEPARENT"] = tp;

            var tracestate = activity.TraceStateString;
            if (!_options.Env.ContainsKey("TRACESTATE"))
            {
                if (!string.IsNullOrEmpty(tracestate))
                    env["TRACESTATE"] = tracestate;
                else
                    env.Remove("TRACESTATE");
            }
        }

        // Python 0.2.160: the query waits for the CLI's session_state_changed
        // "idle" before closing stdin on a run that serves control requests.
        // Ask for the frames it drops (sdk_host_only) unless the caller chose a
        // value, in any case.
        if (!env.Keys.Any(k => string.Equals(k, SdkReadsSessionStateEnv, StringComparison.OrdinalIgnoreCase)))
            env[SdkReadsSessionStateEnv] = "1";

        if (_options.EnableFileCheckpointing)
            env["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true";

        // TS: advertise the SDK-side token refresh callbacks.
        if (_options.GetOAuthToken != null)
            env["CLAUDE_CODE_SDK_HAS_OAUTH_REFRESH"] = "1";
        if (_options.GetHostAuthToken != null)
            env["CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"] = "1";

        // TS toolConfig.askUserQuestion.
        var ask = _options.ToolConfig?.AskUserQuestion;
        if (ask?.PreviewFormat is { } preview)
            env["CLAUDE_CODE_QUESTION_PREVIEW_FORMAT"] = preview == AskUserQuestionPreviewFormat.Html ? "html" : "markdown";
        ApplyFlagEnv(env, ask?.ExtendedQuestions, "CLAUDE_CODE_QUESTION_EXTENDED");
        ApplyFlagEnv(env, ask?.OptionalDescriptions, "CLAUDE_CODE_QUESTION_OPTIONAL_DESCRIPTIONS");

        if (_cwd != null)
            env["PWD"] = _cwd;
    }

    /// <summary>
    /// TS: a true flag sets <c>KEY=1</c>; otherwise an inherited value is removed
    /// (case-insensitively) unless the caller set it in <see cref="ClaudeAgentOptions.Env"/>.
    /// </summary>
    private void ApplyFlagEnv(IDictionary<string, string?> env, bool? flag, string key)
    {
        if (flag == true)
        {
            env[key] = "1";
            return;
        }
        if (_options.Env.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            return;
        foreach (var k in env.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            env.Remove(k);
    }

    /// <summary>Python: <c>_SDK_READS_SESSION_STATE_ENV</c>.</summary>
    internal const string SdkReadsSessionStateEnv = "CLAUDE_CODE_SDK_READS_SESSION_STATE";

    private async Task HandleStderrAsync(CancellationToken cancellationToken)
    {
        if (_stderr == null) return;

        void Emit(string line)
        {
            line = line.TrimEnd();
            if (line.Length == 0)
                return;

            // Python commit 6bbad5f: isolate per-line so a raise in the user's
            // callback doesn't terminate the loop and silently drop every
            // subsequent line for the rest of the session.
            if (_options.StderrCallback != null)
            {
                try
                {
                    _options.StderrCallback(line);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "StderrCallback threw; continuing");
                }
            }
            _logger.LogTrace("CLI stderr: {Line}", line);
        }

        // Frame lines out of chunks so a producer that never emits a newline
        // can't grow the buffer without bound (Python parity).
        var framer = new LineFramer();
        var buffer = new char[ReadChunkSize];
        try
        {
            while (true)
            {
                var n = await _stderr.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (n == 0) break;
                foreach (var line in framer.Push(buffer.AsSpan(0, n)))
                    Emit(line);
                if (framer.PendingLength > _maxBufferSize)
                    Emit(framer.Flush());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI stderr stream read failed");
        }
        finally
        {
            // The last partial line is exactly what the caller needs when the CLI stalled.
            Emit(framer.Flush());
        }
    }

    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_ready || _stdin == null)
                throw new CliConnectionException("Transport is not ready for writing");

            if (_process?.HasExited == true)
                throw new CliConnectionException($"Cannot write to terminated process (exit code: {_process.ExitCode})");

            if (_exitError != null)
                throw new CliConnectionException($"Cannot write to process that exited with error", _exitError);

            await _stdin.WriteAsync(data.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not CliConnectionException)
        {
            _ready = false;
            _exitError = new CliConnectionException($"Failed to write to process stdin: {ex.Message}", ex);
            throw _exitError;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_stdin != null)
            {
                try { _stdin.Close(); } catch { }
                _stdin = null;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Parse one complete line of the CLI's NDJSON stdout. Returns null for lines
    /// that carry no message (blank lines, non-JSON output such as
    /// <c>[SandboxDebug] ...</c>). A line that looks like JSON but does not parse
    /// is corrupt — with proper line framing no later data could complete it — so
    /// it throws rather than silently dropping (or swallowing later) messages.
    /// Python: <c>_parse_stdout_line</c>.
    /// </summary>
    internal static JsonElement? ParseStdoutLine(string line) => StreamJsonReader.ParseLine(line);

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_process == null || _stdout == null)
            throw new CliConnectionException("Not connected");

        // The CLI writes NDJSON: one message per line, bounded while being received.
        var messages = StreamJsonReader.ReadAsync(_stdout, _maxBufferSize, _logger, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var cancelled = false;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await messages.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    // Consumer disconnected: don't fall through to the exit-code
                    // check (the process is still running).
                    hasNext = false;
                    cancelled = true;
                }
                if (!hasNext)
                    break;
                yield return messages.Current;
            }
        }
        finally
        {
            await messages.DisposeAsync();
        }

        if (cancelled)
            yield break;

        // Check process exit
        try
        {
            await _process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        _logger.LogDebug("Claude Code CLI exited with code {ExitCode}", _process.ExitCode);
        if (_process.ExitCode != 0)
        {
            _exitError = new ProcessException(
                $"Command failed with exit code {_process.ExitCode}",
                _process.ExitCode,
                "Check stderr output for details"
            );
            throw _exitError;
        }
    }

    /// <summary>
    /// User-facing warning: to the configured logger, or stderr when none is set
    /// (Python logs via logger.warning, which reaches stderr by default).
    /// </summary>
    private void WarnUser(string message)
    {
        if (_options.Logger != null)
            _logger.LogWarning("{Warning}", message);
        else
            Console.Error.WriteLine($"Warning: {message}");
    }

    private async Task CheckClaudeVersionAsync(CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var startInfo = new ProcessStartInfo
            {
                FileName = Launch().FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in Launch().Command(["-v"]).Skip(1))
                startInfo.ArgumentList.Add(arg);

            process = Process.Start(startInfo);
            if (process == null) return;

            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+\.\d+\.\d+)");
            if (match.Success)
            {
                var version = Version.Parse(match.Groups[1].Value);
                var minVersion = Version.Parse(MinimumClaudeCodeVersion);

                if (version < minVersion)
                {
                    // Python commit 6384c69: dedupe the warning per process.
                    if (Interlocked.Exchange(ref _versionWarningEmitted, 1) == 0)
                    {
                        WarnUser(
                            $"Claude Code version {version} is unsupported in the Agent SDK. " +
                            $"Minimum required version is {MinimumClaudeCodeVersion}. " +
                            "Some features may not work correctly."
                        );
                    }
                }

                var verbatimWarning = GetVerbatimPromptsVersionWarning(
                    _options.VerbatimPrompts, match.Groups[1].Value, _cliPath);
                if (verbatimWarning != null)
                    WarnUser(verbatimWarning);
            }
        }
        catch (Exception)
        {
            // Ignore version check failures
        }
        finally
        {
            if (process != null)
            {
                // Don't leave a hung probe behind after the timeout (Python parity).
                try { if (!process.HasExited) process.Kill(); } catch { }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Warning for <see cref="ClaudeAgentOptions.VerbatimPrompts"/> on a CLI that
    /// predates <c>client_composed</c> (and so still expands <c>@path</c>
    /// mentions and dispatches slash commands), or <c>null</c>. Python:
    /// <c>_check_claude_version</c> with
    /// <c>VERBATIM_PROMPTS_MINIMUM_CLAUDE_CODE_VERSION</c>.
    /// </summary>
    internal static string? GetVerbatimPromptsVersionWarning(bool verbatimPrompts, string cliVersion, string? cliPath)
    {
        if (!verbatimPrompts || !Version.TryParse(cliVersion, out var version))
            return null;
        if (version >= Version.Parse(VerbatimPromptsMinimumClaudeCodeVersion))
            return null;
        return $"VerbatimPrompts is enabled, but Claude Code version {cliVersion} at {cliPath} ignores it: " +
               "prompts will still have @path mentions expanded and slash commands dispatched. " +
               $"Claude Code {VerbatimPromptsMinimumClaudeCodeVersion} or later is required.";
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    private const int SIGTERM = 15;

    /// <summary>
    /// SIGTERM on POSIX so the CLI can clean up its own children and flush its
    /// session file; TerminateProcess on Windows (Python's terminate()).
    /// </summary>
    private static void Terminate(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            process.Kill();
            return;
        }
        try
        {
            SysKill(process.Id, SIGTERM);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            process.Kill();
        }
    }

    public async Task CloseAsync()
    {
        if (_process == null)
        {
            _ready = false;
            return;
        }

        // Stop the stderr reader (it flushes its last partial line on the way out).
        if (_stderrTask != null)
        {
            try { _stderrCts?.Cancel(); } catch { }
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
            _stderrTask = null;
        }
        _stderrCts?.Dispose();
        _stderrCts = null;

        // Close stdin (hold the write lock to prevent a race with concurrent
        // writes). Bounded: a writer blocked on a full stdin pipe must not pin
        // close forever (Python parity: 5s).
        var lockHeld = await _writeLock.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            _ready = false;
            if (_stdin != null)
            {
                try { _stdin.Close(); } catch { }
                _stdin = null;
            }
        }
        finally
        {
            if (lockHeld)
                _writeLock.Release();
        }

        // Python commit 40cc6f5: wait for graceful shutdown after stdin EOF
        // (the CLI flushes its session file then), SIGTERM if it doesn't exit,
        // force kill if SIGTERM doesn't take.
        var exited = false;
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    using var gracefulCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _process.WaitForExitAsync(gracefulCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { Terminate(_process); } catch { }
                    try
                    {
                        using var termCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _process.WaitForExitAsync(termCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { _process.Kill(entireProcessTree: true); } catch { }
                        try
                        {
                            using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await _process.WaitForExitAsync(killCts.Token);
                        }
                        catch { }
                    }
                }
            }
            exited = _process.HasExited;
        }
        catch
        {
            exited = false;
        }
        finally
        {
            // Only stop tracking a child we actually reaped. A still-running
            // process stays in the set (undisposed) so the parent-exit reaper
            // gets a chance at it.
            if (exited)
            {
                _activeChildren.TryRemove(_process.Id, out _);
                _process.Dispose();
            }
        }

        _process = null;
        _stdout = null;
        _stderr = null;
        _exitError = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _writeLock.Dispose();
    }
}
