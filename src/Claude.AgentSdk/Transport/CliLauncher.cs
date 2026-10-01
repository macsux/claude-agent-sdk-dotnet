using System.Text.RegularExpressions;

namespace Claude.AgentSdk.Transport;

/// <summary>
/// The process to start for a Claude Code CLI path: <see cref="FileName"/> followed by
/// <see cref="PrefixArgs"/>, then the CLI's own arguments.
/// </summary>
public sealed record CliLaunch(string FileName, IReadOnlyList<string> PrefixArgs)
{
    /// <summary><see cref="FileName"/>, <see cref="PrefixArgs"/>, then <paramref name="args"/>.</summary>
    public List<string> Command(IEnumerable<string> args) => [FileName, .. PrefixArgs, .. args];
}

/// <summary>
/// Turns a CLI path into a <see cref="CliLaunch"/> that never runs through cmd.exe.
/// </summary>
/// <remarks>
/// On Windows, npm/pnpm/yarn installs put a <c>claude.cmd</c> shim on PATH. CreateProcess runs
/// batch files via <c>cmd.exe /c</c>, which re-parses the command line, so arguments (prompts,
/// titles) could inject commands (CVE-2024-27980 "BatBadBut" class). Instead of running the shim
/// we read it and start what it would have started: the package's native <c>claude.exe</c>, or
/// <c>node cli.js</c>. Only a shim that cannot be resolved is refused.
/// </remarks>
public static class CliLauncher
{
    // A shim target: "%dp0%\…", "%~dp0\…" or "%~dp0…" ending in a launchable extension. The run
    // line comes last in every cmd-shim flavour (npm, pnpm, yarn), so the last match is the target;
    // earlier matches are the "%dp0%\node.exe" probe.
    private static readonly Regex ShimTarget = new(
        @"%~?dp0%?\\?(?<path>[^""%*\r\n]+?\.(?:exe|com|cmd|bat|js|cjs|mjs))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int MaxShimDepth = 4;

    /// <summary>
    /// Resolve <paramref name="cliPath"/>. Off Windows, or for anything but a .bat/.cmd file, it is
    /// launched as is. <paramref name="pathEnv"/> (PATH to search for node; default: this process's)
    /// is only consulted for script targets.
    /// </summary>
    /// <exception cref="CliConnectionException">A batch file that is not a recognisable shim.</exception>
    /// <exception cref="CliNotFoundException">The shim targets a JS entry point and node cannot be found.</exception>
    public static CliLaunch Resolve(string cliPath, string? pathEnv = null) =>
        Resolve(cliPath, pathEnv, OperatingSystem.IsWindows());

    internal static CliLaunch Resolve(string cliPath, string? pathEnv, bool isWindows)
    {
        if (!isWindows || !SubprocessTransport.IsBatchScriptPath(cliPath))
            return new CliLaunch(cliPath, []);
        if (!File.Exists(cliPath))
            throw new CliNotFoundException($"Claude Code not found at: {cliPath}", cliPath);

        var shim = cliPath;
        for (var depth = 0; depth < MaxShimDepth; depth++)
        {
            var target = ShimTargetOf(shim);
            if (target is null) break;
            if (SubprocessTransport.IsBatchScriptPath(target)) { shim = target; continue; }
            if (SubprocessTransport.IsWindowsNativeExe(target)) return new CliLaunch(target, []);
            return new CliLaunch(FindNode(Path.GetDirectoryName(shim)!, pathEnv, target), [target]);
        }

        throw new CliConnectionException(
            $"Refusing to execute batch script '{cliPath}': it is not a recognisable npm/pnpm/yarn shim, " +
            "and Windows runs .bat/.cmd files via cmd.exe, which can execute commands injected through CLI " +
            "arguments. Point ClaudeAgentOptions.CliPath at a claude.exe or at the package's cli.js, or " +
            "install Claude Code natively (irm https://claude.ai/install.ps1 | iex).");
    }

    /// <summary>The existing file a shim launches, or null.</summary>
    internal static string? ShimTargetOf(string shimPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(shimPath))!;
        string text;
        try { text = File.ReadAllText(shimPath); }
        catch (IOException) { return PackageEntry(dir); }
        catch (UnauthorizedAccessException) { return PackageEntry(dir); }

        var matches = ShimTarget.Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var rel = matches[i].Groups["path"].Value.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(rel), "node.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(Path.Combine(dir, rel));
            if (File.Exists(full)) return full;
        }
        return PackageEntry(dir);
    }

    /// <summary>
    /// Fallback for unrecognised shim text: the package installed next to a global npm shim,
    /// <c>&lt;dir&gt;\node_modules\@anthropic-ai\claude-code</c>, native binary first.
    /// </summary>
    private static string? PackageEntry(string shimDir)
    {
        var pkg = Path.Combine(shimDir, "node_modules", "@anthropic-ai", "claude-code");
        foreach (var entry in new[] { Path.Combine(pkg, "bin", "claude.exe"), Path.Combine(pkg, "cli.js") })
            if (File.Exists(entry)) return entry;
        return null;
    }

    /// <summary>The node a shim would use: <c>node.exe</c> beside the shim (npm's own rule), else on PATH.</summary>
    private static string FindNode(string shimDir, string? pathEnv, string script)
    {
        var local = Path.Combine(shimDir, "node.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new CliNotFoundException($"Claude Code at '{script}' needs Node.js, but node.exe was not found on PATH.", script);
    }
}
