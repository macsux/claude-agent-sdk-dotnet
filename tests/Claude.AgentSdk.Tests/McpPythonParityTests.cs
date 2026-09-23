using System.Text.Json;
using Claude.AgentSdk.Mcp;
using Xunit;

namespace Claude.AgentSdk.Tests;

/// <summary>
/// Behaviors of the in-process MCP server path that must match the Python SDK
/// (claude_agent_sdk/__init__.py create_sdk_mcp_server, _internal/sdk_mcp_bridge.py).
/// </summary>
public sealed class McpPythonParityTests
{
    private static JsonElement Request(object id, string method, object? @params = null) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params ?? new { }
        });

    private static JsonElement Notification(string method, object? @params = null) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params ?? new { }
        });

    private static SdkMcpBridge BridgeFor(Action<McpSdkServerBuilder> configure)
    {
        var config = (McpSdkServerConfig)McpServers.Sdk("srv", configure)["srv"];
        return new SdkMcpBridge(config.Handlers, "srv");
    }

    private static Task<JsonElement> CallTool(SdkMcpBridge bridge, object id, string name, object args) =>
        bridge.SendMessageAsync(Request(id, "tools/call", new { name, arguments = args }));

    private static string FirstText(JsonElement response) =>
        response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement response) =>
        response.GetProperty("result").GetProperty("isError").GetBoolean();

    #region 1. Wire format (camelCase, exclude-none, image content)

    [Fact]
    public async Task PromptsAndResources_SerializeWithMcpWireNames()
    {
        var handlers = new McpServerHandlers
        {
            ListPrompts = _ => Task.FromResult<IReadOnlyList<McpPromptDefinition>>([
                new McpPromptDefinition
                {
                    Name = "greet",
                    Arguments = [new McpPromptArgument { Name = "who", Required = true }]
                }
            ]),
            GetPrompt = (name, args, _) => Task.FromResult(new McpPromptResult
            {
                Messages = [new McpPromptMessage { Role = "user", Content = McpContents.Text($"hi {args!["who"]}") }]
            }),
            ListResources = _ => Task.FromResult<IReadOnlyList<McpResourceDefinition>>([
                new McpResourceDefinition { Uri = "file:///a.txt", Name = "A", MimeType = "text/plain" }
            ]),
            ReadResource = (uri, _) => Task.FromResult(new McpResourceResult
            {
                Contents = [new McpResourceContent { Uri = uri, Blob = "AAE=" }]
            })
        };
        await using var bridge = new SdkMcpBridge(handlers, "srv");

        var prompts = (await bridge.SendMessageAsync(Request(1, "prompts/list"))).GetProperty("result").GetProperty("prompts");
        Assert.Equal("""[{"name":"greet","arguments":[{"name":"who","required":true}]}]""", prompts.GetRawText());

        var prompt = (await bridge.SendMessageAsync(Request(2, "prompts/get", new { name = "greet", arguments = new { who = "Bob" } })))
            .GetProperty("result");
        Assert.Equal("""{"messages":[{"role":"user","content":{"type":"text","text":"hi Bob"}}]}""", prompt.GetRawText());

        var resources = (await bridge.SendMessageAsync(Request(3, "resources/list"))).GetProperty("result").GetProperty("resources");
        Assert.Equal("""[{"uri":"file:///a.txt","name":"A","mimeType":"text/plain"}]""", resources.GetRawText());

        var read = (await bridge.SendMessageAsync(Request(4, "resources/read", new { uri = "file:///a.txt" }))).GetProperty("result");
        Assert.Equal("""{"contents":[{"uri":"file:///a.txt","blob":"AAE="}]}""", read.GetRawText());
    }

    [Fact]
    public async Task ImageContent_CarriesDataAndMimeType()
    {
        await using var bridge = BridgeFor(s => s.Tool("shot", () => McpToolResults.Image("iVBORw0K", "image/png")));

        var response = await CallTool(bridge, 1, "shot", new { });

        var content = response.GetProperty("result").GetProperty("content")[0];
        Assert.Equal("""{"type":"image","data":"iVBORw0K","mimeType":"image/png"}""", content.GetRawText());
    }

    [Fact]
    public async Task ToolsList_OmitsNullFields()
    {
        var handlers = new McpServerHandlers
        {
            ListTools = _ => Task.FromResult<IReadOnlyList<McpToolDefinition>>([new McpToolDefinition { Name = "bare" }])
        };
        await using var bridge = new SdkMcpBridge(handlers, "srv");

        var tools = (await bridge.SendMessageAsync(Request(1, "tools/list"))).GetProperty("result").GetProperty("tools");

        Assert.Equal("""[{"name":"bare"}]""", tools.GetRawText());
    }

    #endregion

    #region 2. Input validation and _meta

    [Fact]
    public async Task InvalidArguments_AreRejectedBeforeTheHandlerRuns()
    {
        var calls = 0;
        await using var bridge = BridgeFor(s => s.Tool("add", (double a, double b) => { calls++; return a + b; }));

        var missing = await CallTool(bridge, 1, "add", new { a = 1 });
        var wrongType = await CallTool(bridge, 2, "add", new { a = 1, b = "two" });
        var fine = await CallTool(bridge, 3, "add", new { a = 1, b = 2 });

        Assert.True(IsError(missing));
        Assert.Equal("Input validation error: 'b' is a required property", FirstText(missing));
        Assert.True(IsError(wrongType));
        Assert.Equal("Input validation error: 'two' is not of type 'number'", FirstText(wrongType));
        Assert.False(IsError(fine));
        Assert.Equal("3", FirstText(fine));
        Assert.Equal(1, calls);
    }

    private enum Color { Red, Green }

    private sealed record Order(string Item, int Quantity, IReadOnlyList<string> Tags);

    [Fact]
    public async Task Validation_CoversEnumsIntegersNestedObjectsAndArrays()
    {
        await using var bridge = BridgeFor(s => s
            .Tool("paint", (Color color) => color.ToString())
            .Tool("order", (Order order) => $"{order.Quantity}x{order.Item}"));

        Assert.Equal("Input validation error: 'Blue' is not one of ['Red', 'Green']",
            FirstText(await CallTool(bridge, 1, "paint", new { color = "Blue" })));
        Assert.Equal("Green", FirstText(await CallTool(bridge, 2, "paint", new { color = "Green" })));

        Assert.Equal("Input validation error: 1.5 is not of type 'integer'",
            FirstText(await CallTool(bridge, 3, "order", new { item = "x", quantity = 1.5, tags = Array.Empty<string>() })));
        Assert.Equal("Input validation error: 7 is not of type 'string'",
            FirstText(await CallTool(bridge, 4, "order", new { item = "x", quantity = 1, tags = new object[] { "a", 7 } })));
        Assert.Equal("Input validation error: 'tags' is a required property",
            FirstText(await CallTool(bridge, 5, "order", new { item = "x", quantity = 1 })));
        // jsonschema (Draft 2020-12) treats an integral float as an integer.
        Assert.Equal("2xx", FirstText(await CallTool(bridge, 6, "order", new { item = "x", quantity = 2.0, tags = new[] { "a" } })));
    }

    [Fact]
    public void Validator_ReportsShallowestErrorFirst()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"a":{"type":"string"}},"required":["a","b"]}
            """).RootElement;

        Assert.Equal("'b' is a required property",
            McpInputSchemaValidator.Validate(JsonDocument.Parse("""{"a":1}""").RootElement, schema));
        Assert.Equal("[] is not of type 'object'",
            McpInputSchemaValidator.Validate(JsonDocument.Parse("[]").RootElement, schema));
        Assert.Null(McpInputSchemaValidator.Validate(JsonDocument.Parse("""{"a":"x","b":null}""").RootElement, schema));
    }

    [Fact]
    public async Task MaxResultSizeChars_TravelsInMetaNotInAnnotations()
    {
        await using var bridge = BridgeFor(s => s
            .Tool("big", () => "x", annotations: new McpToolAnnotations { ReadOnlyHint = true, MaxResultSizeChars = 500_000 })
            .Tool("small", () => "x"));

        var tools = (await bridge.SendMessageAsync(Request(1, "tools/list"))).GetProperty("result").GetProperty("tools")
            .EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        Assert.Equal("""{"anthropic/maxResultSizeChars":500000}""", tools["big"].GetProperty("_meta").GetRawText());
        Assert.Equal("""{"readOnlyHint":true}""", tools["big"].GetProperty("annotations").GetRawText());
        Assert.False(tools["small"].TryGetProperty("_meta", out _));
    }

    [Fact]
    public async Task UnknownTool_IsAnErrorResult()
    {
        await using var bridge = BridgeFor(s => s.Tool("known", () => "x"));

        var response = await CallTool(bridge, 1, "nope", new { });

        Assert.True(IsError(response));
        Assert.Equal("Tool 'nope' not found", FirstText(response));
    }

    #endregion

    #region 3. Concurrency, ping, cancellation, unknown methods, dispose

    [Fact]
    public async Task ConcurrentToolCalls_OnOneServer_BothResolve()
    {
        var arrived = 0;
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = BridgeFor(s => s.Tool("rendezvous", async (string tag, CancellationToken ct) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                bothArrived.TrySetResult();
            await bothArrived.Task.WaitAsync(ct);
            return tag;
        }));

        var a = CallTool(bridge, 1, "rendezvous", new { tag = "a" });
        var b = CallTool(bridge, 2, "rendezvous", new { tag = "b" });
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("a", FirstText(a.Result));
        Assert.Equal("b", FirstText(b.Result));
    }

    [Fact]
    public async Task Ping_IsAnswered()
    {
        await using var bridge = BridgeFor(_ => { });

        var reply = await bridge.SendMessageAsync(Request(9, "ping"));

        Assert.Equal(9, reply.GetProperty("id").GetInt32());
        Assert.Equal("{}", reply.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task UnimplementedMethod_IsMethodNotFound()
    {
        await using var bridge = BridgeFor(s => s.Tool("t", () => "x"));

        var response = await bridge.SendMessageAsync(Request(1, "resources/list"));

        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Notification_IsAckedWithoutId()
    {
        await using var bridge = BridgeFor(_ => { });

        var ack = await bridge.SendMessageAsync(Notification("notifications/initialized"));

        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", ack.GetRawText());
    }

    [Fact]
    public async Task CancelledNotification_CancelsTheInFlightCall()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The bridge answers as soon as the token fires, which can be before the handler's catch runs.
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = BridgeFor(s => s.Tool("slow", async (CancellationToken ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                outcome.TrySetResult("finished");
            }
            catch (OperationCanceledException)
            {
                outcome.TrySetResult("cancelled");
                throw;
            }
            return "done";
        }));

        var call = CallTool(bridge, 77, "slow", new { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bridge.SendMessageAsync(Notification("notifications/cancelled", new { requestId = 77, reason = "user interrupted" }));
        var response = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("cancelled", await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(77, response.GetProperty("id").GetInt32());
        Assert.Contains("cancelled", response.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        // The server carries on.
        Assert.Equal("{}", (await bridge.SendMessageAsync(Request(78, "ping"))).GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task CancelledNotification_AnswersEvenIfTheHandlerIgnoresCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new McpServerHandlers
        {
            CallTool = (_, _, _) => { started.SetResult(); return release.Task; }
        };
        await using var bridge = new SdkMcpBridge(handlers, "srv");

        var call = CallTool(bridge, "req-1", "stuck", new { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bridge.SendMessageAsync(Notification("notifications/cancelled", new { requestId = "req-1" }));
        var response = await call.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult(McpToolResults.Text("late"));

        Assert.Equal("req-1", response.GetProperty("id").GetString());
        Assert.Equal(-32800, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ReusingAnInFlightRequestId_IsRefused()
    {
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = BridgeFor(s => s.Tool("wait", async () =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return "done";
        }));

        var first = CallTool(bridge, 7, "wait", new { });
        var second = await CallTool(bridge, 7, "wait", new { }).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.Contains("already in flight", second.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("done", FirstText(await first.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightCallsAndRefusesNewOnes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = BridgeFor(s => s.Tool("slow", async (CancellationToken ct) =>
        {
            started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "done";
        }));

        var call = CallTool(bridge, 1, "slow", new { });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bridge.DisposeAsync();
        var response = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.TryGetProperty("error", out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bridge.SendMessageAsync(Request(2, "ping")));
    }

    #endregion

    #region 4. Handler exceptions

    [Fact]
    public async Task SyncHandlerException_ReportsTheRealMessage()
    {
        await using var bridge = BridgeFor(s => s.Tool("fail", string () => throw new InvalidOperationException("Expected error")));

        var response = await CallTool(bridge, 1, "fail", new { });

        Assert.True(IsError(response));
        Assert.Equal("Expected error", FirstText(response));
    }

    [Fact]
    public async Task AsyncHandlerException_ReportsTheRealMessage()
    {
        await using var bridge = BridgeFor(s => s.Tool("fail", async Task<string> () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Async boom");
        }));

        var response = await CallTool(bridge, 1, "fail", new { });

        Assert.True(IsError(response));
        Assert.Equal("Async boom", FirstText(response));
    }

    #endregion
}
