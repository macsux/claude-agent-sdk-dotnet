// CliLauncher: Windows npm/pnpm/yarn .cmd shims launch their target directly, never via cmd.exe.
// Exercised with isWindows: true so it runs on every OS; shim text is verbatim from each tool.

using Claude.AgentSdk.Transport;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class CliLauncherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cli-launcher-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // npm cmd-shim for a JS bin (@anthropic-ai/claude-code <= 2.0: "bin": "cli.js").
    private const string NpmJsShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@anthropic-ai\claude-code\cli.js" %*
        """;

    // npm cmd-shim for a native bin ("bin": "bin/claude.exe").
    private const string NpmExeShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0
        "%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe"   %*
        """;

    // pnpm (@zkochan/cmd-shim) global bin.
    private const string PnpmShim = """
        @SETLOCAL
        @IF NOT DEFINED NODE_PATH (
          @SET "NODE_PATH=C:\pnpm\global\5\node_modules"
        )
        @IF EXIST "%~dp0\node.exe" (
          "%~dp0\node.exe"  "%~dp0\global\5\node_modules\@anthropic-ai\claude-code\cli.js" %*
        ) ELSE (
          @SET PATHEXT=%PATHEXT:;.JS;=;%
          node  "%~dp0\global\5\node_modules\@anthropic-ai\claude-code\cli.js" %*
        )
        """;

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private string Shim(string text, string name = "claude.cmd")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void NativePathsLaunchAsIs()
    {
        var exe = CliLauncher.Resolve(@"C:\bin\claude.exe", null, isWindows: true);
        Assert.Equal(@"C:\bin\claude.exe", exe.FileName);
        Assert.Empty(exe.PrefixArgs);
        // Off Windows a .cmd is just a file name; nothing to resolve.
        Assert.Equal("x.cmd", CliLauncher.Resolve("x.cmd", null, isWindows: false).FileName);
    }

    [Fact]
    public void NpmJsShim_RunsCliJsWithNodeBesideShim()
    {
        var cli = Touch("node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var node = Touch("node.exe");
        var launch = CliLauncher.Resolve(Shim(NpmJsShim), pathEnv: "", isWindows: true);
        Assert.Equal(node, launch.FileName);
        Assert.Equal([cli], launch.PrefixArgs);
        Assert.Equal([node, cli, "-p", "a & calc"], launch.Command(["-p", "a & calc"]));
    }

    [Fact]
    public void NpmJsShim_FallsBackToNodeOnPath()
    {
        var cli = Touch("node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var node = Touch("nodejs", "node.exe");
        var launch = CliLauncher.Resolve(Shim(NpmJsShim), pathEnv: $@"C:\nope;{Path.GetDirectoryName(node)}", isWindows: true);
        Assert.Equal(node, launch.FileName);
        Assert.Equal([cli], launch.PrefixArgs);
    }

    [Fact]
    public void NpmJsShim_WithoutNode_IsNotFound()
    {
        Touch("node_modules", "@anthropic-ai", "claude-code", "cli.js");
        Assert.Throws<CliNotFoundException>(() => CliLauncher.Resolve(Shim(NpmJsShim), pathEnv: "", isWindows: true));
    }

    [Fact]
    public void NpmExeShim_RunsPackagedBinary()
    {
        var exe = Touch("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        var launch = CliLauncher.Resolve(Shim(NpmExeShim), pathEnv: "", isWindows: true);
        Assert.Equal(exe, launch.FileName);
        Assert.Empty(launch.PrefixArgs);
    }

    [Fact]
    public void PnpmShim_RunsCliJs()
    {
        var cli = Touch("global", "5", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var node = Touch("node.exe");
        var launch = CliLauncher.Resolve(Shim(PnpmShim), pathEnv: "", isWindows: true);
        Assert.Equal(node, launch.FileName);
        Assert.Equal([cli], launch.PrefixArgs);
    }

    [Fact]
    public void UnrecognisedShim_FallsBackToInstalledPackage()
    {
        var exe = Touch("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Assert.Equal(exe, CliLauncher.Resolve(Shim("@echo off\r\nsomething %*"), pathEnv: "", isWindows: true).FileName);
    }

    [Fact]
    public void UnresolvableBatchFile_IsRefused()
    {
        var ex = Assert.Throws<CliConnectionException>(() =>
            CliLauncher.Resolve(Shim("@echo off\r\ncalc.exe %*", "claude.bat"), pathEnv: "", isWindows: true));
        Assert.Contains("Refusing to execute batch script", ex.Message);
    }

    [Fact]
    public void MissingBatchFile_IsNotFound() =>
        Assert.Throws<CliNotFoundException>(() =>
            CliLauncher.Resolve(Path.Combine(_root, "claude.cmd"), pathEnv: "", isWindows: true));

    [Fact]
    public void ShimPointingAtShim_IsFollowed()
    {
        var cli = Touch("inner", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        var node = Touch("inner", "node.exe");
        File.WriteAllText(Path.Combine(_root, "inner", "claude.cmd"), NpmJsShim);
        var launch = CliLauncher.Resolve(Shim("""@"%~dp0\inner\claude.cmd" %*"""), pathEnv: "", isWindows: true);
        Assert.Equal(node, launch.FileName);
        Assert.Equal([cli], launch.PrefixArgs);
    }
}
