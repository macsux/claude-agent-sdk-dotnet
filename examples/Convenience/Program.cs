// One-call helpers, typed structured output, attribute-based MCP tools and
// ILogger integration. Everything here is trim/NativeAOT-safe.

using System.Text.Json.Serialization;
using Claude.AgentSdk;
using Claude.AgentSdk.Mcp;
using Microsoft.Extensions.Logging;
using ClaudeApi = Claude.AgentSdk.Claude;

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));

var options = new ClaudeAgentOptions
{
    Model = "claude-haiku-4-5-20251001",
    MaxTurns = 4,
    // Debug level shows CLI spawn flags and control-protocol traffic (never prompts or values).
    Logger = loggerFactory.CreateLogger("Claude")
};

// 1. Plain text answer in one call.
var answer = await ClaudeApi.QueryTextAsync("What is 17 * 3? Reply with only the number.", options);
Console.WriteLine($"QueryTextAsync: {answer}");

// 2. Typed structured output: the JSON schema is derived from the source-generated metadata.
var forecast = await ClaudeApi.QueryAsync(
    "Invent a plausible weather forecast for Lisbon tomorrow.",
    ExampleJson.Default.Forecast,
    options);
Console.WriteLine($"QueryAsync<T>: {forecast.City}, high {forecast.HighCelsius}°C, {string.Join(", ", forecast.Conditions)}");

// 3. Attribute-based in-process MCP tools.
var tools = new InventoryTools();
var toolOptions = options with
{
    McpServers = McpServers.Sdk("inventory", b => b.ToolsFrom(tools, ExampleJson.Default)),
    AllowedTools = ["mcp__inventory__stock_level"]
};
var stock = await ClaudeApi.QueryTextAsync("How many blue widgets are in stock? Use the tool.", toolOptions);
Console.WriteLine($"[McpTool]: {stock}");

public sealed record Forecast(string City, int HighCelsius, IReadOnlyList<string> Conditions);

public sealed record StockArgs(string Sku);

public sealed class InventoryTools
{
    private readonly Dictionary<string, int> _stock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blue-widget"] = 42,
        ["red-widget"] = 0
    };

    [McpTool("stock_level", Description = "Units in stock for a SKU such as 'blue-widget'.", ReadOnly = true)]
    public string StockLevel(StockArgs args) =>
        _stock.TryGetValue(args.Sku, out var n) ? $"{n} units of {args.Sku}" : $"Unknown SKU '{args.Sku}'";
}

[JsonSerializable(typeof(Forecast))]
[JsonSerializable(typeof(StockArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ExampleJson : JsonSerializerContext;
