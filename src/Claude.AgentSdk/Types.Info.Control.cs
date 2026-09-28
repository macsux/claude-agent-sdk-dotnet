// MERGE: superseded by Types.Info.cs
// Minimal info types used by the typed initialize helpers on ClaudeSDKClient
// (SupportedCommandsAsync, SupportedModelsAsync, ...). The message-types work
// ships the full versions in Types.Info.cs; delete this file when merging.

using System.Text.Json;

namespace Claude.AgentSdk;

/// <summary>A slash command or skill the session offers. TS: <c>SlashCommand</c>.</summary>
public record SlashCommand(string Name, string Description, string ArgumentHint)
{
    internal static SlashCommand Parse(JsonElement e) => new(
        InfoJson.Str(e, "name") ?? "",
        InfoJson.Str(e, "description") ?? "",
        InfoJson.Str(e, "argumentHint") ?? "");
}

/// <summary>A subagent the session can invoke. TS: <c>AgentInfo</c>.</summary>
public record AgentInfo(string Name, string Description, string? Model)
{
    internal static AgentInfo Parse(JsonElement e) => new(
        InfoJson.Str(e, "name") ?? "",
        InfoJson.Str(e, "description") ?? "",
        InfoJson.Str(e, "model"));
}

/// <summary>A model the session can switch to. TS: <c>ModelInfo</c>.</summary>
public record ModelInfo(string Value, string DisplayName, string Description)
{
    internal static ModelInfo Parse(JsonElement e) => new(
        InfoJson.Str(e, "value") ?? "",
        InfoJson.Str(e, "displayName") ?? "",
        InfoJson.Str(e, "description") ?? "");
}

/// <summary>The authenticated account. TS: <c>AccountInfo</c>.</summary>
public record AccountInfo(
    string? Email,
    string? Organization,
    string? SubscriptionType,
    string? TokenSource,
    string? ApiKeySource,
    string? ApiProvider)
{
    internal static AccountInfo Parse(JsonElement e) => new(
        InfoJson.Str(e, "email"),
        InfoJson.Str(e, "organization"),
        InfoJson.Str(e, "subscriptionType"),
        InfoJson.Str(e, "tokenSource"),
        InfoJson.Str(e, "apiKeySource"),
        InfoJson.Str(e, "apiProvider"));
}

/// <summary>
/// The CLI's answer to <c>initialize</c>. TS: <c>SDKControlInitializeResponse</c>.
/// <see cref="Raw"/> keeps the full payload.
/// </summary>
public record InitializeResponse(
    IReadOnlyList<SlashCommand> Commands,
    IReadOnlyList<AgentInfo> Agents,
    string? OutputStyle,
    IReadOnlyList<string> AvailableOutputStyles,
    IReadOnlyList<ModelInfo> Models,
    AccountInfo? Account,
    JsonElement Raw)
{
    /// <summary>Parse an initialize response payload (missing members become empty).</summary>
    public static InitializeResponse Parse(JsonElement raw)
    {
        var obj = raw.ValueKind == JsonValueKind.Object;
        return new InitializeResponse(
            obj ? InfoJson.List(raw, "commands", SlashCommand.Parse) : [],
            obj ? InfoJson.List(raw, "agents", AgentInfo.Parse) : [],
            obj ? InfoJson.Str(raw, "output_style") : null,
            obj ? InfoJson.List(raw, "available_output_styles", s => s.GetString() ?? "") : [],
            obj ? InfoJson.List(raw, "models", ModelInfo.Parse) : [],
            obj && raw.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object
                ? AccountInfo.Parse(acc)
                : null,
            raw.Clone());
    }
}

internal static class InfoJson
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static IReadOnlyList<T> List<T>(JsonElement e, string name, Func<JsonElement, T> parse) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
                .Where(x => x.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                .Select(parse)
                .ToList()
            : [];
}
