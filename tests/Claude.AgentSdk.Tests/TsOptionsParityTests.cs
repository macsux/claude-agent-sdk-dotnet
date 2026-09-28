// TypeScript SDK (0.3.283) parity for ClaudeAgentOptions: argv flags, CLI
// environment, initialize-request keys, pre-flight validation, builder support,
// and the TS-only session listing switches.

using System.Text.Json;
using System.Text.Json.Nodes;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class TsOptionsParityTests
{
    private static List<string> Cmd(ClaudeAgentOptions options) =>
        new SubprocessTransport("test", options with { CliPath = "dummy-claude" }).BuildCommand();

    private static string ValueOf(List<string> cmd, string flag) => cmd[cmd.IndexOf(flag) + 1];

    private static Dictionary<string, string?> Env(ClaudeAgentOptions options, Dictionary<string, string?>? inherited = null)
    {
        var env = inherited ?? new Dictionary<string, string?>();
        new SubprocessTransport("test", options).BuildEnvironment(env);
        return env;
    }

    private static async Task<JsonElement> InitializeRequestAsync(ClaudeAgentOptions options)
    {
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();
        return transport.RequestsOf("initialize").Single();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    #region argv

    [Fact]
    public void Defaults_AddNoTsFlags()
    {
        var cmd = Cmd(new ClaudeAgentOptions());
        foreach (var flag in new[]
                 {
                     "--no-session-persistence", "--allow-dangerously-skip-permissions", "--agent", "--debug",
                     "--debug-file", "--managed-settings", "--permission-prompts", "--channels", "--workload",
                     "--await-initialize", "--plugin-dir-no-mcp"
                 })
            Assert.DoesNotContain(flag, cmd);
        Assert.DoesNotContain(cmd, a => a.StartsWith("--project-config-root"));
    }

    [Fact]
    public void PersistSessionFalse_AddsNoSessionPersistence()
    {
        Assert.Contains("--no-session-persistence", Cmd(new ClaudeAgentOptions { PersistSession = false }));
    }

    [Fact]
    public void AllowDangerouslySkipPermissions_AndAgent()
    {
        var cmd = Cmd(new ClaudeAgentOptions
        {
            AllowDangerouslySkipPermissions = true,
            PermissionMode = PermissionMode.BypassPermissions,
            Agent = "reviewer"
        });
        Assert.Contains("--allow-dangerously-skip-permissions", cmd);
        Assert.Equal("bypassPermissions", ValueOf(cmd, "--permission-mode"));
        Assert.Equal("reviewer", ValueOf(cmd, "--agent"));
    }

    [Fact]
    public void DebugFile_TakesPrecedenceOverDebug()
    {
        var both = Cmd(new ClaudeAgentOptions { Debug = true, DebugFile = "/tmp/d.log" });
        Assert.Equal("/tmp/d.log", ValueOf(both, "--debug-file"));
        Assert.DoesNotContain("--debug", both);

        Assert.Contains("--debug", Cmd(new ClaudeAgentOptions { Debug = true }));
    }

    [Fact]
    public void ManagedSettings_ObjectOrJsonString()
    {
        var obj = Cmd(new ClaudeAgentOptions { ManagedSettings = Json("""{"permissions":{"deny":["Bash"]}}""") });
        Assert.Equal("""{"permissions":{"deny":["Bash"]}}""", ValueOf(obj, "--managed-settings"));

        var str = Cmd(new ClaudeAgentOptions { ManagedSettings = Json("\"{\\\"a\\\":1}\"") });
        Assert.Equal("""{"a":1}""", ValueOf(str, "--managed-settings"));
    }

    [Fact]
    public void ProjectConfigRoot_UsesEqualsForm()
    {
        var cmd = Cmd(new ClaudeAgentOptions { ProjectConfigRoot = "/repo/root" });
        Assert.Contains("--project-config-root=/repo/root", cmd);
    }

    [Theory]
    [InlineData(PermissionPromptsMode.Host, "host")]
    [InlineData(PermissionPromptsMode.None, "none")]
    public void PermissionPrompts_Flag(PermissionPromptsMode mode, string expected)
    {
        Assert.Equal(expected, ValueOf(Cmd(new ClaudeAgentOptions { PermissionPrompts = mode }), "--permission-prompts"));
    }

    [Fact]
    public void Channels_OneFlagEach_DashLeadingUsesEquals()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Channels = ["plugin:a", "-weird"] });
        var i = cmd.IndexOf("--channels");
        Assert.Equal("plugin:a", cmd[i + 1]);
        Assert.Contains("--channels=-weird", cmd);
    }

    [Fact]
    public void Workload_RidesExtraArgs()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Workload = "batch" });
        Assert.Equal("batch", ValueOf(cmd, "--workload"));
    }

    [Fact]
    public void Plugins_SkipMcpDiscoveryUsesNoMcpFlag()
    {
        var cmd = Cmd(new ClaudeAgentOptions
        {
            Plugins =
            [
                new SdkPluginConfig("local", "/p1"),
                new SdkPluginConfig("local", "/p2") { SkipMcpDiscovery = true }
            ]
        });
        Assert.Equal("/p1", ValueOf(cmd, "--plugin-dir"));
        Assert.Equal("/p2", ValueOf(cmd, "--plugin-dir-no-mcp"));
    }

    [Fact]
    public async Task PluginDeliveryInitialize_AwaitsInitializeAndSendsPlugins()
    {
        var options = new ClaudeAgentOptions
        {
            PluginDelivery = PluginDelivery.Initialize,
            Plugins = [new SdkPluginConfig("local", "/p1"), new SdkPluginConfig("local", "/p2") { SkipMcpDiscovery = true }]
        };
        var cmd = Cmd(options);
        Assert.Contains("--await-initialize", cmd);
        Assert.DoesNotContain("--plugin-dir", cmd);
        Assert.DoesNotContain("--plugin-dir-no-mcp", cmd);

        var init = await InitializeRequestAsync(options);
        Assert.Equal("""[{"type":"local","path":"/p1"},{"type":"local","path":"/p2","skipMcpDiscovery":true}]""",
            init.GetProperty("plugins").GetRawText());
    }

    [Fact]
    public async Task PluginDeliveryInitialize_WithoutPlugins_IsNoop()
    {
        var options = new ClaudeAgentOptions { PluginDelivery = PluginDelivery.Initialize };
        Assert.DoesNotContain("--await-initialize", Cmd(options));
        Assert.False((await InitializeRequestAsync(options)).TryGetProperty("plugins", out _));
    }

    [Fact]
    public void FallbackModelEqualToModel_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Cmd(new ClaudeAgentOptions { Model = "opus", FallbackModel = "opus" }));
        Assert.Contains("Fallback model cannot be the same", ex.Message);
        Assert.Equal("sonnet", ValueOf(Cmd(new ClaudeAgentOptions { Model = "opus", FallbackModel = "sonnet" }), "--fallback-model"));
        Assert.Equal("opus", ValueOf(Cmd(new ClaudeAgentOptions { FallbackModel = "opus" }), "--fallback-model"));
    }

    [Fact]
    public void ThinkingEnabledWithoutBudget_IsAdaptive()
    {
        var cmd = Cmd(new ClaudeAgentOptions { Thinking = new ThinkingConfigEnabled { Display = "summarized" } });
        Assert.Equal("adaptive", ValueOf(cmd, "--thinking"));
        Assert.DoesNotContain("--max-thinking-tokens", cmd);
        Assert.Equal("summarized", ValueOf(cmd, "--thinking-display"));

        Assert.Equal("2048", ValueOf(Cmd(new ClaudeAgentOptions { Thinking = new ThinkingConfigEnabled(2048) }), "--max-thinking-tokens"));
    }

    [Fact]
    public async Task SystemPromptBlocks_GoThroughInitializeOnly()
    {
        var options = new ClaudeAgentOptions
        {
            SystemPrompt = new[] { "static", SystemPromptConfig.DynamicBoundary, "dynamic" }
        };
        var cmd = Cmd(options);
        Assert.DoesNotContain("--system-prompt", cmd);
        Assert.DoesNotContain("--append-system-prompt", cmd);

        var init = await InitializeRequestAsync(options);
        Assert.Equal("""["static","__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__","dynamic"]""", init.GetProperty("systemPrompt").GetRawText());
        Assert.False(init.TryGetProperty("systemPromptSnapshot", out _));

        var withSnapshot = await InitializeRequestAsync(new ClaudeAgentOptions
        {
            SystemPrompt = new SystemPromptBlocks(["a"]) { Snapshot = false }
        });
        Assert.False(withSnapshot.GetProperty("systemPromptSnapshot").GetBoolean());
    }

    [Fact]
    public async Task StringSystemPrompt_StaysOnArgv()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = "Be terse" };
        Assert.Equal("Be terse", ValueOf(Cmd(options), "--system-prompt"));
        Assert.False((await InitializeRequestAsync(options)).TryGetProperty("systemPrompt", out _));
    }

    [Fact]
    public void Sandbox_EnabledInjectsFailIfUnavailable()
    {
        var enabled = ValueOf(Cmd(new ClaudeAgentOptions { Sandbox = new SandboxSettings { Enabled = true } }), "--settings");
        Assert.Equal("""{"sandbox":{"enabled":true,"failIfUnavailable":true}}""", enabled);

        var optOut = ValueOf(Cmd(new ClaudeAgentOptions { Sandbox = new SandboxSettings { Enabled = true, FailIfUnavailable = false } }), "--settings");
        Assert.Equal("""{"sandbox":{"enabled":true,"failIfUnavailable":false}}""", optOut);

        var disabled = ValueOf(Cmd(new ClaudeAgentOptions { Sandbox = new SandboxSettings { Enabled = false } }), "--settings");
        Assert.Equal("""{"sandbox":{"enabled":false}}""", disabled);
    }

    [Fact]
    public void Sandbox_TsSchemaKeysSerialize()
    {
        var settings = ValueOf(Cmd(new ClaudeAgentOptions
        {
            Sandbox = new SandboxSettings
            {
                Network = new SandboxNetworkConfig
                {
                    StrictAllowlist = true,
                    TlsTerminate = new SandboxTlsTerminate { CaCertPath = "/ca.pem", CaKeyPath = "/ca.key" }
                },
                Filesystem = new SandboxFilesystemConfig { AllowWrite = ["/w"], DenyRead = ["/secret"], Disabled = false },
                Credentials = Json("""{"envVars":[{"name":"TOKEN","mode":"mask"}]}"""),
                EnableWeakerNetworkIsolation = true,
                AllowAppleEvents = false,
                Ripgrep = new SandboxRipgrepConfig { Command = "rg", Args = ["--hidden"] },
                BwrapPath = "/usr/bin/bwrap",
                SocatPath = "/usr/bin/socat",
                AdditionalProperties = new Dictionary<string, JsonElement> { ["futureKey"] = Json("7") }
            }
        }), "--settings");

        var sandbox = JsonNode.Parse(settings)!["sandbox"]!;
        Assert.True(sandbox["network"]!["strictAllowlist"]!.GetValue<bool>());
        Assert.Equal("/ca.pem", sandbox["network"]!["tlsTerminate"]!["caCertPath"]!.GetValue<string>());
        Assert.Equal("/w", sandbox["filesystem"]!["allowWrite"]![0]!.GetValue<string>());
        Assert.False(sandbox["filesystem"]!["disabled"]!.GetValue<bool>());
        Assert.Null(sandbox["filesystem"]!["denyWrite"]);
        Assert.Equal("mask", sandbox["credentials"]!["envVars"]![0]!["mode"]!.GetValue<string>());
        Assert.True(sandbox["enableWeakerNetworkIsolation"]!.GetValue<bool>());
        Assert.False(sandbox["allowAppleEvents"]!.GetValue<bool>());
        Assert.Equal("--hidden", sandbox["ripgrep"]!["args"]![0]!.GetValue<string>());
        Assert.Equal("/usr/bin/bwrap", sandbox["bwrapPath"]!.GetValue<string>());
        Assert.Equal("/usr/bin/socat", sandbox["socatPath"]!.GetValue<string>());
        Assert.Equal(7, sandbox["futureKey"]!.GetValue<int>());
        Assert.Null(sandbox["failIfUnavailable"]); // not enabled
    }

    [Fact]
    public void McpConfigs_TimeoutAlwaysLoadAndToolPolicies()
    {
        var cmd = Cmd(new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, object>
            {
                ["stdio"] = new McpStdioServerConfig { Command = "srv", Timeout = 30000, AlwaysLoad = true },
                ["sse"] = new McpSSEServerConfig
                {
                    Url = "https://s",
                    Timeout = 1000,
                    Tools = [new McpServerToolPolicy { Name = "t", PermissionPolicy = "always_ask", OrgMaxPermission = "ask" }]
                },
                ["http"] = new McpHttpServerConfig { Url = "https://h", Tools = [new McpServerToolPolicy { Name = "u" }] }
            }
        });
        Assert.Equal(
            """{"mcpServers":{"stdio":{"type":"stdio","command":"srv","timeout":30000,"alwaysLoad":true},"sse":{"type":"sse","url":"https://s","tools":[{"name":"t","permission_policy":"always_ask","org_max_permission":"ask"}],"timeout":1000},"http":{"type":"http","url":"https://h","tools":[{"name":"u"}]}}}""",
            ValueOf(cmd, "--mcp-config"));
    }

    #endregion

    #region environment

    [Fact]
    public void Env_SessionStateHandshakeFlag()
    {
        Assert.Equal("1", Env(new ClaudeAgentOptions())[SubprocessTransport.SdkReadsSessionStateEnv]);

        // Caller's value wins, whatever its case, from Env or the inherited environment.
        var fromOptions = Env(new ClaudeAgentOptions
        {
            Env = new Dictionary<string, string> { ["claude_code_sdk_reads_session_state"] = "0" }
        });
        Assert.False(fromOptions.ContainsKey(SubprocessTransport.SdkReadsSessionStateEnv));
        Assert.Equal("0", fromOptions["claude_code_sdk_reads_session_state"]);

        var inherited = Env(new ClaudeAgentOptions(),
            new Dictionary<string, string?> { [SubprocessTransport.SdkReadsSessionStateEnv] = "0" });
        Assert.Equal("0", inherited[SubprocessTransport.SdkReadsSessionStateEnv]);
    }

    [Fact]
    public void Env_AuthRefreshFlagsFollowCallbacks()
    {
        var none = Env(new ClaudeAgentOptions());
        Assert.False(none.ContainsKey("CLAUDE_CODE_SDK_HAS_OAUTH_REFRESH"));
        Assert.False(none.ContainsKey("CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"));

        var both = Env(new ClaudeAgentOptions
        {
            GetOAuthToken = _ => Task.FromResult<OAuthTokenResult?>("t"),
            GetHostAuthToken = _ => Task.FromResult<string?>("h")
        });
        Assert.Equal("1", both["CLAUDE_CODE_SDK_HAS_OAUTH_REFRESH"]);
        Assert.Equal("1", both["CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"]);
    }

    [Fact]
    public void Env_AskUserQuestionToolConfig()
    {
        var env = Env(new ClaudeAgentOptions
        {
            ToolConfig = new ToolConfig
            {
                AskUserQuestion = new AskUserQuestionConfig
                {
                    PreviewFormat = AskUserQuestionPreviewFormat.Html,
                    ExtendedQuestions = true
                }
            }
        }, new Dictionary<string, string?> { ["claude_code_question_optional_descriptions"] = "1" });
        Assert.Equal("html", env["CLAUDE_CODE_QUESTION_PREVIEW_FORMAT"]);
        Assert.Equal("1", env["CLAUDE_CODE_QUESTION_EXTENDED"]);
        // Not requested: the inherited value (any case) is removed.
        Assert.DoesNotContain(env.Keys, k => k.Equals("CLAUDE_CODE_QUESTION_OPTIONAL_DESCRIPTIONS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Env_QuestionFlagsSetByCallerAreKept()
    {
        var env = Env(new ClaudeAgentOptions
        {
            Env = new Dictionary<string, string> { ["CLAUDE_CODE_QUESTION_EXTENDED"] = "1" }
        });
        Assert.Equal("1", env["CLAUDE_CODE_QUESTION_EXTENDED"]);
        Assert.False(env.ContainsKey("CLAUDE_CODE_QUESTION_PREVIEW_FORMAT"));
    }

    [Fact]
    public void Env_InheritEnvironmentFalseReplacesTheEnvironment()
    {
        var inherited = new Dictionary<string, string?> { ["HOME"] = "/home/x", ["SECRET"] = "s" };
        var env = Env(new ClaudeAgentOptions
        {
            InheritEnvironment = false,
            Env = new Dictionary<string, string> { ["ONLY"] = "me" }
        }, inherited);
        Assert.False(env.ContainsKey("HOME"));
        Assert.False(env.ContainsKey("SECRET"));
        Assert.Equal("me", env["ONLY"]);
        Assert.Equal("sdk-dotnet", env["CLAUDE_CODE_ENTRYPOINT"]);
        Assert.True(env.ContainsKey("CLAUDE_AGENT_SDK_VERSION"));

        var merged = Env(new ClaudeAgentOptions(), new Dictionary<string, string?> { ["HOME"] = "/home/x" });
        Assert.Equal("/home/x", merged["HOME"]);
    }

    #endregion

    #region initialize keys

    [Fact]
    public async Task Initialize_DefaultsSendNoTsKeys()
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions());
        foreach (var key in new[]
                 {
                     "systemPrompt", "jsonSchema", "title", "planModeInstructions", "appendSubagentSystemPrompt",
                     "toolAliases", "webSearchIsolationExemptMcpServers", "promptSuggestions", "agentProgressSummaries",
                     "supportedDialogKinds", "perTaskStopAffordance", "rapidFollowupPreempt", "workspaceTrust",
                     "sdkMcpServerConfigs", "plugins"
                 })
            Assert.False(init.TryGetProperty(key, out _), key);
    }

    [Fact]
    public async Task Initialize_SendsTsKeys()
    {
        var options = new ClaudeAgentOptions
        {
            Title = "My session",
            PlanModeInstructions = "Plan carefully",
            AppendSubagentSystemPrompt = "Be brief",
            ToolAliases = new Dictionary<string, string> { ["Bash"] = "mcp__x__bash" },
            WebSearchIsolationExemptMcpServers = ["srv"],
            PromptSuggestions = true,
            AgentProgressSummaries = false,
            SupportedDialogKinds = ["refusal_fallback_prompt"],
            OnUserDialog = (_, _, _) => Task.FromResult<UserDialogResult?>(null),
            PerTaskStopAffordance = true,
            RapidFollowupPreempt = true,
            WorkspaceTrust = new WorkspaceTrust(true, "/abs/dir"),
            OutputFormat = Json("""{"type":"json_schema","schema":{"type":"object"}}""")
        };
        var init = await InitializeRequestAsync(options);

        Assert.Equal("My session", init.GetProperty("title").GetString());
        Assert.Equal("Plan carefully", init.GetProperty("planModeInstructions").GetString());
        Assert.Equal("Be brief", init.GetProperty("appendSubagentSystemPrompt").GetString());
        Assert.Equal("""{"Bash":"mcp__x__bash"}""", init.GetProperty("toolAliases").GetRawText());
        Assert.Equal("""["srv"]""", init.GetProperty("webSearchIsolationExemptMcpServers").GetRawText());
        Assert.True(init.GetProperty("promptSuggestions").GetBoolean());
        Assert.False(init.GetProperty("agentProgressSummaries").GetBoolean());
        Assert.Equal("""["refusal_fallback_prompt"]""", init.GetProperty("supportedDialogKinds").GetRawText());
        Assert.True(init.GetProperty("perTaskStopAffordance").GetBoolean());
        Assert.True(init.GetProperty("rapidFollowupPreempt").GetBoolean());
        Assert.Equal("""{"accepted":true,"directory":"/abs/dir"}""", init.GetProperty("workspaceTrust").GetRawText());
        Assert.Equal("""{"type":"object"}""", init.GetProperty("jsonSchema").GetRawText());
        // --json-schema stays on argv.
        Assert.Equal("""{"type":"object"}""", ValueOf(Cmd(options), "--json-schema"));
    }

    [Theory]
    [InlineData(true, "relative/dir", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "C:\\work", true)]
    [InlineData(false, "relative", true)]
    public async Task Initialize_WorkspaceTrustNeedsAbsoluteDirWhenAccepted(bool accepted, string? dir, bool sent)
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions { WorkspaceTrust = new WorkspaceTrust(accepted, dir) });
        Assert.Equal(sent, init.TryGetProperty("workspaceTrust", out _));
    }

    [Fact]
    public async Task Initialize_SdkServerTimeouts()
    {
        var registry = McpServers.Sdk("calc", s => s.Tool("t", Json("""{"type":"object"}"""), (_, _) => Task.FromResult(McpToolResults.Text("x"))));
        var withTimeout = ((McpSdkServerConfig)registry["calc"]) with { Timeout = 9000 };
        var init = await InitializeRequestAsync(new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, object>
            {
                ["calc"] = withTimeout,
                ["plain"] = ((McpSdkServerConfig)registry["calc"]) with { Name = "plain" },
                ["ext"] = new McpStdioServerConfig { Command = "x", Timeout = 5 }
            }
        });
        Assert.Equal("""{"calc":{"timeout":9000}}""", init.GetProperty("sdkMcpServerConfigs").GetRawText());
    }

    [Fact]
    public async Task Initialize_AgentDefinitionTsKeys()
    {
        var init = await InitializeRequestAsync(new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["a"] = new("d", "p")
                {
                    CriticalSystemReminderExperimental = "remember",
                    OmitClaudeMd = true,
                    Observer = "watcher",
                    ObserverMessage = "note"
                },
                ["b"] = new("d2", "p2")
            }
        });
        var a = init.GetProperty("agents").GetProperty("a");
        Assert.Equal("remember", a.GetProperty("criticalSystemReminder_EXPERIMENTAL").GetString());
        Assert.True(a.GetProperty("omitClaudeMd").GetBoolean());
        Assert.Equal("watcher", a.GetProperty("observer").GetString());
        Assert.Equal("note", a.GetProperty("observerMessage").GetString());
        Assert.Equal("""{"description":"d2","prompt":"p2"}""", init.GetProperty("agents").GetProperty("b").GetRawText());
    }

    #endregion

    #region validation

    [Fact]
    public async Task PersistSessionFalseWithSessionStore_Throws()
    {
        var options = new ClaudeAgentOptions { PersistSession = false, SessionStore = new InMemorySessionStore() };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new ClaudeSDKClient(options, new FakeTransport()).ConnectAsync());
        Assert.Contains("PersistSession", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in Claude.QueryAsync("x", options, new FakeTransport())) { }
        });
    }

    [Fact]
    public async Task SupportedDialogKindsWithoutHandler_Throws()
    {
        var options = new ClaudeAgentOptions { SupportedDialogKinds = ["k"] };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new ClaudeSDKClient(options, new FakeTransport()).ConnectAsync());
        Assert.Contains("OnUserDialog", ex.Message);

        // An empty list needs no handler.
        await using var ok = new ClaudeSDKClient(new ClaudeAgentOptions { SupportedDialogKinds = [] }, new FakeTransport());
        await ok.ConnectAsync();
    }

    #endregion

    #region builder

    [Fact]
    public async Task Builder_CoversTsOptions()
    {
        ElicitationCallback elicit = (_, _, _) => Task.FromResult<ElicitationResult?>(null);
        UserDialogCallback dialog = (_, _, _) => Task.FromResult<UserDialogResult?>(null);
        OAuthTokenCallback oauth = _ => Task.FromResult<OAuthTokenResult?>(null);
        HostAuthTokenCallback host = _ => Task.FromResult<string?>(null);
        Action<JsonElement> tap = _ => { };

        var o = Claude.Options()
            .Model("opus")
            .OnElicitation(elicit)
            .OnUserDialog(dialog, "k1", "k2")
            .GetOAuthToken(oauth)
            .GetHostAuthToken(host)
            .OnRawMessage(tap)
            .PersistSession(false)
            .AllowDangerouslySkipPermissions()
            .Agent("main")
            .Debug()
            .DebugFile("/d.log")
            .ManagedSettings(new Dictionary<string, object?> { ["x"] = 1 })
            .Settings(new Dictionary<string, object?> { ["model"] = "m" })
            .ProjectConfigRoot("/root")
            .PermissionPrompts(PermissionPromptsMode.Host)
            .PluginDelivery(PluginDelivery.Initialize)
            .Plugin("/p", skipMcpDiscovery: true)
            .Channels("c1")
            .Workload("w")
            .AskUserQuestion(AskUserQuestionPreviewFormat.Html, extendedQuestions: true)
            .InheritEnvironment(false)
            .SystemPromptBlocks("a", "b")
            .Title("t")
            .PlanModeInstructions("plan")
            .ToolAlias("Bash", "mcp__x__bash")
            .ToolAlias("Read", "mcp__x__read")
            .PromptSuggestions()
            .AgentProgressSummaries()
            .PerTaskStopAffordance()
            .AppendSubagentSystemPrompt("sub")
            .WebSearchIsolationExemptMcpServers("srv")
            .RapidFollowupPreempt()
            .WorkspaceTrust("/abs")
            .Build();

        Assert.Equal("opus", o.Model);
        Assert.Same(elicit, o.OnElicitation);
        Assert.Same(dialog, o.OnUserDialog);
        Assert.Equal(["k1", "k2"], o.SupportedDialogKinds!);
        Assert.Same(oauth, o.GetOAuthToken);
        Assert.Same(host, o.GetHostAuthToken);
        Assert.Same(tap, o.OnRawMessage);
        Assert.False(o.PersistSession);
        Assert.True(o.AllowDangerouslySkipPermissions);
        Assert.Equal("main", o.Agent);
        Assert.True(o.Debug);
        Assert.Equal("/d.log", o.DebugFile);
        Assert.Equal("""{"x":1}""", o.ManagedSettings!.Value.GetRawText());
        Assert.Equal("""{"model":"m"}""", o.Settings);
        Assert.Equal("/root", o.ProjectConfigRoot);
        Assert.Equal(PermissionPromptsMode.Host, o.PermissionPrompts);
        Assert.Equal(PluginDelivery.Initialize, o.PluginDelivery);
        Assert.True(Assert.Single(o.Plugins).SkipMcpDiscovery);
        Assert.Equal(["c1"], o.Channels!);
        Assert.Equal("w", o.Workload);
        Assert.Equal(AskUserQuestionPreviewFormat.Html, o.ToolConfig!.AskUserQuestion!.PreviewFormat);
        Assert.True(o.ToolConfig.AskUserQuestion.ExtendedQuestions);
        Assert.False(o.InheritEnvironment);
        Assert.Equal(["a", "b"], Assert.IsType<SystemPromptBlocks>(o.SystemPrompt).Blocks);
        Assert.Equal("t", o.Title);
        Assert.Equal("plan", o.PlanModeInstructions);
        Assert.Equal(2, o.ToolAliases!.Count);
        Assert.True(o.PromptSuggestions);
        Assert.True(o.AgentProgressSummaries);
        Assert.True(o.PerTaskStopAffordance);
        Assert.Equal("sub", o.AppendSubagentSystemPrompt);
        Assert.Equal(["srv"], o.WebSearchIsolationExemptMcpServers!);
        Assert.True(o.RapidFollowupPreempt);
        Assert.Equal(new WorkspaceTrust(true, "/abs"), o.WorkspaceTrust);

        // The built options produce the TS initialize keys.
        var init = await InitializeRequestAsync(o);
        Assert.Equal("t", init.GetProperty("title").GetString());
    }

    [Fact]
    public void Builder_SettingsJsonElement()
    {
        var o = Claude.Options().Settings(Json("""{"a":true}""")).Build();
        Assert.Equal("""{"a":true}""", o.Settings);
    }

    #endregion
}

/// <summary>TS-only session listing switches against a temp CLAUDE_CONFIG_DIR.</summary>
[Collection(ClaudeConfigDirCollection.Name)]
public sealed class TsSessionParityTests : IDisposable
{
    private readonly string? _prevConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    private readonly string _configDir = SessTestUtil.TempDir("claude_cfg_ts_");
    private readonly string _projectDir = SessTestUtil.TempDir("claude_proj_ts_");
    private readonly string _projectStoreDir;

    public TsSessionParityTests()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _configDir);
        _projectStoreDir = Path.Combine(_configDir, "projects", SessionPaths.ProjectKeyForDirectory(_projectDir));
        Directory.CreateDirectory(_projectStoreDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _prevConfigDir);
        try { Directory.Delete(_configDir, true); } catch { }
        try { Directory.Delete(_projectDir, true); } catch { }
    }

    private void WriteSession(string sid, IEnumerable<JsonObject> entries) =>
        File.WriteAllText(Path.Combine(_projectStoreDir, sid + ".jsonl"),
            string.Join("\n", entries.Select(e => e.ToJsonString())) + "\n");

    [Fact]
    public void ListSessions_IncludeProgrammaticFalse_HidesSdkAndDaemonSessions()
    {
        var human = Guid.NewGuid().ToString();
        var sdk = Guid.NewGuid().ToString();
        var dotnet = Guid.NewGuid().ToString();
        var daemon = Guid.NewGuid().ToString();

        WriteSession(human, ParityUtil.Chain(human, 1));
        var sdkChain = ParityUtil.Chain(sdk, 1);
        sdkChain[0]["entrypoint"] = "sdk-ts";
        WriteSession(sdk, sdkChain);
        var dotnetChain = ParityUtil.Chain(dotnet, 1);
        dotnetChain[0]["entrypoint"] = "sdk-dotnet";
        WriteSession(dotnet, dotnetChain);
        var daemonChain = ParityUtil.Chain(daemon, 1);
        daemonChain[0]["sessionKind"] = "daemon-worker";
        WriteSession(daemon, daemonChain);

        Assert.Equal(4, ClaudeSessions.ListSessions(_projectDir).Count); // default: included
        var visible = ClaudeSessions.ListSessions(_projectDir, includeProgrammatic: false);
        Assert.Equal(human, Assert.Single(visible).SessionId);
        Assert.Contains(ClaudeSessions.ListSessions(includeProgrammatic: false), s => s.SessionId == human);
        Assert.DoesNotContain(ClaudeSessions.ListSessions(includeProgrammatic: false), s => s.SessionId == sdk);
    }

    [Fact]
    public async Task GetSessionMessages_IncludeSystemMessages()
    {
        var sid = Guid.NewGuid().ToString();
        var chain = ParityUtil.Chain(sid, 1);
        var system = new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "compact_boundary",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["parentUuid"] = (string)chain[1]["uuid"]!,
            ["sessionId"] = sid,
            ["timestamp"] = "2024-01-01T00:00:02.000Z",
            ["content"] = "Conversation compacted"
        };
        var after = ParityUtil.User("next", Guid.NewGuid().ToString(), (string)system["uuid"]!, sid);
        chain.Add(system);
        chain.Add(after);
        WriteSession(sid, chain);

        var plain = ClaudeSessions.GetSessionMessages(sid, _projectDir);
        Assert.DoesNotContain(plain, m => m.Type == "system");
        Assert.Equal(3, plain.Count);

        var withSystem = ClaudeSessions.GetSessionMessages(sid, _projectDir, includeSystemMessages: true);
        Assert.Equal(["user", "assistant", "system", "user"], withSystem.Select(m => m.Type));

        var store = new InMemorySessionStore();
        await store.AppendAsync(
            new SessionKey { ProjectKey = SessionPaths.ProjectKeyForDirectory(_projectDir), SessionId = sid },
            chain.Select(ParityUtil.E).ToList());
        var fromStore = await ClaudeSessions.GetSessionMessagesAsync(store, sid, _projectDir, null, 0, includeSystemMessages: true);
        Assert.Equal(["user", "assistant", "system", "user"], fromStore.Select(m => m.Type));
        Assert.DoesNotContain(await ClaudeSessions.GetSessionMessagesAsync(store, sid, _projectDir), m => m.Type == "system");
    }
}
