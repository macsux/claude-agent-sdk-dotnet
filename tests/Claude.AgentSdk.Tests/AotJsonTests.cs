using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Mcp;
using Xunit;

namespace Claude.AgentSdk.Tests;

/// <summary>
/// The reflection-free JSON plumbing that makes the SDK trim/NativeAOT-safe.
/// </summary>
public class AotJsonTests
{
    public static IEnumerable<object[]> ContextTypeInfos() =>
        typeof(SdkJsonContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => typeof(JsonTypeInfo).IsAssignableFrom(p.PropertyType))
            .Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(ContextTypeInfos))]
    public void EverySourceGeneratedContractConfigures(string property)
    {
        // Metadata problems (e.g. init-only extension data) only surface when the
        // contract is first configured, so touch every one.
        var info = (JsonTypeInfo)typeof(SdkJsonContext).GetProperty(property)!.GetValue(SdkJsonContext.Default)!;
        Assert.NotNull(info.Type);
    }

    [Fact]
    public void SerializeWritesDictionaryTreesWithoutReflection()
    {
        var payload = new Dictionary<string, object?>
        {
            ["s"] = "x",
            ["n"] = 3,
            ["d"] = 1.5,
            ["b"] = true,
            ["null"] = null,
            ["list"] = new List<string> { "a", "b" },
            ["arr"] = new object?[] { 1, "two", null },
            ["el"] = JsonDocument.Parse("""{"k":[1,2]}""").RootElement,
            ["node"] = new JsonObject { ["z"] = 1 },
            ["nested"] = new Dictionary<string, object?> { ["inner"] = new Dictionary<string, string> { ["a"] = "b" } },
            ["key"] = new SessionKey { ProjectKey = "p", SessionId = "s" },
            ["enum"] = PermissionMode.Plan,
        };

        var json = SdkJson.Serialize(payload);

        Assert.Equal(
            """{"s":"x","n":3,"d":1.5,"b":true,"null":null,"list":["a","b"],"arr":[1,"two",null],"el":{"k":[1,2]},"node":{"z":1},"nested":{"inner":{"a":"b"}},"key":{"project_key":"p","session_id":"s","subpath":null},"enum":2}""",
            json);
        // Byte-identical to the reflection serializer the SDK used before.
        Assert.Equal(JsonSerializer.Serialize(payload), json);
    }

    [Fact]
    public void SerializeFallsBackToReflectionForUnknownTypesWhenEnabled()
    {
        // JIT apps keep working with anonymous objects inside prompt dictionaries.
        var json = SdkJson.Serialize(new Dictionary<string, object?> { ["m"] = new { role = "user" } });
        Assert.Equal("""{"m":{"role":"user"}}""", json);
    }

    [Fact]
    public void SerializeRejectsCycles()
    {
        var a = new Dictionary<string, object?>();
        a["self"] = a;
        Assert.Throws<JsonException>(() => SdkJson.Serialize(a));
    }

    [Fact]
    public async Task TypedTool_BindsThroughJsonTypeInfo_AndExportsSchema()
    {
        var server = McpServers.Sdk("calc", b => b.Tool(
            "add",
            TestJsonContext.Default.AddArgs,
            (args, _) => Task.FromResult(McpToolResults.Text((args.A + args.B).ToString())),
            "Add two numbers"));
        var handlers = ((McpSdkServerConfig)server["calc"]).Handlers;

        var tool = Assert.Single(await handlers.ListTools!(CancellationToken.None));
        var schema = tool.InputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("a", out _));

        var ok = await handlers.CallTool!("add", JsonDocument.Parse("""{"a":2,"b":3}""").RootElement, CancellationToken.None);
        Assert.False(ok.IsError);
        Assert.Equal("5", ok.Content[0].Text);

        var bad = await handlers.CallTool!("add", JsonDocument.Parse("""{"a":"x","b":3}""").RootElement, CancellationToken.None);
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task ExplicitSchemaTool_ValidatesAndReceivesRawArguments()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"msg":{"type":"string"}},"required":["msg"]}
            """).RootElement;
        var server = McpServers.Sdk("echo", b => b.Tool(
            "echo", schema,
            (args, _) => Task.FromResult(McpToolResults.Text(args.GetProperty("msg").GetString()!))));
        var handlers = ((McpSdkServerConfig)server["echo"]).Handlers;

        var tool = Assert.Single(await handlers.ListTools!(CancellationToken.None));
        Assert.Equal(schema.GetRawText(), tool.InputSchema!.Value.GetRawText());

        var ok = await handlers.CallTool!("echo", JsonDocument.Parse("""{"msg":"hi"}""").RootElement, CancellationToken.None);
        Assert.Equal("hi", ok.Content[0].Text);

        var missing = await handlers.CallTool!("echo", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Equal("Input validation error: 'msg' is a required property", missing.Content[0].Text);
    }

    [Fact]
    public void ExplicitSchemaTool_RejectsNonObjectSchema()
    {
        Assert.Throws<ArgumentException>(() => McpServers.Sdk("x", b => b.Tool(
            "t", JsonDocument.Parse("[]").RootElement,
            (_, _) => Task.FromResult(McpToolResults.Text("")))));
    }
}

public sealed record AddArgs(int A, int B);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AddArgs))]
internal partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
