// Claude Agent SDK for .NET
// Trim/NativeAOT-safe JSON plumbing for the control protocol, transport,
// sessions and MCP bridge.

using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Claude.AgentSdk.Mcp;

namespace Claude.AgentSdk.Internal;

/// <summary>
/// Source-generated metadata for every SDK type that crosses a JSON boundary,
/// plus the scalar types a wire payload may carry. Uses the same options as
/// <see cref="JsonSerializerOptions.Default"/> (no naming policy; nulls
/// written unless a member opts out) so the output matches the reflection
/// serializer the SDK used before it was made AOT-compatible.
/// </summary>
[JsonSourceGenerationOptions]
// Scalars (so SdkJson can write any of them through GetTypeInfo(Type)).
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(Int128))]
[JsonSerializable(typeof(UInt128))]
[JsonSerializable(typeof(Half))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Uri))]
[JsonSerializable(typeof(Version))]
[JsonSerializable(typeof(byte[]))]
// Collections of opaque JSON.
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
// Options payloads (--settings, --mcp-config).
[JsonSerializable(typeof(SandboxSettings))]
[JsonSerializable(typeof(McpStdioServerConfig))]
[JsonSerializable(typeof(McpSSEServerConfig))]
[JsonSerializable(typeof(McpHttpServerConfig))]
[JsonSerializable(typeof(McpSdkServerConfig))]
[JsonSerializable(typeof(SdkPluginConfig))]
// Control-protocol responses.
[JsonSerializable(typeof(McpStatusResponse))]
[JsonSerializable(typeof(ContextUsageResponse))]
// Sessions.
[JsonSerializable(typeof(SessionKey))]
[JsonSerializable(typeof(SessionStoreEntry))]
[JsonSerializable(typeof(List<SessionStoreEntry>))]
[JsonSerializable(typeof(IReadOnlyList<SessionStoreEntry>))]
[JsonSerializable(typeof(SessionSummaryEntry))]
[JsonSerializable(typeof(SessionStoreListEntry))]
[JsonSerializable(typeof(SessionListSubkeysKey))]
// Typed hook outputs (HookOutput.HookSpecificOutput).
[JsonSerializable(typeof(PreToolUseHookSpecificOutput))]
[JsonSerializable(typeof(PostToolUseHookSpecificOutput))]
[JsonSerializable(typeof(PostToolUseFailureHookSpecificOutput))]
[JsonSerializable(typeof(UserPromptSubmitHookSpecificOutput))]
[JsonSerializable(typeof(SessionStartHookSpecificOutput))]
[JsonSerializable(typeof(NotificationHookSpecificOutput))]
[JsonSerializable(typeof(SubagentStartHookSpecificOutput))]
[JsonSerializable(typeof(PermissionRequestHookSpecificOutput))]
// TS 0.3.283 hook-specific outputs.
[JsonSerializable(typeof(StopHookSpecificOutput))]
[JsonSerializable(typeof(SubagentStopHookSpecificOutput))]
[JsonSerializable(typeof(UserPromptExpansionHookSpecificOutput))]
[JsonSerializable(typeof(SetupHookSpecificOutput))]
[JsonSerializable(typeof(PreModelSwitchHookSpecificOutput))]
[JsonSerializable(typeof(PostModelSwitchHookSpecificOutput))]
[JsonSerializable(typeof(PostToolBatchHookSpecificOutput))]
[JsonSerializable(typeof(PermissionDeniedHookSpecificOutput))]
[JsonSerializable(typeof(ElicitationHookSpecificOutput))]
[JsonSerializable(typeof(ElicitationResultHookSpecificOutput))]
[JsonSerializable(typeof(CwdChangedHookSpecificOutput))]
[JsonSerializable(typeof(FileChangedHookSpecificOutput))]
[JsonSerializable(typeof(WorktreeCreateHookSpecificOutput))]
[JsonSerializable(typeof(MessageDisplayHookSpecificOutput))]
// TS 0.3.283 info types (initialize response, context usage, usage report).
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(SlashCommand))]
[JsonSerializable(typeof(List<SlashCommand>))]
[JsonSerializable(typeof(AgentInfo))]
[JsonSerializable(typeof(List<AgentInfo>))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(List<ModelInfo>))]
[JsonSerializable(typeof(AccountInfo))]
[JsonSerializable(typeof(SdkContextUsage))]
[JsonSerializable(typeof(SdkUsageReport))]
// In-process MCP server results.
[JsonSerializable(typeof(McpToolAnnotations))]
[JsonSerializable(typeof(McpToolResult))]
[JsonSerializable(typeof(McpContent))]
[JsonSerializable(typeof(McpPromptDefinition))]
[JsonSerializable(typeof(IReadOnlyList<McpPromptDefinition>))]
[JsonSerializable(typeof(McpPromptResult))]
[JsonSerializable(typeof(McpResourceDefinition))]
[JsonSerializable(typeof(IReadOnlyList<McpResourceDefinition>))]
[JsonSerializable(typeof(McpResourceResult))]
internal partial class SdkJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Writes loosely-typed wire payloads (<c>Dictionary&lt;string, object?&gt;</c>
/// trees, as used throughout the control protocol) without reflection.
/// </summary>
/// <remarks>
/// <para>Values are written structurally when they are <c>null</c>, strings,
/// booleans, enums, <see cref="JsonElement"/>, <see cref="JsonDocument"/>,
/// <see cref="JsonNode"/>, dictionaries or sequences; SDK types and scalars go
/// through <see cref="SdkJsonContext"/>. That covers everything the SDK itself
/// produces, and everything a caller can build from JSON DOM types and
/// primitives, so those payloads are trim- and NativeAOT-safe.</para>
/// <para>Any other object (an anonymous type, a user POCO) falls back to the
/// reflection-based serializer when reflection is enabled (the default for
/// JIT apps), matching the SDK's historical behavior. Trimmed and NativeAOT
/// apps disable reflection-based serialization by default; there such values
/// throw <see cref="NotSupportedException"/> and callers should pass a
/// <see cref="JsonElement"/> / <see cref="JsonNode"/> instead.</para>
/// </remarks>
internal static class SdkJson
{
    // Mirrors System.Text.Json's default JsonSerializerOptions.MaxDepth.
    private const int MaxDepth = 64;

    /// <summary>Serialize <paramref name="value"/> to a JSON string.</summary>
    public static string Serialize(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Serialize <paramref name="value"/> to a standalone <see cref="JsonElement"/>.</summary>
    public static JsonElement SerializeToElement(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }
        return JsonSerializer.Deserialize(buffer.WrittenSpan, SdkJsonContext.Default.JsonElement);
    }

    /// <summary>An empty JSON object (<c>{}</c>).</summary>
    public static JsonElement EmptyObject() => ParseElement("{}");

    /// <summary>Parse <paramref name="json"/> into a standalone <see cref="JsonElement"/>.</summary>
    public static JsonElement ParseElement(string json) =>
        JsonSerializer.Deserialize(json, SdkJsonContext.Default.JsonElement);

    /// <summary>Serialize a typed value with source-generated metadata.</summary>
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    /// <summary>Write <paramref name="value"/> to <paramref name="writer"/>.</summary>
    public static void Write(Utf8JsonWriter writer, object? value) => Write(writer, value, 0);

    private static void Write(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new JsonException(
                $"The payload exceeds the maximum depth of {MaxDepth} (possible object cycle).");

        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Undefined)
                    writer.WriteNullValue();
                else
                    element.WriteTo(writer);
                return;
            case JsonDocument document:
                document.WriteTo(writer);
                return;
            case JsonNode node:
                node.WriteTo(writer);
                return;
            case Enum e:
                WriteEnum(writer, e);
                return;
        }

        var type = value.GetType();
        if (SdkJsonContext.Default.GetTypeInfo(type) is { } typeInfo)
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
            return;
        }

        switch (value)
        {
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(KeyToString(entry.Key));
                    Write(writer, entry.Value, depth + 1);
                }
                writer.WriteEndObject();
                return;
            case IReadOnlyDictionary<string, object?> roDictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in roDictionary)
                {
                    writer.WritePropertyName(key);
                    Write(writer, item, depth + 1);
                }
                writer.WriteEndObject();
                return;
            case IReadOnlyDictionary<string, string> stringDictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in stringDictionary)
                    writer.WriteString(key, item);
                writer.WriteEndObject();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                    Write(writer, item, depth + 1);
                writer.WriteEndArray();
                return;
        }

        WriteWithReflection(writer, value);
    }

    private static string KeyToString(object key) => key switch
    {
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => key.ToString() ?? ""
    };

    // System.Text.Json's default enum handling: the underlying integral value.
    private static void WriteEnum(Utf8JsonWriter writer, Enum value)
    {
        switch (Type.GetTypeCode(value.GetType()))
        {
            case TypeCode.UInt64:
                writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    // Justification: this is only reached for values outside the SDK's own
    // types (e.g. a caller's anonymous object inside a prompt dictionary), and
    // only when JsonSerializer.IsReflectionEnabledByDefault is true. That
    // feature switch defaults to false for PublishTrimmed / PublishAot apps
    // (and the trimmer substitutes it, removing this branch), so in those apps
    // the call is never made and a NotSupportedException explains what to pass
    // instead. An app that explicitly re-enables reflection under trimming has
    // opted into the same trade-off System.Text.Json itself makes.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault; see comment above.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Guarded by JsonSerializer.IsReflectionEnabledByDefault; see comment above.")]
    private static void WriteWithReflection(Utf8JsonWriter writer, object value)
    {
        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new NotSupportedException(
                $"Cannot serialize a value of type '{value.GetType()}' in a trimmed or NativeAOT app: " +
                "reflection-based JSON serialization is disabled. Pass a JsonElement, JsonNode, " +
                "dictionary, list or primitive value instead.");
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), JsonSerializerOptions.Default);
    }
}
