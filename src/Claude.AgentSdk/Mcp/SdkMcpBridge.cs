// Claude Agent SDK for .NET
// SDK MCP Server Bridge - enables in-process MCP servers via handler registration

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Claude.AgentSdk.Internal;

namespace Claude.AgentSdk.Mcp;

/// <summary>
/// Delegate for listing tools available in an MCP server.
/// </summary>
public delegate Task<IReadOnlyList<McpToolDefinition>> ListToolsDelegate(CancellationToken ct);

/// <summary>
/// Delegate for calling a tool in an MCP server.
/// </summary>
public delegate Task<McpToolResult> CallToolDelegate(string name, JsonElement arguments, CancellationToken ct);

/// <summary>
/// Delegate for listing prompts available in an MCP server.
/// </summary>
public delegate Task<IReadOnlyList<McpPromptDefinition>> ListPromptsDelegate(CancellationToken ct);

/// <summary>
/// Delegate for getting a prompt from an MCP server.
/// </summary>
public delegate Task<McpPromptResult> GetPromptDelegate(string name, Dictionary<string, string>? arguments, CancellationToken ct);

/// <summary>
/// Delegate for listing resources available in an MCP server.
/// </summary>
public delegate Task<IReadOnlyList<McpResourceDefinition>> ListResourcesDelegate(CancellationToken ct);

/// <summary>
/// Delegate for reading a resource from an MCP server.
/// </summary>
public delegate Task<McpResourceResult> ReadResourceDelegate(string uri, CancellationToken ct);

/// <summary>
/// Handlers for an in-process MCP server.
/// </summary>
public class McpServerHandlers
{
    /// <summary>Handler for tools/list requests.</summary>
    public ListToolsDelegate? ListTools { get; init; }

    /// <summary>Handler for tools/call requests.</summary>
    public CallToolDelegate? CallTool { get; init; }

    /// <summary>Handler for prompts/list requests.</summary>
    public ListPromptsDelegate? ListPrompts { get; init; }

    /// <summary>Handler for prompts/get requests.</summary>
    public GetPromptDelegate? GetPrompt { get; init; }

    /// <summary>Handler for resources/list requests.</summary>
    public ListResourcesDelegate? ListResources { get; init; }

    /// <summary>Handler for resources/read requests.</summary>
    public ReadResourceDelegate? ReadResource { get; init; }
}

/// <summary>
/// Annotations for an MCP tool describing its behavior hints.
/// </summary>
public class McpToolAnnotations
{
    /// <summary>Human-readable title for the tool.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>If true, the tool does not modify state.</summary>
    [JsonPropertyName("readOnlyHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReadOnlyHint { get; init; }

    /// <summary>If true, the tool may perform destructive operations.</summary>
    [JsonPropertyName("destructiveHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DestructiveHint { get; init; }

    /// <summary>If true, repeated calls with same args have no additional effect.</summary>
    [JsonPropertyName("idempotentHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IdempotentHint { get; init; }

    /// <summary>If true, the tool interacts with entities beyond its host environment.</summary>
    [JsonPropertyName("openWorldHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OpenWorldHint { get; init; }

    /// <summary>
    /// Claude Code hint (not an MCP hint): the size, in characters, up to which Claude Code keeps
    /// this tool's result inline instead of persisting it to a file and showing a preview.
    /// </summary>
    /// <remarks>
    /// MCP clients drop annotation fields they do not know, so this is not written on the
    /// annotations object. Tools registered through <see cref="McpSdkServerBuilder"/> carry it in
    /// the tool's <c>_meta</c> as <c>"anthropic/maxResultSizeChars"</c>, as the Python SDK does.
    /// </remarks>
    [JsonIgnore]
    public int? MaxResultSizeChars { get; init; }
}

/// <summary>
/// Definition of an MCP tool.
/// </summary>
public class McpToolDefinition
{
    /// <summary>Tool name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Tool description.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>JSON Schema for the tool's input parameters.</summary>
    [JsonPropertyName("inputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? InputSchema { get; init; }

    /// <summary>Tool behavior annotations/hints.</summary>
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpToolAnnotations? Annotations { get; init; }

    /// <summary>
    /// MCP <c>_meta</c> for the tool (namespaced client hints such as
    /// <c>"anthropic/maxResultSizeChars"</c>).
    /// </summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Meta { get; init; }
}

/// <summary>
/// Result of an MCP tool call.
/// </summary>
public class McpToolResult
{
    /// <summary>Content blocks returned by the tool.</summary>
    [JsonPropertyName("content")]
    public required IReadOnlyList<McpContent> Content { get; init; }

    /// <summary>Whether the tool execution resulted in an error.</summary>
    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

/// <summary>
/// MCP content block (text, image, or other types).
/// </summary>
public class McpContent
{
    /// <summary>Content type (e.g., "text", "image").</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Text content (for type="text").</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Additional data (for other types; base64 string for type="image").</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    /// <summary>MIME type of <see cref="Data"/> (required for type="image", e.g. "image/png").</summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }
}

/// <summary>
/// Definition of an MCP prompt.
/// </summary>
public class McpPromptDefinition
{
    /// <summary>Prompt name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Prompt description.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Arguments the prompt accepts.</summary>
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<McpPromptArgument>? Arguments { get; init; }
}

/// <summary>
/// Argument definition for an MCP prompt.
/// </summary>
public class McpPromptArgument
{
    /// <summary>Argument name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Argument description.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Whether the argument is required.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }
}

/// <summary>
/// Result of getting an MCP prompt.
/// </summary>
public class McpPromptResult
{
    /// <summary>Description of the prompt.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Messages that make up the prompt.</summary>
    [JsonPropertyName("messages")]
    public required IReadOnlyList<McpPromptMessage> Messages { get; init; }
}

/// <summary>
/// A message in an MCP prompt.
/// </summary>
public class McpPromptMessage
{
    /// <summary>Role of the message (e.g., "user", "assistant").</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>Content of the message.</summary>
    [JsonPropertyName("content")]
    public required McpContent Content { get; init; }
}

/// <summary>
/// Definition of an MCP resource.
/// </summary>
public class McpResourceDefinition
{
    /// <summary>Resource URI.</summary>
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    /// <summary>Resource name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Resource description.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>MIME type of the resource.</summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }
}

/// <summary>
/// Result of reading an MCP resource.
/// </summary>
public class McpResourceResult
{
    /// <summary>Contents of the resource.</summary>
    [JsonPropertyName("contents")]
    public required IReadOnlyList<McpResourceContent> Contents { get; init; }
}

/// <summary>
/// Content of an MCP resource.
/// </summary>
public class McpResourceContent
{
    /// <summary>Resource URI.</summary>
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    /// <summary>MIME type.</summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    /// <summary>Text content.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Binary content (base64 encoded).</summary>
    [JsonPropertyName("blob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blob { get; init; }
}

/// <summary>
/// Bridge between the Claude Agent SDK control protocol and an in-process MCP server.
/// Provides handler-based routing for JSONRPC messages.
/// </summary>
/// <remarks>
/// Mirrors the request semantics of the Python SDK, whose bridge serves a real
/// <c>mcp.server.Server</c>: requests are handled concurrently (the CLI may have several tool
/// calls in flight on one server), <c>ping</c> is answered, a <c>notifications/cancelled</c>
/// cancels the matching in-flight request (which is answered with a "Request cancelled"
/// error), a request reusing the id of one still in flight is refused, and methods the
/// server does not implement are answered with JSON-RPC "Method not found" (-32601).
/// </remarks>
internal class SdkMcpBridge : IAsyncDisposable
{
    // JSON-RPC error codes.
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;
    // What mcp answers for a request the client cancelled (see _mcp_compat._REQUEST_CANCELLED).
    private const int RequestCancelled = -32800;

    private readonly McpServerHandlers _handlers;
    private readonly string _serverName;

    // In-flight requests keyed by the raw JSON text of their id (so 7 and "7" stay distinct).
    private readonly ConcurrentDictionary<string, InFlightRequest> _inFlight = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    /// <summary>
    /// Create a new SDK MCP bridge.
    /// </summary>
    /// <param name="handlers">The MCP server handlers.</param>
    /// <param name="serverName">Name for logging/diagnostics.</param>
    public SdkMcpBridge(McpServerHandlers handlers, string serverName)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
    }

    /// <summary>
    /// Start the MCP bridge (no-op for handler-based implementation).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SdkMcpBridge));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Send a JSONRPC message to the MCP server and get the response. Notifications and
    /// responses (which expect no reply) are answered with an empty-result ack.
    /// </summary>
    /// <param name="message">The JSONRPC request message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSONRPC response from the server.</returns>
    public async Task<JsonElement> SendMessageAsync(
        JsonElement message,
        CancellationToken cancellationToken = default)
        => await HandleAsync(message, cancellationToken).ConfigureAwait(false) ?? Ack();

    /// <summary>
    /// Handle one JSON-RPC message from the CLI. Returns the JSON-RPC response for
    /// requests, and <c>null</c> for notifications and responses, which expect no reply.
    /// Python: <c>SdkMcpBridge.handle</c>.
    /// </summary>
    public async Task<JsonElement?> HandleAsync(
        JsonElement message,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SdkMcpBridge));

        var method = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        var hasId = message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind != JsonValueKind.Null;
        var id = hasId ? message.GetProperty("id").Clone() : default;
        var paramsEl = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("params", out var p) ? p : default;

        // Notifications (no id) and responses expect no reply; the caller acks the
        // control request that carried one (Python: {"jsonrpc": "2.0", "result": {}}).
        if (!hasId)
        {
            if (method == "notifications/cancelled")
                CancelInFlight(paramsEl);
            return null;
        }

        if (method == null)
            return Error(id, InternalError, "Invalid JSON-RPC message: missing 'method'");

        var key = id.GetRawText();
        using var request = new InFlightRequest(cancellationToken);
        if (!_inFlight.TryAdd(key, request))
            return Error(id, InternalError, $"Request id {key} is already in flight");

        try
        {
            // Re-check after registering so DisposeAsync cannot miss this request.
            if (_disposed)
                throw new ObjectDisposedException(nameof(SdkMcpBridge));

            object? result;
            try
            {
                result = await DispatchAsync(method, paramsEl, request.Token)
                    .WaitAsync(request.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The control request itself was cancelled; the caller writes no response.
                throw;
            }
            catch (MethodNotFoundException)
            {
                return Error(id, MethodNotFound, "Method not found");
            }
            catch (Exception ex)
            {
                if (!request.CancelledByClient)
                    return Error(id, InternalError, ex.Message);
                result = null;
            }

            // A request the client cancelled is answered as cancelled and whatever the
            // handler produced is discarded (as mcp does).
            if (request.CancelledByClient)
                return Error(id, RequestCancelled, "Request cancelled");

            return SdkJson.SerializeToElement(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            });
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, InFlightRequest>(key, request));
        }
    }

    private Task<object> DispatchAsync(string method, JsonElement paramsEl, CancellationToken ct) => method switch
    {
        "initialize" => Task.FromResult(HandleInitialize()),
        "ping" => Task.FromResult<object>(SdkJson.EmptyObject()),
        "tools/list" => HandleToolsListAsync(ct),
        "tools/call" => HandleToolsCallAsync(paramsEl, ct),
        "prompts/list" => HandlePromptsListAsync(ct),
        "prompts/get" => HandlePromptsGetAsync(paramsEl, ct),
        "resources/list" => HandleResourcesListAsync(ct),
        "resources/read" => HandleResourcesReadAsync(paramsEl, ct),
        _ => throw new MethodNotFoundException()
    };

    private void CancelInFlight(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object ||
            !paramsEl.TryGetProperty("requestId", out var requestId))
            return;

        if (_inFlight.TryGetValue(requestId.GetRawText(), out var request))
            request.CancelByClient();
    }

    private static JsonElement Ack() => SdkJson.ParseElement("""{"jsonrpc":"2.0","result":{}}""");

    private static JsonElement Error(JsonElement id, int code, string message) => SdkJson.SerializeToElement(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.ValueKind != JsonValueKind.Undefined ? id : null,
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message }
    });

    private object HandleInitialize()
    {
        // Build capabilities dynamically - only include supported capabilities
        var capabilities = new Dictionary<string, object?>();
        if (_handlers.ListTools != null)
            capabilities["tools"] = new Dictionary<string, object?>();
        if (_handlers.ListPrompts != null)
            capabilities["prompts"] = new Dictionary<string, object?>();
        if (_handlers.ListResources != null)
            capabilities["resources"] = new Dictionary<string, object?>();

        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = capabilities,
            ["serverInfo"] = new Dictionary<string, object?>
            {
                ["name"] = _serverName,
                ["version"] = "1.0.0"
            }
        };
    }

    // A method whose handler is not registered is unimplemented: mcp answers those with
    // "Method not found" (-32601), and so does this bridge.

    private async Task<object> HandleToolsListAsync(CancellationToken ct)
    {
        if (_handlers.ListTools == null)
            throw new MethodNotFoundException();

        var tools = await _handlers.ListTools(ct);
        return new Dictionary<string, object?> { ["tools"] = tools?.Select(ToolDefinitionToWire).ToList() };
    }

    private async Task<object> HandleToolsCallAsync(JsonElement paramsEl, CancellationToken ct)
    {
        if (_handlers.CallTool == null)
            throw new MethodNotFoundException();

        var name = paramsEl.GetProperty("name").GetString()!;
        var arguments = paramsEl.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null
            ? args
            : SdkJson.EmptyObject();

        var result = await _handlers.CallTool(name, arguments, ct);
        return result;
    }

    private async Task<object> HandlePromptsListAsync(CancellationToken ct)
    {
        if (_handlers.ListPrompts == null)
            throw new MethodNotFoundException();

        var prompts = await _handlers.ListPrompts(ct);
        return new Dictionary<string, object?> { ["prompts"] = prompts };
    }

    private async Task<object> HandlePromptsGetAsync(JsonElement paramsEl, CancellationToken ct)
    {
        if (_handlers.GetPrompt == null)
            throw new MethodNotFoundException();

        var name = paramsEl.GetProperty("name").GetString()!;
        var arguments = paramsEl.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null
            ? args.Deserialize(SdkJsonContext.Default.DictionaryStringString)
            : null;

        var result = await _handlers.GetPrompt(name, arguments, ct);
        return result;
    }

    private async Task<object> HandleResourcesListAsync(CancellationToken ct)
    {
        if (_handlers.ListResources == null)
            throw new MethodNotFoundException();

        var resources = await _handlers.ListResources(ct);
        return new Dictionary<string, object?> { ["resources"] = resources };
    }

    private async Task<object> HandleResourcesReadAsync(JsonElement paramsEl, CancellationToken ct)
    {
        if (_handlers.ReadResource == null)
            throw new MethodNotFoundException();

        var uri = paramsEl.GetProperty("uri").GetString()!;
        var result = await _handlers.ReadResource(uri, ct);
        return result;
    }

    /// <summary>
    /// Wire form of a tool definition. Written by hand (rather than with a serializer
    /// contract) because <see cref="McpToolDefinition.Meta"/> holds arbitrary values.
    /// </summary>
    private static Dictionary<string, object?> ToolDefinitionToWire(McpToolDefinition tool)
    {
        var wire = new Dictionary<string, object?> { ["name"] = tool.Name };
        if (tool.Description != null)
            wire["description"] = tool.Description;
        if (tool.InputSchema is { } schema)
            wire["inputSchema"] = schema;
        if (tool.Annotations != null)
            wire["annotations"] = tool.Annotations;
        if (tool.Meta != null)
            wire["_meta"] = tool.Meta;
        return wire;
    }

    /// <summary>
    /// Clean up resources: refuse new messages and cancel whatever is still in flight
    /// (as the Python bridge cancels in-flight calls when its session closes).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        foreach (var request in _inFlight.Values)
            request.CancelByClient();

        return ValueTask.CompletedTask;
    }

    private sealed class MethodNotFoundException : Exception;

    /// <summary>
    /// Cancellation state for one in-flight request: cancelled either by the caller's token
    /// (the control request itself was cancelled) or by the client's notifications/cancelled.
    /// </summary>
    private sealed class InFlightRequest : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private int _cancelledByClient;
        private int _disposed;

        public InFlightRequest(CancellationToken callerToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            Token = _cts.Token;
        }

        public CancellationToken Token { get; }

        public bool CancelledByClient => Volatile.Read(ref _cancelledByClient) == 1;

        public void CancelByClient()
        {
            if (Interlocked.Exchange(ref _cancelledByClient, 1) == 1)
                return;
            if (Volatile.Read(ref _disposed) == 1)
                return;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _cts.Dispose();
        }
    }
}
