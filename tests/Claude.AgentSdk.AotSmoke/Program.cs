// NativeAOT smoke test: exercises the SDK's AOT-safe surface end to end without a
// Claude CLI. Exits 0 when every check passes.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Claude.AgentSdk;
using Claude.AgentSdk.AotSmoke;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;
using Sdk = Claude.AgentSdk.Claude;

var failures = 0;

void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
    if (!condition) failures++;
}

JsonNode Json(string json) => JsonNode.Parse(json)!;

Console.WriteLine($"Claude.AgentSdk AOT smoke (dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported})");

try
{
    // ---- 1. Options through the fluent builder ---------------------------------------------
    var addSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}
        """).RootElement;

    var options = Sdk.Options()
        .Model("claude-sonnet-4-5")
        .MaxTurns(3)
        .CliPath("claude-not-spawned-by-the-smoke-test")
        .SystemPrompt(SystemPromptPreset.ClaudeCode("Be brief."))
        .Sandbox(s => s.Enable().AutoAllowBash().Network(n => n.AllowLocalBinding()))
        .Agents(a => a.Add("reviewer", "Reviews code", "You review code.", "Read"))
        .Hooks(h => h.PreToolUse("Write", (_, _, _, _) => Task.FromResult(new HookOutput
        {
            HookSpecificOutput = new PreToolUseHookSpecificOutput
            {
                PermissionDecision = "deny",
                PermissionDecisionReason = "smoke test"
            }.ToJsonElement()
        })))
        .CanUseTool((tool, _, _, _) => Task.FromResult<PermissionResult>(
            tool == "Bash" ? new PermissionResultDeny("no bash") : new PermissionResultAllow()))
        .McpServers(r => r.AddSdk("calc", b => b
            .Tool("add", addSchema, (args, _) => Task.FromResult(McpToolResults.Text(
                (args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                "Add two numbers")
            .Tool("mul", SmokeJsonContext.Default.MulArgs, (m, _) => Task.FromResult(McpToolResults.Text((m.X * m.Y).ToString())),
                "Multiply two integers", new McpToolAnnotations { ReadOnlyHint = true, MaxResultSizeChars = 1000 })))
        .Build();
    Check(options.McpServers is McpServersConfig.ServerMap, "builder produced an SDK MCP server map");

    // ---- 2. CLI command line (the --settings / --mcp-config JSON) ------------------------
    var cmd = new SubprocessTransport("unused", options).BuildCommand();
    string Arg(string flag) => cmd[cmd.IndexOf(flag) + 1];
    Check(Arg("--settings") == """{"sandbox":{"enabled":true,"autoAllowBashIfSandboxed":true,"network":{"allowLocalBinding":true}}}""",
        $"--settings carries only the set sandbox members: {Arg("--settings")}");
    Check(Arg("--mcp-config") == """{"mcpServers":{"calc":{"type":"sdk","name":"calc"}}}""",
        $"--mcp-config strips the in-process handlers: {Arg("--mcp-config")}");
    Check(cmd.Contains("--model") && Arg("--model") == "claude-sonnet-4-5", "--model is forwarded");

    // ---- 3. Control protocol round trip over an in-memory transport -----------------------
    var transport = new ScriptedTransport
    {
        ControlResponder = (subtype, _) => subtype switch
        {
            "initialize" => Json("""{"commands":[],"output_style":"default"}"""),
            "mcp_status" => Json("""
                {"mcpServers":[{"name":"calc","status":"connected","serverInfo":{"name":"calc","version":"1.0.0"},
                 "config":{"type":"sdk","name":"calc"},"scope":"dynamic","tools":[{"name":"add"},{"name":"mul"}]},
                 {"name":"remote","status":"needs-auth"}]}
                """),
            "get_context_usage" => Json("""
                {"categories":[{"name":"System prompt","tokens":3000,"color":"promptBorder"}],"totalTokens":3000,
                 "maxTokens":200000,"rawMaxTokens":200000,"percentage":1.5,"model":"claude-sonnet-4-5",
                 "isAutoCompactEnabled":true,"memoryFiles":[],"mcpTools":[],"agents":[],"gridRows":[]}
                """),
            _ => new JsonObject()
        },
        OnUserMessage = _ =>
        [
            Json("""
                {"type":"assistant","message":{"model":"claude-sonnet-4-5","content":[
                  {"type":"thinking","thinking":"hmm","signature":"sig"},
                  {"type":"text","text":"Hello from AOT"},
                  {"type":"tool_use","id":"toolu_1","name":"mcp__calc__add","input":{"a":1,"b":2}}]},
                 "parent_tool_use_id":null,"session_id":"s1","uuid":"u1"}
                """),
            Json("""
                {"type":"user","message":{"role":"user","content":[
                  {"tool_use_id":"toolu_1","type":"tool_result","content":[{"type":"text","text":"3"}]}]},
                 "parent_tool_use_id":null,"session_id":"s1","uuid":"u2"}
                """),
            Json("""
                {"type":"result","subtype":"success","duration_ms":12,"duration_api_ms":10,"is_error":false,
                 "num_turns":1,"session_id":"s1","total_cost_usd":0.001,"result":"Hello from AOT",
                 "usage":{"input_tokens":5,"output_tokens":7}}
                """)
        ]
    };

    await using (var client = new ClaudeSDKClient(options, transport))
    {
        await client.ConnectAsync();

        var init = transport.Written.First(w => w.GetProperty("type").GetString() == "control_request");
        var initRequest = init.GetProperty("request");
        Check(initRequest.GetProperty("subtype").GetString() == "initialize", "initialize is the first control request");
        Check(initRequest.GetProperty("agents").GetProperty("reviewer").GetProperty("tools")[0].GetString() == "Read",
            "initialize carries agent definitions");
        Check(initRequest.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hookCallbackIds")[0].GetString() == "hook_0",
            "initialize registers hook callbacks");

        // CLI -> SDK: in-process MCP server.
        JsonElement Mcp(JsonElement r) => r.GetProperty("response").GetProperty("mcp_response");
        Task<JsonElement> McpAsync(string server, string message) =>
            transport.RequestAsync(new JsonObject { ["subtype"] = "mcp_message", ["server_name"] = server, ["message"] = Json(message) });

        var mcpInit = Mcp(await McpAsync("calc", """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{}}}"""));
        Check(mcpInit.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "calc", "mcp initialize");

        var ack = Mcp(await McpAsync("calc", """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Check(ack.GetRawText() == """{"jsonrpc":"2.0","result":{}}""", "mcp notification is acked with an empty result");

        var tools = Mcp(await McpAsync("calc", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).GetProperty("result").GetProperty("tools");
        Check(tools.GetArrayLength() == 2, "tools/list returns both tools");
        var mul = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "mul");
        Check(mul.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("x", out _),
            $"typed tool schema comes from JsonTypeInfo: {mul.GetProperty("inputSchema").GetRawText()}");
        Check(mul.GetProperty("_meta").GetProperty("anthropic/maxResultSizeChars").GetInt32() == 1000, "tool _meta carries maxResultSizeChars");
        Check(mul.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "tool annotations are serialized");

        string ToolText(JsonElement r) => r.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var added = Mcp(await McpAsync("calc", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"add","arguments":{"a":2,"b":3}}}"""));
        Check(ToolText(added) == "5", "explicit-schema tool call");
        var multiplied = Mcp(await McpAsync("calc", """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"mul","arguments":{"x":4,"y":5}}}"""));
        Check(ToolText(multiplied) == "20", "JsonTypeInfo tool call");
        var invalid = Mcp(await McpAsync("calc", """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"add","arguments":{"a":"x","b":1}}}"""));
        Check(invalid.GetProperty("result").GetProperty("isError").GetBoolean(), $"schema validation rejects bad input: {ToolText(invalid)}");

        var unknown = Mcp(await McpAsync("nope", """{"jsonrpc":"2.0","id":5,"method":"tools/list"}"""));
        Check(unknown.GetProperty("error").GetProperty("message").GetString() == "Server 'nope' not found", "unknown MCP server error text");

        // CLI -> SDK: permissions and hooks.
        var denied = await transport.RequestAsync(Json("""{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"}}"""));
        Check(denied.GetProperty("response").GetProperty("behavior").GetString() == "deny", "can_use_tool deny");
        var allowed = await transport.RequestAsync(Json("""{"subtype":"can_use_tool","tool_name":"Read","input":{"file_path":"/tmp/x"}}"""));
        Check(allowed.GetProperty("response").GetProperty("updatedInput").GetProperty("file_path").GetString() == "/tmp/x",
            "can_use_tool allow echoes the input");
        var hook = await transport.RequestAsync(Json("""{"subtype":"hook_callback","callback_id":"hook_0","input":{"tool_name":"Write"}}"""));
        var hso = hook.GetProperty("response").GetProperty("hookSpecificOutput");
        Check(hso.GetRawText() == """{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"smoke test"}""",
            $"hook output has no null members: {hso.GetRawText()}");

        // SDK -> CLI: control requests with typed responses.
        await client.SetModelAsync("claude-haiku-4-5");
        await client.SetPermissionModeAsync(PermissionMode.AcceptEdits);
        await client.InterruptAsync();
        var status = await client.ListMcpServersAsync();
        Check(status.McpServers[0].Status == McpServerConnectionStatus.Connected &&
              status.McpServers[1].Status == McpServerConnectionStatus.NeedsAuth, "ListMcpServersAsync maps status strings");
        var usage = await client.GetContextUsageAsync();
        Check(usage.TotalTokens == 3000 && usage.Categories[0].Name == "System prompt", "GetContextUsageAsync");

        // Messages.
        await client.QueryAsync("hello");
        var received = new List<Message>();
        await foreach (var message in client.ReceiveResponseAsync())
            received.Add(message);
        var assistant = received.OfType<AssistantMessage>().Single();
        Check(assistant.Content.OfType<TextBlock>().Single().Text == "Hello from AOT", "assistant text block");
        Check(assistant.Content.OfType<ToolUseBlock>().Single().Input.GetProperty("b").GetInt32() == 2, "assistant tool_use block");
        Check(assistant.Content.OfType<ThinkingBlock>().Any(), "assistant thinking block");
        var toolResult = received.OfType<UserMessage>().Single().GetContentBlocks()!.OfType<ToolResultBlock>().Single();
        Check(toolResult.ToolUseId == "toolu_1", "user tool_result block (type not first)");
        var result = received.OfType<ResultMessage>().Single();
        Check(result.Result == "Hello from AOT" && !result.IsError, "result message");

        // A prompt stream of dictionaries with DOM / primitive values.
        await client.QueryAsync(PromptStream());
        var sent = transport.Written.Last(w => w.GetProperty("type").GetString() == "user");
        Check(sent.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString() == "streamed" &&
              sent.GetProperty("session_id").GetString() == "default", $"dictionary prompt serialized: {sent.GetRawText()}");
    }

    // ---- 4. Session store plumbing ---------------------------------------------------------
    var store = new InMemorySessionStore();
    Check(SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSessionsAsync)),
        "SessionStoreValidation detects implemented optional methods");
    Check(!SessionStoreValidation.StoreImplements(new MinimalStore(), nameof(ISessionStore.ListSessionsAsync)),
        "SessionStoreValidation detects default interface methods");
    Check(SessionStoreValidation.StoreImplements(new ExplicitStore(), nameof(ISessionStore.ListSessionsAsync)),
        "SessionStoreValidation detects explicit implementations");
    var mirrorTransport = new ScriptedTransport
    {
        CompleteAfterUserMessage = true,
        ControlResponder = (_, _) => new JsonObject(),
    };
    var configDir = Directory.CreateTempSubdirectory("aot-smoke").FullName;
    try
    {
        var sessionId = "0b9f7c4e-1f0a-4f5e-9d3c-2a1b3c4d5e6f";
        mirrorTransport.OnUserMessage = _ =>
        [
            new JsonObject
            {
                ["type"] = "transcript_mirror",
                ["filePath"] = Path.Combine(configDir, "projects", "proj", sessionId + ".jsonl"),
                ["entries"] = Json($$$"""[{"type":"user","uuid":"e1","sessionId":"{{{sessionId}}}","message":{"role":"user","content":"hi"}}]""")
            },
            Json("""{"type":"result","subtype":"success","duration_ms":1,"duration_api_ms":1,"is_error":false,"num_turns":1,"session_id":"s"}""")
        ];
        var mirrorOptions = new ClaudeAgentOptions
        {
            SessionStore = store,
            Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configDir }
        };
        await foreach (var _ in Sdk.QueryAsync("mirror", mirrorOptions, mirrorTransport)) { }
        var loaded = await store.LoadAsync(new SessionKey { ProjectKey = "proj", SessionId = sessionId });
        Check(loaded?.SingleOrDefault()?.Uuid == "e1", "transcript_mirror frames reach the SessionStore");
    }
    finally
    {
        Directory.Delete(configDir, recursive: true);
    }

    // ---- 5. Parse every message in the recorded real-CLI fixtures ------------------------
    var fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
    var parsed = 0;
    foreach (var file in Directory.Exists(fixtureDir) ? Directory.GetFiles(fixtureDir, "*.jsonl") : [])
    {
        var frames = File.ReadLines(file)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(l => l.TryGetProperty("dir", out var d) && d.GetString() == "in")
            .Select(l => l.GetProperty("msg"))
            .Where(m => m.GetProperty("type").GetString() is not ("control_request" or "control_response"
                or "control_cancel_request" or "transcript_mirror"))
            .ToList();
        var replay = new ScriptedTransport
        {
            CompleteAfterUserMessage = true,
            OnUserMessage = _ => frames.Select(f => JsonNode.Parse(f.GetRawText())!)
        };
        var count = 0;
        await foreach (var message in Sdk.QueryAsync("replay", new ClaudeAgentOptions(), replay))
        {
            if (message is UserMessage { Content.ValueKind: JsonValueKind.Array } user)
                _ = user.GetContentBlocks();
            count++;
        }
        parsed += count;
    }
    Check(parsed > 50, $"parsed {parsed} recorded CLI messages from {fixtureDir}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL  unexpected exception: {ex}");
    failures++;
}

Console.WriteLine(failures == 0 ? "AOT smoke test passed." : $"AOT smoke test FAILED ({failures} check(s)).");
return failures == 0 ? 0 : 1;

static async IAsyncEnumerable<Dictionary<string, object?>> PromptStream()
{
    await Task.Yield();
    yield return new Dictionary<string, object?>
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "streamed" })
        },
        ["parent_tool_use_id"] = null,
        ["priority"] = 1,
    };
}

namespace Claude.AgentSdk.AotSmoke
{
    public sealed record MulArgs(int X, int Y);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(MulArgs))]
    internal partial class SmokeJsonContext : JsonSerializerContext;

    /// <summary>A store with only the required methods (optional ones use the interface defaults).</summary>
    internal sealed class MinimalStore : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);
    }

    internal sealed class ExplicitStore : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);

        Task<IReadOnlyList<SessionStoreListEntry>> ISessionStore.ListSessionsAsync(string projectKey, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SessionStoreListEntry>>([]);
    }
}
