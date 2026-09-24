// Tests for the .NET-only convenience surface: QueryTextAsync, QueryAsync<T>,
// ILogger integration, StreamJsonReader, [McpTool]/ToolsFrom and ScriptedTransport.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Testing;
using Claude.AgentSdk.Transport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed record Forecast(string City, int HighCelsius, IReadOnlyList<string> Conditions);

public sealed record SumArgs(int A, int B);

public sealed record GreetArgs(string Name);

[JsonSerializable(typeof(Forecast))]
[JsonSerializable(typeof(SumArgs))]
[JsonSerializable(typeof(GreetArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ConvenienceJson : JsonSerializerContext;

public sealed class MathTools
{
    public int Calls;

    [McpTool("add", Description = "Add two integers", ReadOnly = true)]
    public string Add(SumArgs args)
    {
        Interlocked.Increment(ref Calls);
        return (args.A + args.B).ToString();
    }

    [McpTool(Idempotent = true)]
    [Description("Say hello")]
    public Task<McpToolResult> GreetAsync(GreetArgs args, CancellationToken ct) =>
        Task.FromResult(McpToolResults.Text($"Hello, {args.Name}!"));

    [McpTool("ping")]
    public static string Ping() => "pong";

    [McpTool("fail")]
    public string Fail(SumArgs args) => throw new InvalidOperationException("boom: " + args.A);

    public string NotATool() => "ignored";
}

public sealed class ScalarTools
{
    [McpTool("multiply")]
    public int Multiply(int a, int b = 2) => a * b;
}

public sealed class NoTools
{
    public void Nothing() { }
}

public sealed class TwoParamTools
{
    [McpTool("bad")]
    public string Bad(int a, int b) => "";
}

public sealed class ConvenienceApiTests
{
    private static ScriptedTransport OneTurn(Action<ScriptedTurn> turn) => new ScriptedTransport().Turn(turn);

    // ---- QueryTextAsync ----------------------------------------------------

    [Fact]
    public async Task QueryTextAsync_ReturnsResultText()
    {
        var transport = OneTurn(t => t.AssistantText("thinking...").Result("4"));

        var answer = await Claude.QueryTextAsync("2+2?", transport: transport);

        Assert.Equal("4", answer);
        Assert.Equal("2+2?", transport.UserMessages.Single()
            .GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task QueryTextAsync_FallsBackToLastAssistantText()
    {
        var transport = OneTurn(t => t.AssistantText("first").AssistantText("final answer").Result());

        Assert.Equal("final answer", await Claude.QueryTextAsync("q", transport: transport));
    }

    [Fact]
    public async Task QueryTextAsync_ErrorResultThrowsResultException()
    {
        var transport = OneTurn(t => t.Result("Reached max turns", isError: true, subtype: "error_max_turns"));

        var ex = await Assert.ThrowsAsync<ResultException>(() => Claude.QueryTextAsync("q", transport: transport));
        Assert.Equal("error_max_turns", ex.Subtype);
    }

    // ---- QueryAsync<T> -----------------------------------------------------

    [Fact]
    public async Task QueryAsyncT_WithTypeInfo_DeserializesStructuredOutput()
    {
        var transport = OneTurn(t => t.Result(
            structuredOutputJson: """{"city":"Paris","highCelsius":21,"conditions":["sunny","breezy"]}"""));

        var forecast = await Claude.QueryAsync("weather?", ConvenienceJson.Default.Forecast, transport: transport);

        Assert.Equal(new Forecast("Paris", 21, forecast.Conditions), forecast);
        Assert.Equal(["sunny", "breezy"], forecast.Conditions);
    }

    [Fact]
    public async Task QueryAsyncT_ReflectionOverload_UsesWebNaming()
    {
        var transport = OneTurn(t => t.Result(
            structuredOutputJson: """{"city":"Oslo","highCelsius":3,"conditions":[]}"""));

        var forecast = await Claude.QueryAsync<Forecast>("weather?", transport: transport);

        Assert.Equal("Oslo", forecast.City);
        Assert.Equal(3, forecast.HighCelsius);
    }

    [Fact]
    public async Task QueryAsyncT_NoStructuredOutputThrows()
    {
        var transport = OneTurn(t => t.Result("plain text only"));

        var ex = await Assert.ThrowsAsync<ClaudeSDKException>(() =>
            Claude.QueryAsync("q", ConvenienceJson.Default.Forecast, transport: transport));
        Assert.Contains("without structured output", ex.Message);
    }

    [Fact]
    public void QueryAsyncT_SchemaSentToCliMatchesTypeInfo()
    {
        // The --json-schema flag carries the schema exported from the same metadata used to deserialize.
        var schema = McpSdkServerBuilder.BuildSchema(ConvenienceJson.Default.Forecast);
        var options = new ClaudeAgentOptions
        {
            CliPath = "dummy-claude",
            OutputFormat = JsonSerializer.SerializeToElement(new { type = "json_schema", schema })
        };
        var cmd = new SubprocessTransport("", options).BuildCommand();

        var sent = JsonDocument.Parse(cmd[cmd.IndexOf("--json-schema") + 1]).RootElement;
        var props = sent.GetProperty("properties");
        Assert.True(props.TryGetProperty("city", out _));
        Assert.True(props.TryGetProperty("highCelsius", out _));
        Assert.Contains("city", sent.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    // ---- ILogger -----------------------------------------------------------

    [Fact]
    public async Task Logger_ReceivesControlTraffic_ButNeverPromptOrToolInput()
    {
        var logger = new CapturingLogger();
        var transport = OneTurn(t => t
            .PermissionRequest("Write", """{"file_path":"/tmp/secret-path-123","content":"x"}""")
            .Result("done"));
        var options = new ClaudeAgentOptions
        {
            Logger = logger,
            CanUseTool = (_, _, _, _) => throw new InvalidOperationException("callback exploded")
        };

        await Claude.QueryTextAsync("prompt-with-secret-xyz", options, transport);

        var all = logger.Render();
        Assert.Contains("Sending control request initialize", all);
        Assert.Contains("Control request can_use_tool", all);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception?.Message == "callback exploded");
        Assert.DoesNotContain("prompt-with-secret-xyz", all);
        Assert.DoesNotContain("secret-path-123", all);
        // Callback failures still fail closed.
        Assert.Contains("\"subtype\":\"error\"", transport.Written.Select(w => w.GetRawText()).Last(w => w.Contains("control_response")));
    }

    [Fact]
    public void Logger_ReceivesShadowingWarningInsteadOfStderr()
    {
        var logger = new CapturingLogger();
        var options = new ClaudeAgentOptions
        {
            Logger = logger,
            AllowedTools = ["Write"],
            CanUseTool = (_, _, _, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow())
        };

        Internal.CanUseToolConfiguration.Configure(options);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Write"));
    }

    // ---- StreamJsonReader --------------------------------------------------

    [Fact]
    public async Task StreamJsonReader_ParsesLinesSkipsNoiseAndDropsTruncatedTail()
    {
        var input = "[debug] hi\n{\"type\":\"a\"}\r\n\n{\"type\":\"b\"}\n{\"type\": \"trunc";
        var types = new List<string>();
        await foreach (var msg in StreamJsonReader.ReadAsync(new StringReader(input)))
            types.Add(msg.GetProperty("type").GetString()!);

        Assert.Equal(["a", "b"], types);
    }

    [Fact]
    public async Task StreamJsonReader_BoundsUnterminatedLine()
    {
        var reader = new StringReader(new string('x', 5000));
        await Assert.ThrowsAsync<JsonDecodeException>(async () =>
        {
            await foreach (var _ in StreamJsonReader.ReadAsync(reader, maxLineLength: 1000)) { }
        });
    }

    [Fact]
    public async Task StreamJsonReader_CorruptLineThrows()
    {
        var reader = new StringReader("{\"type\": \n{\"type\":\"b\"}\n");
        await Assert.ThrowsAsync<JsonDecodeException>(async () =>
        {
            await foreach (var _ in StreamJsonReader.ReadAsync(reader)) { }
        });
    }

    // ---- [McpTool] / ToolsFrom --------------------------------------------

    private static McpSdkServerConfig BuildServer(Action<McpSdkServerBuilder> configure)
    {
        var registry = McpServers.Sdk("tools", configure);
        return (McpSdkServerConfig)registry["tools"];
    }

    [Fact]
    public async Task ToolsFrom_WithContext_RegistersAttributedMethodsWithSchemasAndAnnotations()
    {
        var tools = new MathTools();
        var server = BuildServer(b => b.ToolsFrom(tools, ConvenienceJson.Default));

        var list = await server.Handlers.ListTools!(CancellationToken.None);
        Assert.Equal(["add", "Greet", "ping", "fail"], list.Select(t => t.Name).ToArray());

        var add = list.Single(t => t.Name == "add");
        Assert.Equal("Add two integers", add.Description);
        Assert.True(add.Annotations!.ReadOnlyHint);
        Assert.Null(add.Annotations.DestructiveHint);
        var props = add.InputSchema!.Value.GetProperty("properties");
        Assert.True(props.TryGetProperty("a", out _));
        Assert.True(props.TryGetProperty("b", out _));

        var greet = list.Single(t => t.Name == "Greet");
        Assert.Equal("Say hello", greet.Description);
        Assert.True(greet.Annotations!.IdempotentHint);

        var ping = list.Single(t => t.Name == "ping");
        Assert.Equal("object", ping.InputSchema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ToolsFrom_WithContext_InvokesAndReportsErrors()
    {
        var tools = new MathTools();
        var server = BuildServer(b => b.ToolsFrom(tools, ConvenienceJson.Default));
        var call = server.Handlers.CallTool!;

        var sum = await call("add", JsonSerializer.SerializeToElement(new { a = 2, b = 40 }), CancellationToken.None);
        Assert.Equal("42", sum.Content![0].Text);
        Assert.Equal(1, tools.Calls);

        var hello = await call("Greet", JsonSerializer.SerializeToElement(new { name = "Ada" }), CancellationToken.None);
        Assert.Equal("Hello, Ada!", hello.Content![0].Text);

        Assert.Equal("pong", (await call("ping", JsonSerializer.SerializeToElement(new { }), CancellationToken.None)).Content![0].Text);

        var failed = await call("fail", JsonSerializer.SerializeToElement(new { a = 7, b = 0 }), CancellationToken.None);
        Assert.True(failed.IsError);
        Assert.Equal("boom: 7", failed.Content![0].Text);

        var invalid = await call("add", JsonSerializer.SerializeToElement(new { a = "x" }), CancellationToken.None);
        Assert.True(invalid.IsError);
        Assert.StartsWith("Input validation error", invalid.Content![0].Text);
    }

    [Fact]
    public async Task ToolsFrom_Reflection_SupportsScalarParameters()
    {
        var server = BuildServer(b => b.ToolsFrom(new ScalarTools()));

        var def = (await server.Handlers.ListTools!(CancellationToken.None)).Single();
        Assert.Equal("multiply", def.Name);
        var required = def.InputSchema!.Value.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["a"], required);

        var result = await server.Handlers.CallTool!("multiply", JsonSerializer.SerializeToElement(new { a = 21 }), CancellationToken.None);
        Assert.Equal("42", result.Content![0].Text);
    }

    [Fact]
    public void ToolsFrom_StaticOnlyOverload_RegistersStaticMethods()
    {
        var server = BuildServer(b => b.ToolsFrom<MathTools>(ConvenienceJson.Default));
        var names = server.Handlers.ListTools!(CancellationToken.None).Result.Select(t => t.Name);
        Assert.Equal(["ping"], names);
    }

    [Fact]
    public void ToolsFrom_RejectsMisconfiguredTypes()
    {
        Assert.Throws<ArgumentException>(() => BuildServer(b => b.ToolsFrom(new NoTools(), ConvenienceJson.Default)));
        var multi = Assert.Throws<ArgumentException>(() => BuildServer(b => b.ToolsFrom(new TwoParamTools(), ConvenienceJson.Default)));
        Assert.Contains("wrap the parameters in a record", multi.Message);
        var unregistered = Assert.Throws<ArgumentException>(() =>
            BuildServer(b => b.ToolsFrom(new MathTools(), EmptyJson.Default)));
        Assert.Contains("not registered", unregistered.Message);
    }

    // ---- ScriptedTransport end-to-end -------------------------------------

    [Fact]
    public async Task ScriptedTransport_PermissionRequestRunsCallbackAndRecordsDecision()
    {
        string? askedTool = null;
        var transport = OneTurn(t => t
            .PermissionRequest("Write", """{"file_path":"/etc/passwd","content":"x"}""")
            .AssistantText("I was not allowed to write that.")
            .Result("I was not allowed to write that."));
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (tool, input, _, _) =>
            {
                askedTool = tool;
                return Task.FromResult<PermissionResult>(
                    input.GetProperty("file_path").GetString()!.StartsWith("/etc")
                        ? new PermissionResultDeny("system files are off limits")
                        : new PermissionResultAllow());
            }
        };

        var answer = await Claude.QueryTextAsync("write it", options, transport);

        Assert.Equal("Write", askedTool);
        var decision = Assert.Single(transport.PermissionResponses);
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
        Assert.Equal("system files are off limits", decision.GetProperty("message").GetString());
        Assert.Equal("I was not allowed to write that.", answer);
        Assert.True(transport.InputEnded);
    }

    [Fact]
    public async Task ScriptedTransport_McpToolCallReachesAttributedTool()
    {
        var tools = new MathTools();
        var transport = OneTurn(t => t
            .McpToolCall("math", "add", """{"a":1729,"b":2718}""")
            .Result("4447"));
        var options = new ClaudeAgentOptions
        {
            McpServers = McpServers.Sdk("math", b => b.ToolsFrom(tools, ConvenienceJson.Default))
        };

        Assert.Equal("4447", await Claude.QueryTextAsync("add them", options, transport));

        Assert.Equal(1, tools.Calls);
        var mcpResponse = transport.Written
            .Where(w => w.GetProperty("type").GetString() == "control_response")
            .Select(w => w.GetProperty("response").GetProperty("response"))
            .Single(r => r.TryGetProperty("mcp_response", out _))
            .GetProperty("mcp_response");
        Assert.Equal("4447", mcpResponse.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ScriptedTransport_HookCallbackRunsHook()
    {
        var seen = new List<string>();
        var transport = OneTurn(t => t
            .HookCallback("hook_0", """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"rm -rf /"}}""")
            .Result("blocked"));
        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher("Bash", [(input, _, _, _) =>
                {
                    seen.Add(input.GetProperty("tool_input").GetProperty("command").GetString()!);
                    return Task.FromResult(new HookOutput { Decision = "block", Reason = "dangerous" });
                }])]
            }
        };

        await Claude.QueryTextAsync("clean up", options, transport);

        Assert.Equal(["rm -rf /"], seen);
        var response = Assert.Single(transport.HookResponses);
        Assert.Equal("block", response.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ScriptedTransport_DrivesMultiTurnClient()
    {
        var transport = new ScriptedTransport()
            .Turn(t => t.AssistantText("Nice to meet you, Ada.").Result("Nice to meet you, Ada."))
            .Turn(t => t.AssistantText("Your name is Ada.").Result("Your name is Ada."));

        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions(), transport);
        await client.ConnectAsync();

        var replies = new List<string>();
        foreach (var prompt in new[] { "I'm Ada", "What's my name?" })
        {
            await client.QueryAsync(prompt);
            await foreach (var msg in client.ReceiveResponseAsync())
            {
                if (msg is ResultMessage r)
                    replies.Add(r.Result!);
            }
        }

        Assert.Equal(["Nice to meet you, Ada.", "Your name is Ada."], replies);
        Assert.Equal(2, transport.UserMessages.Count);
    }

    // ---- helpers ------------------------------------------------------------

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));

        public string Render()
        {
            var sb = new StringBuilder();
            foreach (var (level, message, ex) in Entries)
                sb.Append(level).Append(": ").Append(message).Append(' ').Append(ex?.Message).AppendLine();
            return sb.ToString();
        }
    }
}

[JsonSerializable(typeof(int))]
internal partial class EmptyJson : JsonSerializerContext;
