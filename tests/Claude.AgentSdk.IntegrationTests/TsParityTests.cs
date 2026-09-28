using System.Collections.Concurrent;
using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>
/// TypeScript SDK (0.3.283) parity features against the real CLI: typed initialize
/// info, the TS-only control requests, tool aliases, raw frames, new hook events,
/// can_use_tool echo fields and the TS-only options. Most tests make no model call
/// (control requests only); the rest use one short haiku turn.
/// </summary>
[Trait("Category", "Integration")]
public class TsParityTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    private async Task<ClaudeSDKClient> ConnectAsync(ClaudeAgentOptions options, string test)
    {
        var client = new ClaudeSDKClient(options, Transport(options, test));
        await client.ConnectAsync(cancellationToken: Ct);
        return client;
    }

    // ---------------------------------------------------------------- info (no model call)

    [IntegrationFact]
    public async Task InitializationResult_TypedInfoApis_ReturnSaneValues()
    {
        var options = Options();
        await using var client = await ConnectAsync(options, nameof(InitializationResult_TypedInfoApis_ReturnSaneValues));

        var init = await client.InitializationResultAsync(Ct);
        Assert.Equal(JsonValueKind.Object, init.Raw.ValueKind);
        Log("initialize keys: " + string.Join(",", init.Raw.EnumerateObject().Select(p => p.Name)));

        var commands = await client.SupportedCommandsAsync(Ct);
        Log($"commands: {commands.Count} (e.g. {string.Join(", ", commands.Take(5).Select(c => c.Name))})");
        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));

        var models = await client.SupportedModelsAsync(Ct);
        Log($"models: {string.Join(", ", models.Select(m => m.Value))}");
        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.False(string.IsNullOrWhiteSpace(m.Value)));
        Assert.Contains(models, m => !string.IsNullOrWhiteSpace(m.DisplayName));

        var agents = await client.SupportedAgentsAsync(Ct);
        Log($"agents: {string.Join(", ", agents.Select(a => a.Name))}");
        Assert.NotEmpty(agents);
        Assert.All(agents, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));

        var account = await client.AccountInfoAsync(Ct);
        Assert.NotNull(account);
        // No identity assertions: only that some account facts were parsed.
        Assert.True(account!.TokenSource is not null || account.ApiKeySource is not null ||
                    account.SubscriptionType is not null || account.Email is not null,
            "account info has no recognizable fields");

        Assert.NotEmpty(init.AvailableOutputStyles);
    }

    [IntegrationFact]
    public async Task GetUsage_ReturnsParseableReport()
    {
        var options = Options();
        await using var client = await ConnectAsync(options, nameof(GetUsage_ReturnsParseableReport));

#pragma warning disable CLAUDESDK001
        var usage = await client.GetUsageAsync(skipBehaviors: true, Ct);
#pragma warning restore CLAUDESDK001
        Log("get_usage keys: " + string.Join(",", usage.Raw.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(JsonValueKind.Object, usage.Raw.ValueKind);
        Assert.True(usage.Session.HasValue, "get_usage has no session section");
        Assert.Equal(JsonValueKind.Object, usage.Session!.Value.ValueKind);
        if (usage.RateLimitsAvailable)
            Assert.NotNull(usage.RateLimits);
    }

    // ------------------------------------------------------- control requests (no model call)

    [IntegrationFact]
    public async Task SettingsAndReloadControls_AreAcceptedByCli()
    {
        var options = Options();
        await using var client = await ConnectAsync(options, nameof(SettingsAndReloadControls_AreAcceptedByCli));

        await client.SetMaxThinkingTokensAsync(2048, cancellationToken: Ct);
        await client.SetMaxThinkingTokensAsync(4096, ThinkingDisplayMode.Summarized, cancellationToken: Ct);
        await client.SetMaxThinkingTokensAsync(null, clearThinkingDisplay: true, cancellationToken: Ct);

        await client.ApplyFlagSettingsAsync(new Dictionary<string, object?>
        {
            ["permissions"] = new Dictionary<string, object?> { ["deny"] = new[] { "WebFetch" } },
        }, Ct);
        var settings = await client.SendControlRequestAsync("get_settings", cancellationToken: Ct);
        Log("get_settings: " + Truncate(settings.GetRawText(), 400));
        Assert.Contains("WebFetch", settings.GetRawText());

        var skills = await client.ReloadSkillsAsync(Ct);
        Log($"reload_skills: {skills.Count} skills");
        var styles = await client.ReloadOutputStylesAsync(Ct);
        Log($"reload_output_styles: {string.Join(", ", styles)}");
        Assert.NotEmpty(styles);

        var backgrounded = await client.BackgroundTasksAsync(cancellationToken: Ct);
        Log($"background_tasks: {backgrounded}");

        var plugins = await client.ReloadPluginsAsync(cancellationToken: Ct);
        Log($"reload_plugins: errors={plugins.ErrorCount} raw keys=" +
            string.Join(",", plugins.Raw.EnumerateObject().Select(p => p.Name)));

        var warning = await client.SetMcpPermissionModeOverrideAsync("no-such-server", McpPermissionModeOverride.Default, Ct);
        Log($"mcp_permission_mode_override warning: {warning}");
        await client.SetMcpPermissionModeOverrideAsync("no-such-server", null, Ct);

        var reinit = await client.ReinitializeAsync(Ct);
        Assert.NotEmpty(reinit.Models);
    }

    [IntegrationFact]
    public async Task UpdateSettings_LocalOutputStyle_IsWrittenIntoProjectSettings()
    {
        // The CLI refuses to write a source that --setting-sources disabled; the local
        // source of the temp cwd is still hermetic.
        var options = Options() with { SettingSources = [SettingSource.Local] };
        await using var client = await ConnectAsync(options, nameof(UpdateSettings_LocalOutputStyle_IsWrittenIntoProjectSettings));

        await client.UpdateSettingsAsync(SettingsFileSource.LocalSettings,
            new Dictionary<string, string> { ["outputStyle"] = "Explanatory" }, Ct);

        var file = Path.Combine(Cwd, ".claude", "settings.local.json");
        Assert.True(File.Exists(file), $"{file} was not written");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("Explanatory", doc.RootElement.GetProperty("outputStyle").GetString());
    }

    [IntegrationFact]
    public async Task ReadFile_AndSeedReadState_Work()
    {
        var nonce = NewNonce("READ");
        var path = Path.Combine(Cwd, "notes.txt");
        File.WriteAllText(path, $"line one\n{nonce}\n");
        var options = Options();
        await using var client = await ConnectAsync(options, nameof(ReadFile_AndSeedReadState_Work));

        var read = await client.ReadFileAsync("notes.txt", cancellationToken: Ct);
        Assert.NotNull(read);
        Assert.Contains(nonce, read!.Contents);
        Assert.Equal(path, SessionPaths.CanonicalizePath(read.AbsPath));

        var b64 = await client.ReadFileAsync(path, maxBytes: 4, encoding: ReadFileEncoding.Base64, cancellationToken: Ct);
        Assert.NotNull(b64);
        Log($"base64 read: {b64!.Contents} truncated={b64.Truncated} encoding={b64.Encoding}");
        Assert.Equal("line", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64.Contents)));

        Assert.Null(await client.ReadFileAsync(Path.Combine(Cwd, "missing.txt"), cancellationToken: Ct));

        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        await client.SeedReadStateAsync(path, mtime, Ct);
    }

    [IntegrationFact]
    public async Task GetContextUsage_SummaryAndFull_ReturnSaneShape()
    {
        var options = Options();
        await using var client = await ConnectAsync(options, nameof(GetContextUsage_SummaryAndFull_ReturnSaneShape));

        var summary = await client.GetContextUsageAsync(ContextUsageDetail.Summary, Ct);
        var full = await client.GetContextUsageAsync(ContextUsageDetail.Full, Ct);
        foreach (var u in new[] { summary, full })
        {
            Log($"context usage: total={u.TotalTokens} max={u.MaxTokens} model={u.Model} categories={u.Categories.Count}");
            Assert.True(u.MaxTokens > 0);
            Assert.False(string.IsNullOrEmpty(u.Model));
        }
        Assert.NotEmpty(full.Categories);
    }

    [IntegrationFact]
    public async Task Interrupt_WhenIdle_ReturnsReceipt_AndCancelQueuedIsAccepted()
    {
        var options = Options();
        await using var client = await ConnectAsync(options, nameof(Interrupt_WhenIdle_ReturnsReceipt_AndCancelQueuedIsAccepted));

        var receipt = await client.InterruptAsync(cancelQueued: false, Ct);
        Log($"receipt: {(receipt is null ? "null" : $"still_queued={receipt.StillQueued.Count} cancelled={receipt.Cancelled?.Count}")}");
        Assert.NotNull(receipt);
        Assert.Empty(receipt!.StillQueued);

        var withCancel = await client.InterruptAsync(cancelQueued: true, Ct);
        Assert.NotNull(withCancel);
        Assert.Empty(withCancel!.StillQueued);
        Assert.NotNull(withCancel.Cancelled);
    }

    /// <summary>
    /// A dependency-free stdio MCP server (python3) with one tool, <c>ask</c>, that sends
    /// <c>elicitation/create</c> while the call is running, writes the client's answer to
    /// <paramref name="answerFile"/> and returns the chosen action as the tool result.
    /// (The CLI declares no client capabilities to in-process SDK servers, and answers
    /// elicitations outside a tool call with "cancel" itself, so this needs a stdio server
    /// and one model turn.)
    /// </summary>
    private static string WriteElicitingServer(string dir, string answerFile)
    {
        var script = Path.Combine(dir, "elicit_server.py");
        File.WriteAllText(script, """
            import json, sys
            def send(o):
                sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
            pending = None
            for line in sys.stdin:
                m = json.loads(line)
                meth = m.get("method")
                if meth == "initialize":
                    send({"jsonrpc": "2.0", "id": m["id"], "result": {
                        "protocolVersion": m["params"]["protocolVersion"],
                        "capabilities": {"tools": {}},
                        "serverInfo": {"name": "asker", "version": "1.0"}}})
                elif meth == "tools/list":
                    send({"jsonrpc": "2.0", "id": m["id"], "result": {"tools": [{
                        "name": "ask", "description": "Ask the user to pick a color.",
                        "inputSchema": {"type": "object", "properties": {}}}]}})
                elif meth == "tools/call":
                    pending = m["id"]
                    send({"jsonrpc": "2.0", "id": "elicit-1", "method": "elicitation/create", "params": {
                        "message": "Pick a color",
                        "requestedSchema": {"type": "object", "properties": {"color": {"type": "string"}}}}})
                elif meth is None and m.get("id") == "elicit-1":
                    open(ANSWER_FILE, "w").write(json.dumps(m))
                    action = m.get("result", {}).get("action", "error")
                    send({"jsonrpc": "2.0", "id": pending, "result": {
                        "content": [{"type": "text", "text": "user action: " + action}]}})
                elif "id" in m and meth is not None:
                    send({"jsonrpc": "2.0", "id": m["id"], "result": {}})
            """.Replace("ANSWER_FILE", JsonSerializer.Serialize(answerFile), StringComparison.Ordinal));
        return script;
    }

    private static string? FindPython() =>
        new[] { "/usr/bin/python3", "/opt/homebrew/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists);

    private async Task<(JsonDocument? Answer, List<Message> Messages)> RunElicitationAsync(
        ElicitationCallback? onElicitation, string test)
    {
        var answerFile = Path.Combine(Cwd, "answer.json");
        var options = Options(test) with
        {
            McpServers = new Dictionary<string, object>
            {
                ["asker"] = new McpStdioServerConfig { Command = FindPython()!, Args = [WriteElicitingServer(Cwd, answerFile)] },
            },
            AllowedTools = ["mcp__asker__ask"],
            OnElicitation = onElicitation,
        };
        await using var client = new ClaudeSDKClient(options, Transport(options, test));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync("Call the mcp__asker__ask tool once, then reply with only its result text.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "mcp__asker__ask"), "model never called mcp__asker__ask");
        if (!File.Exists(answerFile)) return (null, messages);
        Log("server received: " + File.ReadAllText(answerFile));
        return (JsonDocument.Parse(File.ReadAllText(answerFile)), messages);
    }

    [IntegrationFact]
    public Task Elicitation_FromMcpServer_ReachesCallback_AndAnswerReachesServer() => ModelCompliance.RetryOnceAsync(async _ =>
    {
        if (FindPython() is null)
        {
            Log("python3 not found; nothing to test");
            return;
        }
        var elicitations = new ConcurrentQueue<ElicitationRequest>();
        var (answer, _) = await RunElicitationAsync((req, _, _) =>
        {
            elicitations.Enqueue(req);
            return Task.FromResult<ElicitationResult?>(new ElicitationResult(ElicitationAction.Accept,
                JsonSerializer.SerializeToElement(new { color = "teal" })));
        }, nameof(Elicitation_FromMcpServer_ReachesCallback_AndAnswerReachesServer));

        var req = Assert.Single(elicitations);
        Assert.Equal("asker", req.ServerName);
        Assert.Equal("Pick a color", req.Message);
        Assert.NotNull(answer);
        using var _d = answer;
        var result = answer!.RootElement.GetProperty("result");
        Assert.Equal("accept", result.GetProperty("action").GetString());
        Assert.Equal("teal", result.GetProperty("content").GetProperty("color").GetString());
    }, Log);

    [IntegrationFact]
    public Task Elicitation_WithoutCallback_IsDeclined_AndSessionFinishes() => ModelCompliance.RetryOnceAsync(async _ =>
    {
        if (FindPython() is null)
        {
            Log("python3 not found; nothing to test");
            return;
        }
        var (answer, messages) = await RunElicitationAsync(null, nameof(Elicitation_WithoutCallback_IsDeclined_AndSessionFinishes));

        Assert.NotNull(answer);
        using var _d = answer;
        Assert.Equal("decline", answer!.RootElement.GetProperty("result").GetProperty("action").GetString());
        Assert.Equal("success", SingleResult(messages).Subtype);
    }, Log);

    // ------------------------------------------------------------------ model turns

    private static McpServerRegistry EchoServer(string name, ConcurrentQueue<string> calls, string tool = "echo",
        string param = "text") =>
        McpServers.Sdk(name, s => s.Tool(tool,
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object> { [param] = new Dictionary<string, string> { ["type"] = "string" } },
                ["required"] = new[] { param },
            }),
            (args, _) =>
            {
                var value = args.TryGetProperty(param, out var v) ? v.GetString() ?? "" : "";
                calls.Enqueue(value);
                return Task.FromResult(McpToolResults.Text($"echoed:{value}"));
            },
            "Echo the given text back."));

    [IntegrationFact]
    public Task ToolAliases_BuiltInBashRoutesToSdkTool() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var nonce = NewNonce("ALIAS");
        var calls = new ConcurrentQueue<string>();
        var options = Options() with
        {
            Tools = ["Bash"],
            McpServers = EchoServer("ws", calls, tool: "bash", param: "command"),
            AllowedTools = ["mcp__ws__bash", "Bash"],
            ToolAliases = new Dictionary<string, string> { ["Bash"] = "mcp__ws__bash" },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Bash tool exactly once to run the command: echo {nonce}. " +
                                "Then reply with only the tool's output.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        var use = ToolUses(messages).FirstOrDefault(t => t.Name == "Bash");
        ModelCompliance.Require(use is not null, "model never called Bash");
        // The call the model addressed to Bash landed in the SDK tool, not the built-in shell.
        Assert.Contains(calls, c => c.Contains(nonce, StringComparison.Ordinal));
        var result = Assert.Single(ToolResults(messages), r => r.ToolUseId == use!.Id);
        Assert.Contains($"echoed:", ToolResultText(result));
    }, Log);

    [IntegrationFact]
    public Task RawFrames_OnMessagesEqualTheFrame_AndEveryFrameIsTapped() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var frames = new ConcurrentQueue<JsonElement>();
        var calls = new ConcurrentQueue<string>();
        var options = Options() with
        {
            McpServers = EchoServer("e", calls),
            AllowedTools = ["mcp__e__echo"],
            OnRawMessage = f => frames.Enqueue(f.Clone()),
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync("Call mcp__e__echo once with text=\"hi\", then reply with only DONE.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "mcp__e__echo"), "model never called mcp__e__echo");

        var types = frames.Select(f => f.TryGetProperty("type", out var t) ? t.GetString() : "?").ToList();
        Log("frame types: " + string.Join(",", types.GroupBy(t => t).Select(g => $"{g.Key}x{g.Count()}")));
        // Control traffic is tapped too (initialize response, mcp_message requests).
        Assert.Contains("control_response", types);
        Assert.Contains("control_request", types);

        foreach (var m in messages)
        {
            Assert.Equal(JsonValueKind.Object, m.Raw.ValueKind);
            Assert.True(frames.Any(f => JsonElement.DeepEquals(f, m.Raw)),
                $"{m.GetType().Name}.Raw does not equal any tapped frame");
        }
        Assert.Contains(messages, m => m is AssistantMessage);
        Assert.Contains(messages, m => m is UserMessage);
        Assert.Contains(messages, m => m is ResultMessage);
        var unknown = messages.OfType<UnknownMessage>().Select(u => u.Type).ToList();
        Log("unknown message types: " + string.Join(",", unknown));
    }, Log);

    [IntegrationFact]
    public Task RewindFiles_DryRun_ReportsFileChange() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var target = Path.Combine(Cwd, $"rewind-{attempt}.txt");
        var options = Options() with
        {
            Tools = ["Write"],
            PermissionMode = PermissionMode.AcceptEdits,
            EnableFileCheckpointing = true,
            ExtraArgs = new Dictionary<string, string?> { ["replay-user-messages"] = null },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create {target} containing exactly: hello", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(File.Exists(target), "model never wrote the file");

        var userUuid = messages.OfType<UserMessage>().Select(u => u.Uuid).FirstOrDefault(u => u is not null);
        Assert.NotNull(userUuid);

        var preview = await client.RewindFilesAsync(userUuid!, dryRun: true, Ct);
        Log("rewind dry-run: " + preview.Raw.GetRawText());
        Assert.True(preview.CanRewind, preview.Error);
        Assert.NotNull(preview.FilesChanged);
        Assert.Contains(preview.FilesChanged!, f => f.EndsWith(Path.GetFileName(target), StringComparison.Ordinal));
        // Dry run: nothing changed on disk.
        Assert.True(File.Exists(target));
    }, Log);

    [IntegrationFact]
    public Task SetMcpServers_AddsSdkServerMidSession_AndModelCallsIt() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var nonce = NewNonce("LATE");
        var calls = new ConcurrentQueue<string>();
        var options = Options() with { AllowedTools = ["mcp__late__echo"] };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);

        var set = await client.SetMcpServersAsync(EchoServer("late", calls), Ct);
        Log($"mcp_set_servers: added=[{string.Join(",", set.Added)}] errors={set.Errors.Count}");
        Assert.Contains("late", set.Added);
        Assert.Empty(set.Errors);

        await client.QueryAsync($"Call the mcp__late__echo tool once with text=\"{nonce}\", then reply with only DONE.",
            cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "mcp__late__echo"), "model never called mcp__late__echo");
        Assert.Contains(nonce, calls);
    }, Log);

    [IntegrationFact]
    public async Task NewHookEvents_PostToolBatch_IsDeliveredAndParsesTyped()
    {
        var seen = new ConcurrentQueue<BaseHookInput>();
        Task<HookOutput> Record(JsonElement input, string? _, HookContext __, CancellationToken ___)
        {
            seen.Enqueue(HookInput.Parse(input));
            return Task.FromResult(new HookOutput());
        }
        var calls = new ConcurrentQueue<string>();
        // SessionStart is registered too, but CLI 2.1.283 runs SessionStart hooks at process
        // start, before the initialize request registers SDK callbacks, so it never reaches
        // an SDK callback (the TS SDK behaves the same way). Only logged, not asserted.
        HookEvent[] events = [HookEvent.SessionStart, HookEvent.UserPromptSubmit, HookEvent.PostToolBatch, HookEvent.Stop];
        var options = Options() with
        {
            McpServers = EchoServer("e", calls),
            AllowedTools = ["mcp__e__echo"],
            Hooks = events.ToDictionary(e => e, e => (IReadOnlyList<HookMatcher>)[new HookMatcher(Matcher: null, Hooks: [Record])]),
        };

        await ModelCompliance.RetryOnceAsync(async attempt =>
        {
            seen.Clear();
            await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
            await client.ConnectAsync(cancellationToken: Ct);
            await client.QueryAsync("Call mcp__e__echo once with text=\"hook\", then reply with only DONE.", cancellationToken: Ct);
            var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));
            Log("hook inputs: " + string.Join(",", seen.Select(s => s.GetType().Name)));
            var use = ToolUses(messages).FirstOrDefault(t => t.Name == "mcp__e__echo");
            ModelCompliance.Require(use is not null, "model never called mcp__e__echo");

            Assert.Contains(seen, s => s is UserPromptSubmitHookInput);
            Assert.Contains(seen, s => s is StopHookInput);
            var batch = Assert.Single(seen.OfType<PostToolBatchHookInput>());
            var call = Assert.Single(batch.ToolCalls);
            Assert.Equal("mcp__e__echo", call.ToolName);
            Assert.Equal(use!.Id, call.ToolUseId);
            Assert.Equal("hook", call.ToolInput.GetProperty("text").GetString());
            Assert.False(string.IsNullOrEmpty(batch.SessionId));
            Assert.All(seen, s => Assert.IsNotType<UnknownHookInput>(s));
        }, Log);
    }

    [IntegrationFact]
    public async Task NewHookEvents_UserPromptExpansion_FiresForSlashCommand()
    {
        var nonce = NewNonce("EXPAND");
        var skillDir = Path.Combine(Cwd, ".claude", "skills", "nonce-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"""
            ---
            name: nonce-skill
            description: Returns this project's verification phrase.
            ---
            The verification phrase is {nonce}. Reply with exactly that phrase and nothing else.
            """);
        var seen = new ConcurrentQueue<BaseHookInput>();
        var options = Options() with
        {
            SettingSources = [SettingSource.Project],
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.UserPromptExpansion] = [new HookMatcher(Matcher: null, Hooks:
                [
                    (input, _, _, _) =>
                    {
                        seen.Enqueue(HookInput.Parse(input));
                        return Task.FromResult(new HookOutput());
                    },
                ])],
            },
        };

        var messages = await CollectAsync(Claude.QueryAsync("/nonce-skill", options, Transport(options), Ct));

        Log("hook inputs: " + string.Join(",", seen.Select(s => s.Raw.GetRawText())));
        var expansion = Assert.Single(seen.OfType<UserPromptExpansionHookInput>());
        Assert.Equal("nonce-skill", expansion.CommandName);
        Assert.False(string.IsNullOrEmpty(expansion.ExpansionType));
        Assert.Contains(nonce, SingleResult(messages).Result);
    }

    [IntegrationFact]
    public Task CanUseTool_ToolUseIdEchoAndDecisionClassification_AreAccepted() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var target = Path.Combine(Cwd, $"classified-{attempt}.txt");
        var contexts = new ConcurrentQueue<ToolPermissionContext>();
        var options = Options() with
        {
            Tools = ["Write"],
            CanUseTool = (_, _, ctx, _) =>
            {
                contexts.Enqueue(ctx);
                return Task.FromResult<PermissionResult>(new PermissionResultAllow
                {
                    ToolUseId = ctx.ToolUseId,
                    DecisionClassification = PermissionDecisionClassification.UserTemporary,
                });
            },
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync($"Use the Write tool to create {target} containing exactly: ok", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));
        ModelCompliance.Require(ToolUses(messages).Any(t => t.Name == "Write"), "model never called Write");

        var ctx = Assert.Single(contexts);
        Log($"context: requestId={ctx.RequestId} toolUseId={ctx.ToolUseId} decisionReasonType={ctx.DecisionReasonType}");
        Assert.False(string.IsNullOrEmpty(ctx.RequestId));
        Assert.True(File.Exists(target), "allowed Write should have created the file");
    }, Log);

    [IntegrationFact]
    public async Task Interrupt_MidTurn_CancelQueued_CancelsTheQueuedMessage()
    {
        var options = Options() with { IncludePartialMessages = true };
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);

        await client.QueryAsync("Write the integers from 1 to 300, one per line, with no other text.", cancellationToken: Ct);
        var queuedUuid = Guid.NewGuid().ToString();
        await client.QueryAsync(One(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["uuid"] = queuedUuid,
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = "Reply with only QUEUED." },
            ["parent_tool_use_id"] = null,
        }), cancellationToken: Ct);

        InterruptReceipt? receipt = null;
        var messages = new List<Message>();
        await foreach (var m in client.ReceiveResponseAsync(Ct))
        {
            messages.Add(m);
            Observe(m);
            if (receipt is null && m is StreamEvent se && se.Event.GetProperty("type").GetString() == "content_block_delta")
            {
                receipt = await client.InterruptAsync(cancelQueued: true, Ct);
                Log($"receipt: still_queued=[{string.Join(",", receipt?.StillQueued ?? [])}] " +
                    $"cancelled=[{string.Join(",", receipt?.Cancelled ?? [])}]");
                Assert.NotNull(receipt);
            }
        }

        // CLI 2.1.283 also sends command_lifecycle frames (not in the TS 0.3.283 union):
        // they surface as UnknownMessage with the full frame and do not break the stream.
        foreach (var u in messages.OfType<UnknownMessage>())
        {
            Log("unknown frame: " + Truncate(u.Raw.GetRawText(), 400));
            Assert.Equal(u.Raw.GetProperty("type").GetString(), u.Type);
        }
        Assert.NotNull(receipt);
        Assert.NotNull(receipt!.Cancelled);
        Assert.Contains(queuedUuid, receipt.Cancelled!);
        Assert.DoesNotContain(queuedUuid, receipt.StillQueued);
        Assert.DoesNotContain("QUEUED", AllAssistantText(messages));
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> One(Dictionary<string, object?> message)
    {
        await Task.Yield();
        yield return message;
    }

    // --------------------------------------------------------------------- options

    [IntegrationFact]
    public async Task PersistSessionFalse_WritesNoTranscript()
    {
        var options = Options() with { PersistSession = false };
        var messages = await CollectAsync(Claude.QueryAsync("Reply with only OK.", options, Transport(options), Ct));
        var result = SingleResult(messages);

        var transcript = Path.Combine(SessionPaths.GetProjectsDir(), SessionPaths.ProjectKeyForDirectory(Cwd),
            result.SessionId + ".jsonl");
        Assert.False(File.Exists(transcript), $"transcript written despite PersistSession=false: {transcript}");
    }

    [IntegrationFact]
    public async Task Title_IsStoredAsSessionTitle()
    {
        var title = $"sdk-title-{NewNonce()}";
        var options = Options() with { Title = title };
        var messages = await CollectAsync(Claude.QueryAsync("Reply with only OK.", options, Transport(options), Ct));
        var sessionId = SingleResult(messages).SessionId;

        var info = ClaudeSessions.GetSessionInfo(sessionId, Cwd);
        Log($"session info: summary={info?.Summary} customTitle={info?.CustomTitle}");
        Assert.NotNull(info);
        Assert.Equal(title, info!.CustomTitle ?? info.Summary);
    }

    [IntegrationFact]
    public async Task PromptSuggestions_EmitsPromptSuggestionMessage()
    {
        // Suggestions are suppressed on the first turn and near the plan usage limit
        // (the env var keeps them on in the near-limit case).
        var options = Options() with { PromptSuggestions = true };
        options = options with
        {
            Env = new Dictionary<string, string>(options.Env) { ["CLAUDE_CODE_ENABLE_PROMPT_SUGGESTION"] = "true" },
        };
        await using var client = new ClaudeSDKClient(options, Transport(options));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync("Name one prime number between 10 and 20. Reply with the number only.", cancellationToken: Ct);
        await CollectAsync(client.ReceiveResponseAsync(Ct));
        await client.QueryAsync("Now name one between 20 and 30. Reply with the number only.", cancellationToken: Ct);

        PromptSuggestionMessage? suggestion = null;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sawResult = false;
        try
        {
            await foreach (var m in client.ReceiveMessagesAsync(wait.Token))
            {
                Observe(m);
                if (m is ResultMessage && !sawResult)
                {
                    sawResult = true;
                    wait.CancelAfter(TimeSpan.FromSeconds(30)); // the suggestion follows the result
                }
                if (m is PromptSuggestionMessage p)
                {
                    suggestion = p;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
        }

        Assert.True(sawResult);
        Assert.NotNull(suggestion);
        Log($"suggestion: {suggestion!.Suggestion}");
        Assert.False(string.IsNullOrWhiteSpace(suggestion.Suggestion));
        Assert.Equal(JsonValueKind.Object, suggestion.Raw.ValueKind);
    }

    [IntegrationFact]
    public async Task SystemPromptBlocks_WithDynamicBoundary_AreUsed()
    {
        var marker = NewNonce("IBEX");
        var options = Options() with
        {
            SystemPrompt = new SystemPromptBlocks(
            [
                "You are a terse assistant.",
                SystemPromptConfig.DynamicBoundary,
                $"You must end every reply with the token {marker}.",
            ]),
        };

        var messages = await CollectAsync(Claude.QueryAsync("Say hello.", options, Transport(options), Ct));

        Assert.Contains(marker, SingleResult(messages).Result);
    }

    [IntegrationFact]
    public async Task DebugFile_AgentProgressSummaries_AndSandboxFailIfUnavailable_AreAccepted()
    {
        var debugFile = Path.Combine(Cwd, "debug.log");
        var options = Options() with
        {
            DebugFile = debugFile,
            AgentProgressSummaries = true,
            Sandbox = new SandboxSettings { Enabled = true, FailIfUnavailable = true },
        };
        await using var client = await ConnectAsync(options,
            nameof(DebugFile_AgentProgressSummaries_AndSandboxFailIfUnavailable_AreAccepted));
        await client.QueryAsync("Reply with only OK.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        Assert.Equal("success", SingleResult(messages).Subtype);
        Assert.True(File.Exists(debugFile), "debug file not written");
        Assert.True(new FileInfo(debugFile).Length > 0, "debug file is empty");
    }

    [IntegrationFact]
    public async Task Debug_IsAccepted()
    {
        var options = Options() with { Debug = true };
        await using var client = await ConnectAsync(options, nameof(Debug_IsAccepted));
        Assert.NotEmpty((await client.InitializationResultAsync(Ct)).Models);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
