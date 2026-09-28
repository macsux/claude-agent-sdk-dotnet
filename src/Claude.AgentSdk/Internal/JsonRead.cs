// Claude Agent SDK for .NET
// Lenient, reflection-free JsonElement readers used by the TS-parity parsers.

using System.Text.Json;

namespace Claude.AgentSdk.Internal;

/// <summary>
/// Lenient accessors over <see cref="JsonElement"/>: a missing, null or
/// mistyped member reads as <c>null</c> instead of throwing. Trim/AOT-safe.
/// </summary>
internal static class JsonRead
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static double? Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    public static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return null;
        if (v.TryGetInt64(out var l))
            return l;
        var d = v.GetDouble();
        return d is >= long.MinValue and <= long.MaxValue ? (long)d : null;
    }

    public static int? Int(JsonElement e, string name)
    {
        var l = Long(e, name);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l.Value : null;
    }

    /// <summary>A member that is present and not JSON <c>null</c>, cloned.</summary>
    public static JsonElement? Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v.Clone()
            : null;

    /// <summary>An object-valued member, or <c>null</c>.</summary>
    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    /// <summary>String array member (non-string items skipped), or <c>null</c> when absent / not an array.</summary>
    public static IReadOnlyList<string>? StrList(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString()!);
        }
        return list;
    }

    /// <summary>
    /// Map each object item of an array member through <paramref name="map"/>;
    /// non-object items and <c>null</c> results are skipped. Empty when absent.
    /// </summary>
    public static IReadOnlyList<T> ObjList<T>(JsonElement e, string name, Func<JsonElement, T?> map) where T : class
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<T>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && map(item) is { } mapped)
                list.Add(mapped);
        }
        return list;
    }

    /// <summary>Like <see cref="ObjList{T}"/> but <c>null</c> when the member is absent / not an array.</summary>
    public static IReadOnlyList<T>? ObjListOrNull<T>(JsonElement e, string name, Func<JsonElement, T?> map) where T : class =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? ObjList(e, name, map)
            : null;

    /// <summary>String at a nested object path, or <c>null</c>.</summary>
    public static string? Path(JsonElement e, params string[] path)
    {
        var cur = e;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (Obj(cur, path[i]) is not { } next)
                return null;
            cur = next;
        }
        return Str(cur, path[^1]);
    }
}
