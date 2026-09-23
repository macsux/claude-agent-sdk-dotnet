using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Claude.AgentSdk.Mcp;

/// <summary>
/// Minimal JSON Schema validator for tool input, standing in for the Python SDK's
/// <c>jsonschema.validate(instance=arguments, schema=...)</c> in <c>create_sdk_mcp_server</c>.
/// </summary>
/// <remarks>
/// Covers the keywords <see cref="McpSdkServerBuilder"/> emits for tool input schemas
/// (<c>type</c>, <c>properties</c>, <c>required</c>, <c>enum</c>, <c>items</c>,
/// <c>additionalProperties</c>) plus <c>const</c>. Other keywords are ignored, as is
/// <c>format</c> (which jsonschema also does not assert by default). Type semantics follow
/// Draft 2020-12 (an integral float such as <c>1.0</c> is an <c>integer</c>; booleans are not
/// numbers). Error messages mirror jsonschema's wording, and, like <c>jsonschema.best_match</c>,
/// the shallowest error is reported.
/// </remarks>
internal static class McpInputSchemaValidator
{
    /// <summary>
    /// Validate <paramref name="instance"/> against <paramref name="schema"/>.
    /// Returns null when valid, otherwise the message of the most relevant error.
    /// </summary>
    public static string? Validate(JsonElement instance, JsonElement schema)
    {
        var errors = new List<(int Depth, string Message)>();
        ValidateNode(instance, schema, 0, errors);
        if (errors.Count == 0)
            return null;

        var best = errors[0];
        foreach (var e in errors)
        {
            if (e.Depth < best.Depth)
                best = e;
        }

        return best.Message;
    }

    private static void ValidateNode(JsonElement instance, JsonElement schema, int depth, List<(int, string)> errors)
    {
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add((depth, $"False schema does not allow {Repr(instance)}"));
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                : type.ValueKind == JsonValueKind.String ? [type.GetString()!] : [];

            if (types.Count > 0 && !types.Any(t => IsOfType(instance, t)))
                errors.Add((depth, $"{Repr(instance)} is not of type {string.Join(", ", types.Select(ReprString))}"));
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            if (!enumValues.EnumerateArray().Any(v => JsonEquals(v, instance)))
                errors.Add((depth, $"{Repr(instance)} is not one of {Repr(enumValues)}"));
        }

        if (schema.TryGetProperty("const", out var constValue) && !JsonEquals(constValue, instance))
            errors.Add((depth, $"{Repr(constValue)} was expected"));

        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.String && !instance.TryGetProperty(name.GetString()!, out _))
                        errors.Add((depth, $"{ReprString(name.GetString()!)} is a required property"));
                }
            }

            var hasProperties = schema.TryGetProperty("properties", out var properties) &&
                                properties.ValueKind == JsonValueKind.Object;
            var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
            var unexpected = new List<string>();

            foreach (var prop in instance.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(prop.Name, out var propSchema))
                {
                    ValidateNode(prop.Value, propSchema, depth + 1, errors);
                }
                else if (hasAdditional)
                {
                    if (additional.ValueKind == JsonValueKind.False)
                        unexpected.Add(prop.Name);
                    else
                        ValidateNode(prop.Value, additional, depth + 1, errors);
                }
            }

            if (unexpected.Count > 0)
            {
                var verb = unexpected.Count == 1 ? "was" : "were";
                errors.Add((depth, $"Additional properties are not allowed ({string.Join(", ", unexpected.Select(ReprString))} {verb} unexpected)"));
            }
        }

        if (instance.ValueKind == JsonValueKind.Array &&
            schema.TryGetProperty("items", out var items) &&
            (items.ValueKind == JsonValueKind.Object || items.ValueKind == JsonValueKind.False))
        {
            foreach (var item in instance.EnumerateArray())
                ValidateNode(item, items, depth + 1, errors);
        }
    }

    private static bool IsOfType(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && IsIntegral(instance),
        // Unknown type names are left to the handler rather than rejected here.
        _ => true
    };

    private static bool IsIntegral(JsonElement number)
    {
        if (number.TryGetInt64(out _))
            return true;
        if (number.TryGetDecimal(out var dec))
            return decimal.Truncate(dec) == dec;
        return number.TryGetDouble(out var d) && !double.IsInfinity(d) && Math.Floor(d) == d;
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            if (a.TryGetDecimal(out var da) && b.TryGetDecimal(out var db))
                return da == db;
            return a.GetDouble() == b.GetDouble();
        }

        if (a.ValueKind != b.ValueKind)
            return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() &&
                       a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToList();
                return aProps.Count == b.EnumerateObject().Count() &&
                       aProps.All(p => b.TryGetProperty(p.Name, out var bv) && JsonEquals(p.Value, bv));
            default:
                return true; // True, False, Null
        }
    }

    // Python repr() of the decoded JSON value, as jsonschema's messages show it.
    private static string Repr(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ReprString(value.GetString()!);
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.Null:
                return "None";
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.Array:
                return "[" + string.Join(", ", value.EnumerateArray().Select(Repr)) + "]";
            case JsonValueKind.Object:
                return "{" + string.Join(", ", value.EnumerateObject().Select(p => $"{ReprString(p.Name)}: {Repr(p.Value)}")) + "}";
            default:
                return value.GetRawText();
        }
    }

    private static string ReprString(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote)
                        sb.Append('\\').Append(c);
                    else if (c < 0x20)
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append(quote).ToString();
    }
}
