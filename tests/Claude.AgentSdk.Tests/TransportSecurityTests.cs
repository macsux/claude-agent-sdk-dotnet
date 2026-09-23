// Python parity for argv construction and CLI discovery hardening
// (claude-agent-sdk-python/_internal/transport/subprocess_cli.py).

using System.Diagnostics;
using Claude.AgentSdk.Transport;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class TransportSecurityTests
{
    private static SubprocessTransport MakeTransport(ClaudeAgentOptions options) =>
        new(prompt: "test", options: options with { CliPath = "dummy-claude" });

    [Theory]
    [InlineData(@"C:\npm\claude.cmd")]
    [InlineData(@"C:\npm\CLAUDE.BAT")]
    [InlineData(@"C:\npm\claude.cmd. . ")]
    [InlineData(@"C:\npm\claude.cmd:stream")]
    [InlineData(@"C:claude.cmd")]
    [InlineData(@"C:\npm\claude.cmd\...\..")]
    [InlineData("/opt/x.cmd/claude.exe")]
    [InlineData(".cmd")]
    public void IsBatchScriptPath_DetectsBatchSpellings(string path)
    {
        Assert.True(SubprocessTransport.IsBatchScriptPath(path));
    }

    [Theory]
    [InlineData(@"C:\Users\me\.local\bin\claude.exe")]
    [InlineData("/usr/local/bin/claude")]
    [InlineData(@"C:\cmdtools\claude.exe")]
    public void IsBatchScriptPath_AllowsNativeExecutables(string path)
    {
        Assert.False(SubprocessTransport.IsBatchScriptPath(path));
    }

    [Fact]
    public void RejectWindowsBatchCli_ThrowsOnWindowsOnly()
    {
        Assert.Throws<CliConnectionException>(() =>
            SubprocessTransport.RejectWindowsBatchCli(@"C:\npm\claude.cmd", isWindows: true));
        SubprocessTransport.RejectWindowsBatchCli(@"C:\npm\claude.cmd", isWindows: false);
        SubprocessTransport.RejectWindowsBatchCli(@"C:\bin\claude.exe", isWindows: true);
    }

    [Theory]
    [InlineData("abc & calc")]
    [InlineData("%PATH%")]
    [InlineData("a|b")]
    [InlineData("x\"y")]
    [InlineData("line\nbreak")]
    public void RejectWindowsCmdMetacharacters_RejectsOnWindows(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            SubprocessTransport.RejectWindowsCmdMetacharacters("Resume", value, isWindows: true));
        SubprocessTransport.RejectWindowsCmdMetacharacters("Resume", value, isWindows: false);
    }

    [Fact]
    public void BuildCommand_ResumeUsesEqualsFormSoDashValueCannotBecomeAFlag()
    {
        var cmd = MakeTransport(new ClaudeAgentOptions { Resume = "--dangerously-skip-permissions" }).BuildCommand();

        Assert.Contains("--resume=--dangerously-skip-permissions", cmd);
        Assert.DoesNotContain("--resume", cmd);
        Assert.DoesNotContain("--dangerously-skip-permissions", cmd);
    }

    [Fact]
    public void BuildCommand_SessionIdUsesEqualsForm()
    {
        var cmd = MakeTransport(new ClaudeAgentOptions { SessionId = "-x" }).BuildCommand();

        Assert.Contains("--session-id=-x", cmd);
        Assert.DoesNotContain("--session-id", cmd);
    }

    [Fact]
    public void BuildCommand_ExtraArgWithDashValueUsesEqualsForm()
    {
        var cmd = MakeTransport(new ClaudeAgentOptions
        {
            ExtraArgs = new Dictionary<string, string?>
            {
                ["some-flag"] = "--dangerously-skip-permissions",
                ["plain"] = "value",
                ["bool-flag"] = null
            }
        }).BuildCommand();

        Assert.Contains("--some-flag=--dangerously-skip-permissions", cmd);
        Assert.DoesNotContain("--dangerously-skip-permissions", cmd);
        var plainIdx = cmd.IndexOf("--plain");
        Assert.Equal("value", cmd[plainIdx + 1]);
        Assert.Contains("--bool-flag", cmd);
    }

    [Theory]
    [InlineData("x),Bash,Skill(y")]
    [InlineData("a,b")]
    [InlineData("bad\u0001name")]
    [InlineData("*")]
    [InlineData("plugin:*")]
    [InlineData("/slash")]
    [InlineData(" padded ")]
    [InlineData("")]
    [InlineData(@"a\\b")]
    [InlineData(@"trailing\")]
    public void BuildCommand_RejectsUnsafeSkillNames(string name)
    {
        var transport = MakeTransport(new ClaudeAgentOptions { Skills = new List<string> { name } });
        Assert.Throws<ArgumentException>(() => transport.BuildCommand());
    }

    // Python raises TypeError for a bare skill name (it would iterate as
    // characters). With the typed SkillsConfig the rejection happens as soon as
    // the string is converted, with the same "Did you mean" hint.
    [Fact]
    public void Skills_RejectsBareStringOtherThanAll()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ClaudeAgentOptions { Skills = "pdf" });
        Assert.Contains("Did you mean [\"pdf\"]?", ex.Message);
    }

    [Fact]
    public void BuildCommand_ValidSkillsBecomeAllowedToolRules()
    {
        var cmd = MakeTransport(new ClaudeAgentOptions { Skills = new List<string> { "pdf", "plugin:xlsx" } }).BuildCommand();

        var idx = cmd.IndexOf("--allowedTools");
        Assert.Equal("Skill(pdf),Skill(plugin:xlsx)", cmd[idx + 1]);
    }

    [Fact]
    public void BuildCommand_AlwaysStreamsAndNeverPutsPromptOrAgentsOnArgv()
    {
        var options = new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["reviewer"] = new("Reviews code", "You review code.")
            }
        };
        var cmd = new SubprocessTransport("secret prompt text", options with { CliPath = "dummy-claude" }).BuildCommand();

        Assert.DoesNotContain("--print", cmd);
        Assert.DoesNotContain("secret prompt text", cmd);
        Assert.DoesNotContain("--agents", cmd);
        var idx = cmd.IndexOf("--input-format");
        Assert.Equal("stream-json", cmd[idx + 1]);
    }

    [Fact]
    public void BuildCommand_ToolsPresetEmitsToolsDefault()
    {
        var cmd = MakeTransport(new ClaudeAgentOptions { ToolsPreset = ToolsPreset.ClaudeCode() }).BuildCommand();

        var idx = cmd.IndexOf("--tools");
        Assert.Equal("default", cmd[idx + 1]);
    }

    [Fact]
    public async Task ConnectAsync_UserOptionFailsLoudly()
    {
        var transport = MakeTransport(new ClaudeAgentOptions { User = "nobody" });
        await Assert.ThrowsAsync<NotSupportedException>(() => transport.ConnectAsync());
    }

    [Fact]
    public void ClaudeAgentOptions_ToStringDoesNotLeakEnv()
    {
        var options = new ClaudeAgentOptions
        {
            Env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-secret" }
        };
        Assert.DoesNotContain("sk-secret", options.ToString());
    }
}

public sealed class StdoutFramingTests
{
    [Fact]
    public void LineFramer_ReassemblesLinesAcrossChunks()
    {
        var framer = new LineFramer();
        Assert.Empty(framer.Push("{\"a\":"));
        var lines = framer.Push("1}\n{\"b\"");
        Assert.Equal(["{\"a\":1}"], lines);
        Assert.Equal(4, framer.PendingLength);
        Assert.Equal(["{\"b\":2}"], framer.Push(":2}\n"));
        Assert.Equal("", framer.Flush());
    }

    [Fact]
    public void ParseStdoutLine_SkipsBlankAndNonJsonLines()
    {
        Assert.Null(SubprocessTransport.ParseStdoutLine("   "));
        Assert.Null(SubprocessTransport.ParseStdoutLine("[SandboxDebug] hello"));
        Assert.Equal("x", SubprocessTransport.ParseStdoutLine("{\"type\":\"x\"}\r")!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void ParseStdoutLine_CorruptJsonThrowsInsteadOfSwallowingLaterLines()
    {
        Assert.Throws<JsonDecodeException>(() => SubprocessTransport.ParseStdoutLine("{\"type\": "));
    }
}

/// <summary>
/// End-to-end transport tests against a fake "CLI" shell script (POSIX only).
/// </summary>
public sealed class SubprocessTransportProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sdk-transport-test").FullName;

    public SubprocessTransportProcessTests()
    {
        Environment.SetEnvironmentVariable("CLAUDE_AGENT_SDK_SKIP_VERSION_CHECK", "1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string FakeCli(string body)
    {
        var path = Path.Combine(_dir, "fake-claude");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task ReadMessages_UnterminatedHugeLineIsBoundedByMaxBufferSize()
    {
        if (OperatingSystem.IsWindows()) return;
        // 2 MB without a newline, then hang: ReadLineAsync would buffer it all.
        var cli = FakeCli("head -c 2097152 /dev/zero | tr '\\0' 'a'; sleep 30");
        await using var transport = new SubprocessTransport("", new ClaudeAgentOptions { CliPath = cli, MaxBufferSize = 1024 * 1024 });
        await transport.ConnectAsync();

        await Assert.ThrowsAsync<JsonDecodeException>(async () =>
        {
            await foreach (var _ in transport.ReadMessagesAsync()) { }
        });
    }

    [Fact]
    public async Task ReadMessages_YieldsEachLineAndSkipsNoise()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = FakeCli("printf '[debug] noise\\n{\"type\":\"a\"}\\n\\n{\"type\":\"b\"}'");
        await using var transport = new SubprocessTransport("", new ClaudeAgentOptions { CliPath = cli });
        await transport.ConnectAsync();

        var types = new List<string>();
        await foreach (var msg in transport.ReadMessagesAsync())
            types.Add(msg.GetProperty("type").GetString()!);

        Assert.Equal(["a", "b"], types);
    }

    [Fact]
    public async Task ReadMessages_CorruptLineThrows()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = FakeCli("printf '{\"type\": \\n{\"type\":\"b\"}\\n'");
        await using var transport = new SubprocessTransport("", new ClaudeAgentOptions { CliPath = cli });
        await transport.ConnectAsync();

        await Assert.ThrowsAsync<JsonDecodeException>(async () =>
        {
            await foreach (var _ in transport.ReadMessagesAsync()) { }
        });
    }

    [Fact]
    public async Task Close_SendsSigtermBeforeKilling()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(_dir, "got-term");
        // Ignores stdin EOF; exits cleanly (and records it) only on SIGTERM.
        var cli = FakeCli($"trap 'touch \"{marker}\"; exit 0' TERM\nwhile true; do sleep 0.1; done");
        var transport = new SubprocessTransport("", new ClaudeAgentOptions { CliPath = cli });
        await transport.ConnectAsync();

        var sw = Stopwatch.StartNew();
        await transport.CloseAsync();
        sw.Stop();

        Assert.True(File.Exists(marker), "CLI should have received SIGTERM");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9), $"close took {sw.Elapsed}");
    }
}
