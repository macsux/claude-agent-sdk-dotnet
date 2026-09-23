using System.ComponentModel;
using System.Text.Json;
using Claude.AgentSdk.Mcp;
using Xunit;

namespace Claude.AgentSdk.Tests;

/// <summary>
/// Input-schema generation and argument binding of <see cref="McpSdkServerBuilder"/> tools
/// (the .NET counterpart of Python's _build_input_schema / _python_type_to_json_schema).
/// </summary>
public sealed class McpSchemaBindingTests
{
    private static McpServerHandlers Handlers(Action<McpSdkServerBuilder> configure) =>
        ((McpSdkServerConfig)McpServers.Sdk("srv", configure)["srv"]).Handlers;

    private static async Task<JsonElement> SchemaOf(Delegate handler)
    {
        var tools = await Handlers(s => s.Tool("t", handler)).ListTools!(CancellationToken.None);
        return tools.Single().InputSchema!.Value;
    }

    private static Task<McpToolResult> Call(Delegate handler, string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return Handlers(s => s.Tool("t", handler)).CallTool!("t", doc.RootElement.Clone(), CancellationToken.None);
    }

    private static string[] Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

    // ---- 1. single non-POCO parameters are named properties -----------------------------

    public enum Color { Red, Green }

    public static IEnumerable<object[]> SingleScalarOrCollectionHandlers() =>
    [
        [(Func<List<int>, string>)(items => string.Join(",", items)), "items", "array", """{"items":[1,2,3]}""", "1,2,3"],
        [(Func<string[], string>)(names => string.Join("+", names)), "names", "array", """{"names":["a","b"]}""", "a+b"],
        [(Func<Guid, string>)(id => id.ToString()), "id", "string", """{"id":"6f9619ff-8b86-d011-b42d-00c04fc964ff"}""", "6f9619ff-8b86-d011-b42d-00c04fc964ff"],
        [(Func<Uri, string>)(url => url.Host), "url", "string", """{"url":"https://example.com/x"}""", "example.com"],
        [(Func<DateTime, string>)(when => when.Year.ToString()), "when", "string", """{"when":"2024-05-06T07:08:09Z"}""", "2024"],
        [(Func<Color, string>)(color => color.ToString()), "color", "string", """{"color":"Green"}""", "Green"],
        [(Func<TimeSpan, string>)(span => span.TotalMinutes.ToString()), "span", "string", """{"span":"00:05:00"}""", "5"],
    ];

    [Theory]
    [MemberData(nameof(SingleScalarOrCollectionHandlers))]
    public async Task SingleNonObjectParameter_IsNamedProperty(Delegate handler, string name, string type, string args, string expected)
    {
        var schema = await SchemaOf(handler);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(type, schema.GetProperty("properties").GetProperty(name).GetProperty("type").GetString());
        Assert.Equal([name], Required(schema));

        var result = await Call(handler, args);
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(expected, result.Content[0].Text);
    }

    [Fact]
    public async Task SingleDictionaryParameter_BindsWholeArgumentsObject()
    {
        Func<Dictionary<string, int>, string> handler = d => string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var schema = await SchemaOf(handler);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("integer", schema.GetProperty("additionalProperties").GetProperty("type").GetString());

        var result = await Call(handler, """{"a":1,"b":2}""");
        Assert.Equal("a=1,b=2", result.Content[0].Text);
    }

    [Fact]
    public async Task ReadOnlyDictionaryProperty_IsObjectNotArray()
    {
        var schema = await SchemaOf((IReadOnlyDictionary<string, string> tags, int n) => n);
        var tags = schema.GetProperty("properties").GetProperty("tags");
        Assert.Equal("object", tags.GetProperty("type").GetString());
        Assert.Equal("string", tags.GetProperty("additionalProperties").GetProperty("type").GetString());
    }

    public sealed record WithEnum(Color Color, int Count);

    [Fact]
    public async Task PocoWithEnumProperty_BindsEnumByName()
    {
        Func<WithEnum, string> handler = a => $"{a.Color}x{a.Count}";
        var schema = await SchemaOf(handler);
        Assert.Equal(["Red", "Green"], schema.GetProperty("properties").GetProperty("color").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));

        var result = await Call(handler, """{"color":"Green","count":2}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal("Greenx2", result.Content[0].Text);
    }

    public sealed class Computed
    {
        public int A { get; set; }
        public int Twice => A * 2;
    }

    [Fact]
    public async Task GetOnlyComputedProperties_AreNotInSchema()
    {
        var schema = await SchemaOf((Computed c) => c.Twice);
        Assert.False(schema.GetProperty("properties").TryGetProperty("twice", out _));
        Assert.Equal(["a"], Required(schema));
    }

    [Fact]
    public async Task ByteArray_IsBase64String()
    {
        Func<byte[], int> handler = data => data.Length;
        var schema = await SchemaOf(handler);
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("3", (await Call(handler, """{"data":"AAEC"}""")).Content[0].Text);
    }

    // ---- 2. recursive types -----------------------------------------------------------------

    public sealed class TreeNode
    {
        public string Name { get; set; } = "";
        public List<TreeNode> Children { get; set; } = [];
        public TreeNode? Parent { get; set; }
    }

    public sealed class Ping { public Pong? Other { get; set; } public int P { get; set; } }
    public sealed class Pong { public Ping? Other { get; set; } public int Q { get; set; } }

    [Fact]
    public async Task RecursiveType_DoesNotOverflow_AndCutsCycleWithObjectSchema()
    {
        Func<TreeNode, int> handler = n => n.Children.Count;
        var schema = await SchemaOf(handler);
        var props = schema.GetProperty("properties");
        Assert.Equal("object", props.GetProperty("children").GetProperty("items").GetProperty("type").GetString());
        Assert.False(props.GetProperty("children").GetProperty("items").TryGetProperty("properties", out _));
        Assert.Equal("""["object","null"]""", props.GetProperty("parent").GetProperty("type").GetRawText());

        var result = await Call(handler, """{"name":"root","children":[{"name":"a","children":[]},{"name":"b","children":[{"name":"c","children":[]}]}],"parent":null}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal("2", result.Content[0].Text);
    }

    [Fact]
    public async Task MutuallyRecursiveTypes_DoNotOverflow()
    {
        var schema = await SchemaOf((Ping p, int x) => x);
        var ping = schema.GetProperty("properties").GetProperty("p");
        var pong = ping.GetProperty("properties").GetProperty("other");
        Assert.True(pong.GetProperty("properties").TryGetProperty("q", out _));
        // Pong.Other is a Ping again: the cycle is cut there.
        Assert.False(pong.GetProperty("properties").GetProperty("other").TryGetProperty("properties", out _));
    }

    public sealed class Expanding<T> { public Expanding<List<T>>? Next { get; set; } }

    [Fact]
    public async Task InfinitelyExpandingGenericType_IsTruncated()
    {
        var schema = await SchemaOf((Expanding<int> e) => 1);
        Assert.Equal("object", schema.GetProperty("type").GetString());
    }

    // ---- 3. nullable / optional parameters ---------------------------------------------------

    [Fact]
    public async Task NullableParameters_AreOptionalAndAcceptExplicitNull()
    {
        Func<string, int?, string?, Color?, string> handler =
            (query, limit, cursor, color) => $"{query}|{limit?.ToString() ?? "none"}|{cursor ?? "none"}|{color?.ToString() ?? "none"}";
        var schema = await SchemaOf(handler);
        var props = schema.GetProperty("properties");

        Assert.Equal(["query"], Required(schema));
        Assert.Equal("\"string\"", props.GetProperty("query").GetProperty("type").GetRawText());
        Assert.Equal("""["integer","null"]""", props.GetProperty("limit").GetProperty("type").GetRawText());
        Assert.Equal("""["string","null"]""", props.GetProperty("cursor").GetProperty("type").GetRawText());
        Assert.Equal("""["Red","Green",null]""", props.GetProperty("color").GetProperty("enum").GetRawText());

        var explicitNulls = await Call(handler, """{"query":"q","limit":null,"cursor":null,"color":null}""");
        Assert.False(explicitNulls.IsError, explicitNulls.Content[0].Text);
        Assert.Equal("q|none|none|none", explicitNulls.Content[0].Text);

        var omitted = await Call(handler, """{"query":"q"}""");
        Assert.Equal("q|none|none|none", omitted.Content[0].Text);

        var provided = await Call(handler, """{"query":"q","limit":3,"cursor":"c","color":"Red"}""");
        Assert.Equal("q|3|c|Red", provided.Content[0].Text);
    }

    [Fact]
    public async Task NonNullableParameter_RejectsExplicitNull()
    {
        var result = await Call((Func<string, int, string>)((a, b) => a + b), """{"a":null,"b":1}""");
        Assert.True(result.IsError);
        Assert.Equal("Input validation error: None is not of type 'string'", result.Content[0].Text);
    }

    [Fact]
    public async Task DefaultedParameter_IsOptionalButNotNullable()
    {
        var schema = await SchemaOf((string q, int limit = 10) => $"{q}{limit}");
        Assert.Equal(["q"], Required(schema));
        Assert.Equal("\"integer\"", schema.GetProperty("properties").GetProperty("limit").GetProperty("type").GetRawText());
        Assert.Equal("x10", (await Call((string q, int limit = 10) => $"{q}{limit}", """{"q":"x"}""")).Content[0].Text);
    }

    public sealed record Filter(string Name, int? Max, string? Note, List<string?> Tags);

    [Fact]
    public async Task NullablePocoProperties_AreOptionalAndNullable()
    {
        Func<Filter, string> handler = f => $"{f.Name}|{f.Max}|{f.Note}|{f.Tags.Count}";
        var schema = await SchemaOf(handler);
        var props = schema.GetProperty("properties");
        Assert.Equal(["name", "tags"], Required(schema));
        Assert.Equal("""["integer","null"]""", props.GetProperty("max").GetProperty("type").GetRawText());
        Assert.Equal("""["string","null"]""", props.GetProperty("note").GetProperty("type").GetRawText());
        Assert.Equal("""["string","null"]""", props.GetProperty("tags").GetProperty("items").GetProperty("type").GetRawText());

        var result = await Call(handler, """{"name":"n","max":null,"note":null,"tags":["a",null]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal("n|||2", result.Content[0].Text);
    }

    [Fact]
    public async Task IntegralFloat_BindsToIntegerParameter()
    {
        var result = await Call((Func<int, long, int>)((a, b) => a + (int)b), """{"a":2.0,"b":3}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal("5", result.Content[0].Text);
    }

    [Fact]
    public async Task ParameterDescriptionAttribute_IsEmitted()
    {
        var schema = await SchemaOf(([Description("Search text")] string q) => q);
        Assert.Equal("Search text", schema.GetProperty("properties").GetProperty("q").GetProperty("description").GetString());
    }

    // ---- 4. result content conversion (Python _convert_tool_content) -------------------------

    [Fact]
    public async Task ResultContent_IsConvertedLikePython()
    {
        Func<McpToolResult> handler = () => new McpToolResult
        {
            IsError = true,
            Content =
            [
                McpContents.Text("t"),
                McpContents.Image("AAE=", "image/png"),
                new McpContent { Type = "resource_link" },
                new McpContent { Type = "resource", Text = "embedded" },
                new McpContent { Type = "resource" },
                new McpContent { Type = "audio", Data = JsonSerializer.SerializeToElement("AAE="), MimeType = "audio/wav" },
            ]
        };

        var result = await Call(handler, "{}");
        Assert.True(result.IsError);
        Assert.Equal(["text", "image", "text", "text"], result.Content.Select(c => c.Type));
        Assert.Equal("Resource link", result.Content[2].Text);
        Assert.Equal("embedded", result.Content[3].Text);
        Assert.Equal("image/png", result.Content[1].MimeType);
    }
}
