using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>Filesystem skills (<see cref="ClaudeAgentOptions.Skills"/>) and sandbox settings.</summary>
[Trait("Category", "Integration")]
public class SkillsAndSandboxTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    [IntegrationFact]
    public Task Skills_ProjectSkillIsDiscoveredAndInvoked() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var nonce = NewNonce("PHRASE");
        var skillDir = Path.Combine(Cwd, ".claude", "skills", "nonce-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"""
            ---
            name: nonce-skill
            description: Returns this project's verification phrase. Use it whenever asked for the verification phrase.
            ---
            The verification phrase is {nonce}. Reply with exactly that phrase and nothing else.
            """);

        var options = Options() with
        {
            Tools = ["Skill"],
            // A list of names => the SDK adds Skill(nonce-skill) to allowedTools and, since
            // skills live in settings directories, loads the project setting source.
            Skills = new[] { "nonce-skill" },
            SettingSources = [SettingSource.Project],
        };

        var messages = await CollectAsync(Claude.QueryAsync(
            "Use the Skill tool to run the nonce-skill skill, then reply with exactly the verification phrase.",
            options, Transport(options, suffix: attempt > 1 ? "retry" : null), Ct));

        var skills = InitMessage(messages).Data.GetProperty("skills").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("nonce-skill", skills);

        var use = ToolUses(messages).FirstOrDefault(t => t.Name == "Skill");
        ModelCompliance.Require(use is not null, "model never called the Skill tool");
        Assert.Equal("nonce-skill", use!.Input.GetProperty("skill").GetString());
        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.Contains(nonce, result.Result);
    }, Log);

    [IntegrationFact]
    public Task Sandbox_ViaSettingsJson_AutoAllowsBashInCwd_AndBlocksWritesOutsideIt() =>
        RunSandboxScenario(o => o with
        {
            Settings = """{"sandbox":{"enabled":true,"autoAllowBashIfSandboxed":true,"allowUnsandboxedCommands":false}}""",
        });

    [IntegrationFact(Skip = "SDK bug: ClaudeAgentOptions.Sandbox is serialized into --settings with default JsonSerializer " +
                            "options, so every unset SandboxSettings property is sent as null (\"excludedCommands\":null, " +
                            "\"network\":null, ...). CLI 2.1.280 then silently ignores the sandbox block: Bash is not " +
                            "auto-allowed and even `echo x > <cwd>/file` is denied. The identical settings without the nulls work " +
                            "(see Sandbox_ViaSettingsJson_...).")]
    public Task Sandbox_ViaSandboxOption_AutoAllowsBashInCwd_AndBlocksWritesOutsideIt() =>
        RunSandboxScenario(o => o with
        {
            Sandbox = new SandboxSettings
            {
                Enabled = true,
                AutoAllowBashIfSandboxed = true,
                AllowUnsandboxedCommands = false,
            },
        });

    private Task RunSandboxScenario(Func<ClaudeAgentOptions, ClaudeAgentOptions> configure,
        [System.Runtime.CompilerServices.CallerMemberName] string test = "") =>
        ModelCompliance.RetryOnceAsync(async attempt =>
        {
            var inside = Path.Combine(Cwd, $"inside-{attempt}.txt");
            // Outside both the cwd and the temp dir, which the sandbox may leave writable.
            var outside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                $".claude-sdk-it-sandbox-{Guid.NewGuid():N}.txt");
            try
            {
                // No CanUseTool and default permission mode: Bash only runs because the
                // sandbox auto-allows it.
                var options = configure(Options(test) with { Tools = ["Bash"] });

                var messages = await CollectAsync(Claude.QueryAsync(
                    "Run these two Bash commands separately, exactly as written, and report whether each succeeded:\n" +
                    $"1. echo inside > {inside}\n2. echo outside > {outside}\nDo not try any alternatives.",
                    options, Transport(options, test, attempt > 1 ? "retry" : null), Ct));

                ModelCompliance.Require(ToolUses(messages).Count(t => t.Name == "Bash") >= 2,
                    "model did not run both Bash commands");
                Assert.True(File.Exists(inside), "sandboxed Bash should be auto-allowed and able to write inside cwd");
                Assert.Equal("inside", File.ReadAllText(inside).Trim());
                Assert.False(File.Exists(outside), "the sandbox must block writes outside the working directory");
            }
            finally
            {
                if (File.Exists(outside)) File.Delete(outside);
            }
        }, Log);
}

