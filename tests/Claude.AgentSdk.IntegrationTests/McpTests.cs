using System.Collections.Concurrent;
using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Claude.AgentSdk.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>In-process SDK MCP servers bridged to the CLI over mcp_message control requests.</summary>
[Trait("Category", "Integration")]
public class McpTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    private readonly ConcurrentQueue<(int A, int B)> _addCalls = new();
    private readonly ConcurrentQueue<string> _failCalls = new();

    private McpServerRegistry Servers(string failMessage) => McpServers.Sdk("calc", s => s
        .Tool("add", (int a, int b) =>
        {
            _addCalls.Enqueue((a, b));
            return (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }, "Add two integers and return the sum.")
        .Tool("explode", (string label) =>
        {
            _failCalls.Enqueue(label);
            throw new InvalidOperationException(failMessage);
#pragma warning disable CS0162
            return "";
#pragma warning restore CS0162
        }, "A tool that always fails. Call it only when asked to."));

    [IntegrationFact]
    public Task SdkTool_IsCalledWithExactArguments_AndResultReachesModel() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var options = Options() with
        {
            McpServers = Servers("unused"),
            AllowedTools = ["mcp__calc__add"],
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync("Call the mcp__calc__add tool exactly once with a=1729 and b=2718, " +
                                "then reply with only the number it returned.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        var use = ToolUses(messages).FirstOrDefault(t => t.Name == "mcp__calc__add");
        ModelCompliance.Require(use is not null, "model never called mcp__calc__add");

        // The model's arguments reached the C# delegate unchanged, bound by name.
        Assert.Equal(1729, use!.Input.GetProperty("a").GetInt32());
        Assert.Equal(2718, use.Input.GetProperty("b").GetInt32());
        Assert.Contains((1729, 2718), _addCalls);

        var result = Assert.Single(ToolResults(messages), r => r.ToolUseId == use.Id);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("4447", ToolResultText(result).Trim());
        Assert.Contains("4447", SingleResult(messages).Result);
    }, Log);

    [IntegrationFact]
    public Task SdkTool_Exception_IsReportedToModelAsToolError() => ModelCompliance.RetryOnceAsync(async attempt =>
    {
        var failure = $"kaboom-{NewNonce()}";
        var options = Options() with
        {
            McpServers = Servers(failure),
            AllowedTools = ["mcp__calc__explode"],
        };

        await using var client = new ClaudeSDKClient(options, Transport(options, suffix: attempt > 1 ? "retry" : null));
        await client.ConnectAsync(cancellationToken: Ct);
        await client.QueryAsync("Call the mcp__calc__explode tool once with label=\"x\". " +
                                "Do not retry if it fails; just reply with the error text.", cancellationToken: Ct);
        var messages = await CollectAsync(client.ReceiveResponseAsync(Ct));

        var use = ToolUses(messages).FirstOrDefault(t => t.Name == "mcp__calc__explode");
        ModelCompliance.Require(use is not null, "model never called mcp__calc__explode");
        Assert.Contains("x", _failCalls);

        // The handler's exception becomes an is_error tool result carrying its message;
        // the session itself carries on and finishes normally.
        var result = Assert.Single(ToolResults(messages), r => r.ToolUseId == use!.Id);
        Assert.True(result.IsError);
        Assert.Contains(failure, ToolResultText(result));
        Assert.Equal("success", SingleResult(messages).Subtype);
    }, Log);

    /// <summary>
    /// Connect (no query, no model call) and poll mcp_status until the SDK server is
    /// connected: the CLI connects MCP servers in the background after initialize
    /// (sending initialize / tools/list to the SDK bridge), so the list is briefly empty.
    /// </summary>
    private async Task<(ClaudeSDKClient Client, JsonElement Status)> ConnectAndWaitForMcpAsync(string test)
    {
        var options = Options(test) with { McpServers = Servers("unused") };
        var client = new ClaudeSDKClient(options, Transport(options, test));
        await client.ConnectAsync(cancellationToken: Ct);
        JsonElement raw = default;
        for (var i = 0; i < 50; i++)
        {
            raw = await client.GetMcpStatusAsync(Ct);
            var servers = raw.GetProperty("mcpServers");
            if (servers.GetArrayLength() > 0 && servers[0].GetProperty("status").GetString() != "pending")
                break;
            await Task.Delay(200, Ct);
        }
        Log("mcp_status: " + raw.GetRawText());
        return (client, raw);
    }

    [IntegrationFact]
    public async Task GetMcpStatus_ListsConnectedSdkServerAndItsTools_WithoutAnyModelCall()
    {
        var (client, raw) = await ConnectAndWaitForMcpAsync(nameof(GetMcpStatus_ListsConnectedSdkServerAndItsTools_WithoutAnyModelCall));
        await using var _ = client;

        var calc = Assert.Single(raw.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("calc", calc.GetProperty("name").GetString());
        Assert.Equal("connected", calc.GetProperty("status").GetString());
        Assert.Equal("calc", calc.GetProperty("serverInfo").GetProperty("name").GetString());
        var tools = calc.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["add", "explode"], tools.Order().ToList());
    }

    [IntegrationFact]
    public async Task ListMcpServers_ReturnsTypedStatus()
    {
        var (client, _) = await ConnectAndWaitForMcpAsync(nameof(ListMcpServers_ReturnsTypedStatus));
        await using var _c = client;

        var typed = await client.ListMcpServersAsync(Ct);

        var calc = Assert.Single(typed.McpServers);
        Assert.Equal("calc", calc.Name);
        Assert.Equal(McpServerConnectionStatus.Connected, calc.Status);
        Assert.Equal(["add", "explode"], calc.Tools!.Select(t => t.Name).Order().ToList());
    }
}
