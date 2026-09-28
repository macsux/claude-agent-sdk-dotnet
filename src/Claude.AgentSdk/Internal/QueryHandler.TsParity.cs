// Claude Agent SDK for .NET
// TypeScript SDK parity for the control protocol: inbound elicitation / user
// dialog / auth-refresh handlers and the TS-only outbound Query methods.
// Wire shapes checked against @anthropic-ai/claude-agent-sdk 0.3.283 sdk.mjs
// (Query.processControlRequest and the Query request methods).

using System.Text.Json;
using Claude.AgentSdk.Mcp;
using Microsoft.Extensions.Logging;

namespace Claude.AgentSdk.Internal;

internal partial class QueryHandler
{
    #region Inbound (CLI -> SDK)

    private async Task<object> HandleElicitationAsync(
        JsonElement request, ControlRequestContext context, CancellationToken cancellationToken)
    {
        // TS: no handler → decline (not an error).
        if (_options.OnElicitation is not { } callback)
            return new Dictionary<string, object?> { ["action"] = "decline" };

        var req = new ElicitationRequest(
            ServerName: OptString(request, "mcp_server_name") ?? "",
            Message: OptString(request, "message") ?? "",
            Mode: OptString(request, "mode"),
            Url: OptString(request, "url"),
            ElicitationId: OptString(request, "elicitation_id"),
            RequestedSchema: request.TryGetProperty("requested_schema", out var schema) &&
                             schema.ValueKind == JsonValueKind.Object
                ? schema.Clone()
                : null,
            Title: OptString(request, "title"),
            DisplayName: OptString(request, "display_name"),
            Description: OptString(request, "description"));

        var result = await callback(req, context, cancellationToken);
        if (result is null)
            return SuppressResponse;

        var response = new Dictionary<string, object?> { ["action"] = result.Action.ToWire() };
        if (result.Content is { } content)
            response["content"] = content;
        return response;
    }

    private async Task<object> HandleUserDialogAsync(
        JsonElement request, ControlRequestContext context, CancellationToken cancellationToken)
    {
        // TS: no handler → stay silent so a renderer-bearing client (or the
        // worker's park deadline) settles the dialog.
        if (_options.OnUserDialog is not { } callback)
        {
            _logger.LogDebug("No OnUserDialog handler for request_user_dialog; staying silent");
            return SuppressResponse;
        }

        var req = new UserDialogRequest(
            DialogKind: OptString(request, "dialog_kind") ?? "",
            Payload: request.TryGetProperty("payload", out var payload) ? payload.Clone() : SdkJson.EmptyObject(),
            ToolUseId: OptString(request, "tool_use_id"));

        var result = await callback(req, context, cancellationToken);
        return result switch
        {
            null => SuppressResponse,
            UserDialogResult.Completed c => new Dictionary<string, object?>
            {
                ["behavior"] = "completed",
                ["result"] = c.Result
            },
            _ => new Dictionary<string, object?> { ["behavior"] = "cancelled" }
        };
    }

    private async Task<object> HandleOAuthTokenRefreshAsync(CancellationToken cancellationToken)
    {
        if (_options.GetOAuthToken is not { } callback)
            throw new ClaudeSDKException("GetOAuthToken callback is not provided.");

        var result = await callback(cancellationToken);
        var token = result?.AccessToken;
        var response = new Dictionary<string, object?> { ["accessToken"] = token };
        if (token is null && OAuthDeclineReason.IsKnown(result?.DeclineReason))
            response["reason"] = result!.DeclineReason;
        return response;
    }

    private async Task<object> HandleHostAuthTokenRefreshAsync(CancellationToken cancellationToken)
    {
        if (_options.GetHostAuthToken is not { } callback)
            throw new ClaudeSDKException("GetHostAuthToken callback is not provided.");

        var token = await callback(cancellationToken);
        return new Dictionary<string, object?> { ["authToken"] = token };
    }

    #endregion

    #region Outbound (SDK -> CLI)

    public async Task<InterruptReceipt?> InterruptWithReceiptAsync(bool cancelQueued, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?> { ["subtype"] = "interrupt" };
        if (cancelQueued)
            request["cancel_queued"] = true;
        var response = await SendAsync(request, cancellationToken);
        return ParseInterruptReceipt(response);
    }

    internal static InterruptReceipt? ParseInterruptReceipt(JsonElement response)
    {
        // Older CLIs (no interrupt_receipt_v1) answer {}: no receipt.
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("still_queued", out var sq) ||
            sq.ValueKind != JsonValueKind.Array)
            return null;
        IReadOnlyList<string>? cancelled =
            response.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.Array
                ? Strings(c)
                : null;
        return new InterruptReceipt(Strings(sq), cancelled);
    }

    public async Task<RewindFilesResult> RewindFilesWithResultAsync(
        string userMessageId, bool? dryRun, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?>
        {
            ["subtype"] = "rewind_files",
            ["user_message_id"] = userMessageId
        };
        if (dryRun is { } d)
            request["dry_run"] = d;
        var r = await SendAsync(request, cancellationToken);
        return new RewindFilesResult
        {
            CanRewind = OptBool(r, "canRewind") ?? false,
            Error = OptString(r, "error"),
            FilesChanged = r.TryGetProperty("filesChanged", out var fc) && fc.ValueKind == JsonValueKind.Array
                ? Strings(fc)
                : null,
            Insertions = OptInt(r, "insertions"),
            Deletions = OptInt(r, "deletions"),
            SkippedLinks = OptInt(r, "skippedLinks"),
            Raw = r
        };
    }

    public Task<JsonElement> GetContextUsageAsync(ContextUsageDetail detail, CancellationToken cancellationToken) =>
        SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "get_context_usage",
            ["detail"] = detail.ToWire()
        }, cancellationToken);

    public async Task SetMaxThinkingTokensAsync(
        int? maxThinkingTokens, ThinkingDisplayMode? display, bool clearDisplay, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?>
        {
            ["subtype"] = "set_max_thinking_tokens",
            ["max_thinking_tokens"] = maxThinkingTokens
        };
        // Omitted keeps the session's display mode; null clears the override.
        if (display is { } d)
            request["thinking_display"] = d.ToWire();
        else if (clearDisplay)
            request["thinking_display"] = null;
        await SendAsync(request, cancellationToken);
    }

    public async Task<string?> SetMcpPermissionModeOverrideAsync(
        string serverName, McpPermissionModeOverride? mode, CancellationToken cancellationToken)
    {
        var r = await SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "set_mcp_permission_mode_override",
            ["serverName"] = serverName,
            ["mode"] = mode?.ToWire()
        }, cancellationToken);
        return OptString(r, "warning");
    }

    public async Task ApplyFlagSettingsAsync(IReadOnlyDictionary<string, object?> settings, CancellationToken cancellationToken)
    {
        // Copy into a Dictionary so explicit nulls are written as JSON null
        // (they clear a key from the flag layer).
        await SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "apply_flag_settings",
            ["settings"] = new Dictionary<string, object?>(settings)
        }, cancellationToken);
    }

    public async Task UpdateSettingsAsync(
        SettingsFileSource source, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        await SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "update_settings",
            ["source"] = source.ToWire(),
            ["settings"] = new Dictionary<string, string>(settings)
        }, cancellationToken);
    }

    public async Task<bool> BackgroundTasksAsync(string? toolUseId, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?> { ["subtype"] = "background_tasks" };
        if (toolUseId != null)
            request["tool_use_id"] = toolUseId;
        var r = await SendAsync(request, cancellationToken);
        return OptBool(r, "backgrounded") ?? true;
    }

    public async Task SeedReadStateAsync(string path, long mtimeMs, CancellationToken cancellationToken)
    {
        await SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "seed_read_state",
            ["path"] = path,
            ["mtime"] = mtimeMs
        }, cancellationToken);
    }

    public async Task<ReadFileResult?> ReadFileAsync(
        string path, int? maxBytes, ReadFileEncoding? encoding, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?> { ["subtype"] = "read_file", ["path"] = path };
        if (maxBytes is { } mb)
            request["max_bytes"] = mb;
        if (encoding is { } enc)
            request["encoding"] = enc.ToWire();
        try
        {
            var r = await SendAsync(request, cancellationToken);
            return new ReadFileResult(
                OptString(r, "contents") ?? "",
                OptString(r, "absPath") ?? "",
                OptBool(r, "truncated"),
                OptString(r, "encoding"));
        }
        catch (ClaudeSDKException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // TS: null on permission denial, missing file or transport error.
            _logger.LogDebug(ex, "read_file failed; returning null");
            return null;
        }
    }

    public async Task<ReloadPluginsResult> ReloadPluginsAsync(bool holdOnCacheImpact, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?> { ["subtype"] = "reload_plugins" };
        if (holdOnCacheImpact)
            request["hold_on_cache_impact"] = true;
        var r = await SendAsync(request, cancellationToken);

        PluginCacheImpact? impact = null;
        if (r.TryGetProperty("cache_impact", out var ci) && ci.ValueKind == JsonValueKind.Object)
        {
            impact = new PluginCacheImpact(
                ci.TryGetProperty("mcp_servers_added", out var a) && a.ValueKind == JsonValueKind.Array ? Strings(a) : [],
                ci.TryGetProperty("mcp_servers_removed", out var rm) && rm.ValueKind == JsonValueKind.Array ? Strings(rm) : [],
                OptString(ci, "lsp_tool_change"));
        }

        var info = ParseInfoLists(r);
        return new ReloadPluginsResult
        {
            Commands = info.Commands,
            Agents = info.Agents,
            Plugins = Objects(r, "plugins")
                .Select(p => new ReloadedPlugin(
                    OptString(p, "name") ?? "",
                    OptString(p, "path") ?? "",
                    OptString(p, "source"),
                    OptString(p, "version")))
                .ToList(),
            McpServers = Objects(r, "mcpServers").ToList(),
            ErrorCount = OptInt(r, "error_count") ?? 0,
            Held = OptBool(r, "held"),
            CacheImpact = impact,
            Raw = r
        };
    }

    public async Task<IReadOnlyList<SlashCommand>> ReloadSkillsAsync(CancellationToken cancellationToken)
    {
        var r = await SendAsync(new Dictionary<string, object?> { ["subtype"] = "reload_skills" }, cancellationToken);
        return r.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array
            ? ParseCommands(skills)
            : [];
    }

    public async Task<IReadOnlyList<string>> ReloadOutputStylesAsync(CancellationToken cancellationToken)
    {
        var r = await SendAsync(new Dictionary<string, object?> { ["subtype"] = "reload_output_styles" }, cancellationToken);
        return r.TryGetProperty("available_output_styles", out var s) && s.ValueKind == JsonValueKind.Array
            ? Strings(s)
            : [];
    }

    public async Task<McpReadResourceResult> ReadMcpResourceAsync(
        string serverName, string uri, CancellationToken cancellationToken)
    {
        var r = await SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "mcp_read_resource",
            ["serverName"] = serverName,
            ["uri"] = uri
        }, cancellationToken);
        return new McpReadResourceResult(Objects(r, "contents")
            .Select(c => new McpReadResourceContent(
                OptString(c, "uri") ?? "",
                OptString(c, "mimeType"),
                OptString(c, "text"),
                OptString(c, "blob"),
                c.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object ? meta.Clone() : null))
            .ToList());
    }

    /// <summary>
    /// Replace the dynamically added MCP servers. In-process SDK servers are
    /// registered / unregistered locally and sent as <c>{"type":"sdk","name","timeout"?}</c>;
    /// an already-registered server keeps its bridge (TS ignores a timeout change
    /// until the server is removed and re-added).
    /// </summary>
    public async Task<McpSetServersResult> SetMcpServersAsync(
        IReadOnlyDictionary<string, object> servers, CancellationToken cancellationToken)
    {
        var sdkServers = new Dictionary<string, McpSdkServerConfig>();
        var external = new Dictionary<string, object?>();
        foreach (var (name, config) in servers)
        {
            if (config is McpSdkServerConfig sdk)
                sdkServers[name] = sdk;
            else
                external[name] = config;
        }

        foreach (var name in _sdkMcpBridges.Keys.ToList())
        {
            if (!sdkServers.ContainsKey(name) && _sdkMcpBridges.TryRemove(name, out var removed))
                await removed.DisposeAsync();
        }

        foreach (var (name, cfg) in sdkServers)
        {
            if (_sdkMcpBridges.TryGetValue(name, out var existing))
            {
                if (_sdkMcpTimeouts.TryGetValue(name, out var oldTimeout) && oldTimeout != cfg.Timeout)
                    _logger.LogWarning("MCP server '{Name}' is already registered; its timeout change is ignored until the server is removed and re-added", name);
                continue;
            }
            var bridge = new SdkMcpBridge(cfg.Handlers, name);
            await bridge.StartAsync(cancellationToken);
            _sdkMcpBridges[name] = bridge;
            _sdkMcpTimeouts[name] = cfg.Timeout;
        }

        var payload = new Dictionary<string, object?>(external);
        foreach (var (name, cfg) in sdkServers)
        {
            var entry = new Dictionary<string, object?> { ["type"] = "sdk", ["name"] = name };
            var timeout = _sdkMcpTimeouts.TryGetValue(name, out var t) ? t : cfg.Timeout;
            if (timeout is { } ms)
                entry["timeout"] = ms;
            payload[name] = entry;
        }

        var r = await SendAsync(new Dictionary<string, object?>
        {
            ["subtype"] = "mcp_set_servers",
            ["servers"] = payload
        }, cancellationToken);

        var errors = new Dictionary<string, string>();
        if (r.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in errs.EnumerateObject())
                errors[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        }
        return new McpSetServersResult(
            r.TryGetProperty("added", out var added) && added.ValueKind == JsonValueKind.Array ? Strings(added) : [],
            r.TryGetProperty("removed", out var rem) && rem.ValueKind == JsonValueKind.Array ? Strings(rem) : [],
            errors);
    }

    // Timeouts of bridges registered through SetMcpServersAsync / at connect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> _sdkMcpTimeouts = new();

    internal void RecordSdkMcpTimeout(string serverName, int? timeout) => _sdkMcpTimeouts[serverName] = timeout;

    public Task<JsonElement> GetUsageAsync(bool skipBehaviors, CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?> { ["subtype"] = "get_usage" };
        if (skipBehaviors)
            request["skip_behaviors"] = true;
        return SendAsync(request, cancellationToken);
    }

    #endregion

    #region JSON helpers

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        array.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToList();

    private static int? OptInt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static IEnumerable<JsonElement> Objects(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => x.Clone())
            : [];

    /// <summary>
    /// Parse a <c>SlashCommand[]</c> array through <see cref="InitializeResponse.Parse"/>
    /// so the info types' own parsing stays the single source of truth.
    /// </summary>
    internal static IReadOnlyList<SlashCommand> ParseCommands(JsonElement commands) =>
        InitializeResponse.Parse(SdkJson.SerializeToElement(new Dictionary<string, object?> { ["commands"] = commands })).Commands;

    private static InitializeResponse ParseInfoLists(JsonElement response)
    {
        var d = new Dictionary<string, object?>();
        if (response.TryGetProperty("commands", out var c)) d["commands"] = c;
        if (response.TryGetProperty("agents", out var a)) d["agents"] = a;
        return InitializeResponse.Parse(SdkJson.SerializeToElement(d));
    }

    #endregion
}
