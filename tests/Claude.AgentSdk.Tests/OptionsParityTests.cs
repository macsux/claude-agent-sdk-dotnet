// Python 0.2.158 parity for ClaudeAgentOptions: argv construction
// (claude-agent-sdk-python/tests/test_transport.py), initialize-request fields
// (tests/test_query.py, tests/test_client.py, tests/test_streaming_client.py),
// the can_use_tool helpers (tests/test_option_warnings.py) and the typed
// option unions that replaced the old `object` members.

using System.Globalization;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class OptionsParityTests
{
    private static List<string> Cmd(ClaudeAgentOptions options) =>
        new SubprocessTransport("test", options with { CliPath = "dummy-claude" }).BuildCommand();

    private static string ValueOf(List<string> cmd, string flag) => cmd[cmd.IndexOf(flag) + 1];

    private static async Task<JsonElement> InitializeRequestAsync(ClaudeAgentOptions options)
    {
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();
        return transport.Written
            .First(w => w.GetProperty("type").GetString() == "control_request")
            .GetProperty("request");
    }

    #region resume_session_at / resume_drops_turn (test_transport.py)

    [Fact]
    public void ResumeSessionAtAndDropsTurn_UseEqualsForm()
    {
        const string at = "0d78eb23-2d48-4741-b970-4ed0a3356cce";
        const string drops = "ce0a8011-2c8d-40f2-86e5-d6e1b0c041c0";
        var cmd = Cmd(new ClaudeAgentOptions
        {
            Resume = "abc123",
            ForkSession = true,
            ResumeSessionAt = at,
            ResumeDropsTurn = drops
        });

        Assert.Contains($"--resume-session-at={at}", cmd);
        Assert.Contains($"--resume-drops-turn={drops}", cmd);
        Assert.DoesNotContain("--resume-session-at", cmd);
        Assert.DoesNotContain("--resume-drops-turn", cmd);
        Assert.DoesNotContain(at, cmd);
        Assert.DoesNotContain(drops, cmd);
    }

    [Fact]
    public void ResumeDropsTurn_OmittedByDefault()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Resume = "abc123", ResumeSessionAt = "x" });
        Assert.Contains("--resume-session-at=x", cmd);
        Assert.DoesNotContain(cmd, a => a.StartsWith("--resume-drops-turn"));
    }

    // An empty declaration must reach the CLI (which rejects it) rather than
    // being dropped here and silently disarming the guard.
    [Fact]
    public void ResumeDropsTurn_EmptyIsForwarded()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Resume = "abc123", ResumeSessionAt = "x", ResumeDropsTurn = "" });
        Assert.Contains("--resume-drops-turn=", cmd);
    }

    [Fact]
    public void ResumeSessionAt_EmptyIsOmitted()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Resume = "abc123", ResumeSessionAt = "" });
        Assert.DoesNotContain(cmd, a => a.StartsWith("--resume-session-at"));
    }

    [Fact]
    public void ResumeSessionAt_DashLeadingValueCannotInjectFlags()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Resume = "abc", ResumeSessionAt = "--dangerously-skip-permissions" });
        Assert.Contains("--resume-session-at=--dangerously-skip-permissions", cmd);
        Assert.DoesNotContain("--dangerously-skip-permissions", cmd);
    }

    [Theory]
    [InlineData("ResumeSessionAt", "id&calc")]
    [InlineData("ResumeDropsTurn", "id|x")]
    public void NewResumeValues_UseWindowsMetacharacterGuard(string option, string value)
    {
        // The guard itself is platform-gated; BuildCommand passes these option
        // names through it exactly like Resume / SessionId.
        Assert.Throws<ArgumentException>(() =>
            SubprocessTransport.RejectWindowsCmdMetacharacters(option, value, isWindows: true));
    }

    #endregion

    #region System prompt (test_transport.py)

    [Theory]
    [InlineData("Be helpful")]
    [InlineData("")]
    [InlineData("--help")]
    public void SystemPromptCustom_ReachesCliLikeAPlainString(string prompt)
    {
        var cmd = Cmd(new ClaudeAgentOptions
        {
            SystemPrompt = new SystemPromptCustom { Prompt = prompt, Snapshot = false }
        });

        Assert.Equal(prompt, ValueOf(cmd, "--system-prompt"));
        Assert.DoesNotContain("--append-system-prompt", cmd);
        Assert.DoesNotContain("--system-prompt-snapshot", cmd);
    }

    [Fact]
    public void SystemPrompt_StringConvertsToText()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = "Be concise." };
        Assert.Equal(new SystemPromptText("Be concise."), options.SystemPrompt);
        Assert.Equal("Be concise.", ValueOf(Cmd(options), "--system-prompt"));
    }

    [Fact]
    public void SystemPrompt_NullSendsEmptyPrompt()
    {
        Assert.Equal("", ValueOf(Cmd(new ClaudeAgentOptions()), "--system-prompt"));
    }

    [Fact]
    public void SystemPromptPreset_WithoutAppend_EmitsNothing()
    {
        var cmd = Cmd(new ClaudeAgentOptions { SystemPrompt = SystemPromptPreset.ClaudeCode() with { Snapshot = true } });
        Assert.DoesNotContain("--system-prompt", cmd);
        Assert.DoesNotContain("--append-system-prompt", cmd);
    }

    [Fact]
    public void SystemPromptFile_UsesFileFlag()
    {
        var cmd = Cmd(new ClaudeAgentOptions { SystemPrompt = new SystemPromptFile { Path = "/path/to/prompt.md" } });
        Assert.Equal("/path/to/prompt.md", ValueOf(cmd, "--system-prompt-file"));
        Assert.DoesNotContain("--system-prompt", cmd);
    }

    #endregion

    #region Other argv parity

    [Theory]
    [InlineData(PermissionMode.DontAsk, "dontAsk")]
    [InlineData(PermissionMode.Auto, "auto")]
    [InlineData(PermissionMode.Plan, "plan")]
    public void PermissionMode_AllPythonLiteralsReachTheCli(PermissionMode mode, string expected)
    {
        Assert.Equal(expected, ValueOf(Cmd(new ClaudeAgentOptions { PermissionMode = mode }), "--permission-mode"));
    }

    // Python: `if self._options.max_turns:` -- 0 is falsy and omitted.
    [Fact]
    public void MaxTurns_ZeroIsOmitted()
    {
        Assert.DoesNotContain("--max-turns", Cmd(new ClaudeAgentOptions { MaxTurns = 0 }));
        Assert.Equal("3", ValueOf(Cmd(new ClaudeAgentOptions { MaxTurns = 3 }), "--max-turns"));
    }

    [Fact]
    public void MaxBudget_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.5", ValueOf(Cmd(new ClaudeAgentOptions { MaxBudgetUsd = 0.5m }), "--max-budget-usd"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void McpServers_EmptyMapIsOmitted()
    {
        var cmd = Cmd(new ClaudeAgentOptions { McpServers = new Dictionary<string, object>() });
        Assert.DoesNotContain("--mcp-config", cmd);
    }

    [Fact]
    public void McpServers_PathIsPassedThrough()
    {
        var cmd = Cmd(new ClaudeAgentOptions { McpServers = "/path/to/mcp.json" });
        Assert.Equal("/path/to/mcp.json", ValueOf(cmd, "--mcp-config"));
    }

    [Fact]
    public void McpServers_SdkServersAreStrippedToTypeAndName()
    {
        var servers = McpServers.Sdk("calc", s => { });
        servers["ext"] = new McpStdioServerConfig { Command = "node", Args = ["server.js"] };
        var cmd = Cmd(new ClaudeAgentOptions { McpServers = servers });

        var config = JsonDocument.Parse(ValueOf(cmd, "--mcp-config")).RootElement.GetProperty("mcpServers");
        Assert.Equal("sdk", config.GetProperty("calc").GetProperty("type").GetString());
        Assert.Equal("calc", config.GetProperty("calc").GetProperty("name").GetString());
        Assert.Equal(2, config.GetProperty("calc").EnumerateObject().Count());
        Assert.Equal("node", config.GetProperty("ext").GetProperty("command").GetString());
    }

    [Fact]
    public void Plugins_NonLocalTypeIsRejected()
    {
        var options = new ClaudeAgentOptions { Plugins = [new SdkPluginConfig("remote", "x")] };
        var ex = Assert.Throws<ArgumentException>(() => Cmd(options));
        Assert.Contains("Unsupported plugin type: remote", ex.Message);
    }

    [Fact]
    public void Skills_AllAddsBareSkillTool()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Skills = "all", AllowedTools = ["Read"] });
        Assert.Equal("Read,Skill", ValueOf(cmd, "--allowedTools"));
        Assert.Contains("--setting-sources=user,project", cmd);
    }

    [Fact]
    public void Skills_EmptyListSuppressesEverySkill()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Skills = new List<string>() });
        Assert.DoesNotContain("--allowedTools", cmd);
        Assert.Contains("--setting-sources=user,project", cmd);
    }

    #endregion

    #region verbatim_prompts version warning (test_verbatim_prompts.py TestOlderCliWarning)

    [Theory]
    [InlineData("2.1.247")]
    [InlineData("2.0.5")]
    public void VerbatimPrompts_WarnsWhenCliPredatesClientComposed(string version)
    {
        var warning = SubprocessTransport.GetVerbatimPromptsVersionWarning(true, version, "/usr/bin/claude");
        Assert.NotNull(warning);
        Assert.Contains("VerbatimPrompts", warning);
        Assert.EndsWith("Claude Code 2.1.248 or later is required.", warning);
    }

    [Theory]
    [InlineData("2.1.248")]
    [InlineData("2.1.273")]
    [InlineData("3.0.0")]
    public void VerbatimPrompts_NoWarningWhenCliSupportsIt(string version)
    {
        Assert.Null(SubprocessTransport.GetVerbatimPromptsVersionWarning(true, version, "/usr/bin/claude"));
    }

    [Fact]
    public void VerbatimPrompts_NoWarningWhenOptionIsOff()
    {
        Assert.Null(SubprocessTransport.GetVerbatimPromptsVersionWarning(false, "2.1.100", "/usr/bin/claude"));
    }

    #endregion

    #region Initialize request (test_query.py / test_client.py / test_streaming_client.py)

    public static TheoryData<SystemPromptConfig?, bool?> SnapshotCases => new()
    {
        { new SystemPromptCustom { Prompt = "Be helpful", Snapshot = false }, false },
        { SystemPromptPreset.ClaudeCode() with { Snapshot = true }, true },
        { SystemPromptPreset.ClaudeCode(), null },
        { new SystemPromptFile { Path = "/p.md" }, null },
        { "Be helpful", null },
        { null, null },
    };

    // query()/connect() hand snapshot to initialize only for the preset and custom forms.
    [Theory]
    [MemberData(nameof(SnapshotCases))]
    public async Task Initialize_SendsSystemPromptSnapshotOnlyForPresetAndCustom(SystemPromptConfig? prompt, bool? expected)
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions { SystemPrompt = prompt });

        if (expected is null)
            Assert.False(init.TryGetProperty("systemPromptSnapshot", out _));
        else
            Assert.Equal(expected.Value, init.GetProperty("systemPromptSnapshot").GetBoolean());
    }

    [Fact]
    public async Task Initialize_SendsForwardSubagentTextWhenEnabled()
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions { ForwardSubagentText = true });
        Assert.True(init.GetProperty("forwardSubagentText").GetBoolean());
    }

    [Fact]
    public async Task Initialize_OmitsForwardSubagentTextByDefault()
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions());
        Assert.False(init.TryGetProperty("forwardSubagentText", out _));
    }

    [Fact]
    public async Task Initialize_SkillsAllSendsNoFilter()
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions { Skills = SkillsConfig.All });
        Assert.False(init.TryGetProperty("skills", out _));
    }

    [Fact]
    public async Task Initialize_AgentEffortAndMcpServerUnionsSerializeLikePython()
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["a"] = new("d", "p", Effort: 2048, McpServers:
                [
                    "github",
                    new Dictionary<string, object> { ["local"] = new McpStdioServerConfig { Command = "srv" } }
                ]),
                ["b"] = new("d", "p", Effort: EffortLevel.XHigh)
            }
        });

        var agents = init.GetProperty("agents");
        var a = agents.GetProperty("a");
        Assert.Equal(2048, a.GetProperty("effort").GetInt32());
        var servers = a.GetProperty("mcpServers");
        Assert.Equal("github", servers[0].GetString());
        Assert.Equal("srv", servers[1].GetProperty("local").GetProperty("command").GetString());
        Assert.Equal("xhigh", agents.GetProperty("b").GetProperty("effort").GetString());
    }

    #endregion

    #region Typed option unions keep the old assignment shapes compiling

    [Fact]
    public void OptionUnions_AcceptPreviouslySupportedShapes()
    {
        var registry = McpServers.Sdk("calc", s => { });
        var options = new ClaudeAgentOptions
        {
            SystemPrompt = "x",
            Skills = "all",
            McpServers = new Dictionary<string, object> { ["s"] = new McpHttpServerConfig { Url = "http://x" } }
        };

        Assert.IsType<SystemPromptText>(options.SystemPrompt);
        Assert.Same(SkillsConfig.All, options.Skills);
        Assert.IsType<McpServersConfig.ServerMap>(options.McpServers);

        options = options with
        {
            SystemPrompt = SystemPromptPreset.ClaudeCode("more"),
            Skills = new List<string> { "pdf" },
            McpServers = registry
        };
        Assert.IsType<SystemPromptPreset>(options.SystemPrompt);
        Assert.Equal(["pdf"], Assert.IsType<SkillsConfig.Named>(options.Skills).Names);
        Assert.Single(options.McpServers!.SdkServers());

        options = options with { Skills = new[] { "a", "b" }, McpServers = "{\"mcpServers\":{}}" };
        Assert.Equal(["a", "b"], Assert.IsType<SkillsConfig.Named>(options.Skills).Names);
        Assert.IsType<McpServersConfig.ConfigPath>(options.McpServers);
    }

    [Fact]
    public void AgentDefinitionUnions_AcceptLevelIntNameAndInlineMap()
    {
        var def = new AgentDefinition("d", "p", McpServers: ["x"], Effort: EffortLevel.Low);
        Assert.Equal(new AgentEffort.Level(EffortLevel.Low), def.Effort);
        Assert.Equal(new AgentMcpServer.Reference("x"), def.McpServers![0]);

        def = def with { Effort = 5 };
        Assert.Equal(new AgentEffort.Tokens(5), def.Effort);
    }

    #endregion

    #region Builder

    [Fact]
    public void Builder_SetsNewOptions()
    {
        var options = Claude.Options()
            .ResumeSessionAt("at-uuid", dropsTurn: "drop-uuid")
            .ForwardSubagentText()
            .VerbatimPrompts()
            .LoadTimeoutMs(1234)
            .SystemPrompt("custom", snapshot: false)
            .McpServers("/cfg.json")
            .Build();

        Assert.Equal("at-uuid", options.ResumeSessionAt);
        Assert.Equal("drop-uuid", options.ResumeDropsTurn);
        Assert.True(options.ForwardSubagentText);
        Assert.True(options.VerbatimPrompts);
        Assert.Equal(1234, options.LoadTimeoutMs);
        Assert.Equal(new SystemPromptCustom { Prompt = "custom", Snapshot = false }, options.SystemPrompt);
        Assert.Equal(new McpServersConfig.ConfigPath("/cfg.json"), options.McpServers);
    }

    [Fact]
    public void Builder_Defaults_MatchPython()
    {
        var options = Claude.Options().Build();
        Assert.False(options.ForwardSubagentText);
        Assert.False(options.VerbatimPrompts);
        Assert.Null(options.ResumeSessionAt);
        Assert.Null(options.ResumeDropsTurn);
        Assert.Equal(60_000, options.LoadTimeoutMs);
        Assert.Equal(60_000, new ClaudeAgentOptions().LoadTimeoutMs);
    }

    [Fact]
    public void AgentsBuilder_AcceptsFullDefinition()
    {
        var options = Claude.Options()
            .Agents(a => a.Add("r", new AgentDefinition("d", "p", Effort: EffortLevel.Max)))
            .Build();
        Assert.Equal(new AgentEffort.Level(EffortLevel.Max), options.Agents!["r"].Effort);
    }

    #endregion

    #region load_timeout_ms

    [Theory]
    [InlineData(60_000, 60_000.0)]
    [InlineData(1_500, 1_500.0)]
    public void LoadTimeout_MapsMilliseconds(int ms, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs),
            SessionStoreSupport.LoadTimeout(new ClaudeAgentOptions { LoadTimeoutMs = ms }));
    }

    // Python: "A value of 0 means immediate timeout".
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LoadTimeout_ZeroOrLessIsImmediate(int ms)
    {
        Assert.Equal(TimeSpan.FromTicks(1), SessionStoreSupport.LoadTimeout(new ClaudeAgentOptions { LoadTimeoutMs = ms }));
    }

    private sealed class HangingStore : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return null;
        }
    }

    [Fact]
    public async Task QueryAsync_HonorsLoadTimeoutMsDuringResume()
    {
        var options = new ClaudeAgentOptions
        {
            SessionStore = new HangingStore(),
            Resume = "550e8400-e29b-41d4-a716-446655440000",
            LoadTimeoutMs = 100,
            CliPath = "dummy-claude-never-spawned"
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<SessionStoreOperationException>(async () =>
        {
            await foreach (var _ in Claude.QueryAsync("hi", options)) { }
        });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    #endregion

    #region can_use_tool (test_option_warnings.py)

    [Theory]
    [InlineData("Read", "Read")]
    [InlineData("mcp__server__tool", "mcp__server__tool")]
    [InlineData("Read(*)", "Read")]
    [InlineData("Read()", "Read")]
    [InlineData("mcp__server__tool(*)", "mcp__server__tool")]
    [InlineData("Bash(ls:*)", null)]
    [InlineData("Bash(git log:*)", null)]
    [InlineData("Bash(*.py)", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Bash(ls:*", null)]
    [InlineData("Bash(ls)x", null)]
    [InlineData("(foo)", null)]
    [InlineData("(*)", null)]
    [InlineData("Read(*x", null)]
    public void WholeToolAllowed(string entry, string? expected)
    {
        Assert.Equal(expected, CanUseToolConfiguration.WholeToolAllowed(entry));
    }

    [Fact]
    public void ShadowedWarning_BypassPermissions()
    {
        var message = CanUseToolConfiguration.GetShadowedWarning(PermissionMode.BypassPermissions, ["Read", "Write"]);
        Assert.NotNull(message);
        Assert.Contains("bypassPermissions", message);
        Assert.Contains("PreToolUse", message);
        Assert.DoesNotContain("Read", message);
    }

    [Fact]
    public void ShadowedWarning_BareEntries()
    {
        var message = CanUseToolConfiguration.GetShadowedWarning(null, ["Read", "mcp__server__tool", "Bash(ls:*)"]);
        Assert.NotNull(message);
        Assert.Contains("Read, mcp__server__tool", message);
        Assert.DoesNotContain("Bash(ls:*)", message);
        Assert.Contains("settings files", message);
    }

    [Fact]
    public void ShadowedWarning_DedupesPreservingFirstSeenOrder()
    {
        Assert.Contains("invoked for: Write, Read.",
            CanUseToolConfiguration.GetShadowedWarning(null, ["Write", "Read", "Write()"]));
        Assert.Contains("invoked for: Read.",
            CanUseToolConfiguration.GetShadowedWarning(null, ["   ", "Read", "Read()", "Read(*)"]));
    }

    [Fact]
    public void ShadowedWarning_AcceptEditsAloneIsSilent()
    {
        Assert.Null(CanUseToolConfiguration.GetShadowedWarning(PermissionMode.AcceptEdits, []));
        Assert.Contains("Read", CanUseToolConfiguration.GetShadowedWarning(PermissionMode.AcceptEdits, ["Read"]));
    }

    // skills="all" appends a bare "Skill" to the effective allowed tools, so it
    // shadows the callback just like a hand-written entry; skills=[names] does not.
    [Fact]
    public void ShadowedWarning_SkillsAllCountsAsBareSkill()
    {
        CanUseToolCallback cb = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow());
        Assert.Contains("invoked for: Skill.",
            CanUseToolConfiguration.GetShadowedWarning(new ClaudeAgentOptions { CanUseTool = cb, Skills = "all" }));
        Assert.Null(CanUseToolConfiguration.GetShadowedWarning(
            new ClaudeAgentOptions { CanUseTool = cb, Skills = new List<string> { "pdf" } }));
        Assert.Null(CanUseToolConfiguration.GetShadowedWarning(new ClaudeAgentOptions { Skills = "all" }));
    }

    [Fact]
    public void Configure_RoutesPromptsOverStdioAndRejectsPromptToolName()
    {
        CanUseToolCallback cb = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow());

        Assert.Equal("stdio", CanUseToolConfiguration.Configure(new ClaudeAgentOptions { CanUseTool = cb }).PermissionPromptToolName);

        var plain = new ClaudeAgentOptions();
        Assert.Same(plain, CanUseToolConfiguration.Configure(plain));

        Assert.Throws<ArgumentException>(() => CanUseToolConfiguration.Configure(
            new ClaudeAgentOptions { CanUseTool = cb, PermissionPromptToolName = "mcp__x__y" }));
    }

    [Fact]
    public async Task QueryAsync_RejectsCanUseToolWithPermissionPromptToolName()
    {
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
            PermissionPromptToolName = "mcp__x__y"
        };
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in Claude.QueryAsync("hi", options, new FakeTransport())) { }
        });
    }

    #endregion
}
