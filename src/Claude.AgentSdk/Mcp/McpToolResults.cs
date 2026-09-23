using System.Text.Json;

namespace Claude.AgentSdk.Mcp;

/// <summary>
/// Convenience helpers for producing MCP tool results.
/// </summary>
public static class McpToolResults
{
    public static McpToolResult Text(string text, bool isError = false) => new()
    {
        IsError = isError,
        Content = [new McpContent { Type = "text", Text = text }]
    };

    /// <summary>
    /// An image result: base64-encoded <paramref name="data"/> with its <paramref name="mimeType"/>
    /// (e.g. "image/png"), sent as MCP <c>{"type": "image", "data": ..., "mimeType": ...}</c>.
    /// </summary>
    public static McpToolResult Image(string data, string mimeType, bool isError = false) => new()
    {
        IsError = isError,
        Content = [McpContents.Image(data, mimeType)]
    };
}

/// <summary>
/// Convenience helpers for producing MCP content blocks.
/// </summary>
public static class McpContents
{
    /// <summary>A text content block.</summary>
    public static McpContent Text(string text) => new() { Type = "text", Text = text };

    /// <summary>An image content block: base64-encoded <paramref name="data"/> and its MIME type.</summary>
    public static McpContent Image(string data, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(mimeType);
        return new McpContent
        {
            Type = "image",
            Data = JsonSerializer.SerializeToElement(data, Internal.SdkJsonContext.Default.String),
            MimeType = mimeType
        };
    }
}

