// Claude Agent SDK for .NET
// TypeScript SDK parity for ClaudeSDKClient: the TS `Query` control methods
// that the Python SDK does not have, typed initialize-result helpers, a generic
// control-request escape hatch, and thin wrappers for a few undocumented
// subtypes. Wire shapes: @anthropic-ai/claude-agent-sdk 0.3.283 sdk.mjs.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Claude.AgentSdk.Internal;

namespace Claude.AgentSdk;

public partial class ClaudeSDKClient
{
    private QueryHandler Handler =>
        _queryHandler ?? throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

    #region Generic escape hatch

    /// <summary>
    /// Send any control request and return the CLI's inner response payload
    /// (<c>{"type":"control_request","request":{"subtype":…, …fields}}</c>).
    /// Covers every subtype without a typed wrapper (e.g. <c>get_hooks_listing</c>,
    /// <c>list_permission_rules</c>, <c>set_cwd</c>, <c>mcp_authenticate</c>, …).
    /// </summary>
    /// <param name="subtype">The control subtype.</param>
    /// <param name="fields">Other request members; null values are written as JSON null.</param>
    /// <param name="timeout">Response timeout (default 60s; <see cref="Timeout.InfiniteTimeSpan"/> waits forever).</param>
    /// <param name="cancellationToken">
    /// Cancels the wait. If the request was already written, a
    /// <c>control_cancel_request</c> is sent and <see cref="OperationCanceledException"/> is thrown.
    /// </param>
    /// <exception cref="ClaudeSDKException">The CLI answered with an error, or the request timed out.</exception>
    public Task<JsonElement> SendControlRequestAsync(
        string subtype,
        IReadOnlyDictionary<string, object?>? fields = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subtype);
        var request = new Dictionary<string, object?> { ["subtype"] = subtype };
        if (fields != null)
        {
            foreach (var (key, value) in fields)
            {
                if (key == "subtype")
                    throw new ArgumentException("fields must not contain 'subtype'; pass it as the subtype argument.", nameof(fields));
                request[key] = value;
            }
        }
        return Handler.SendControlRequestAsync(request, timeout ?? TimeSpan.FromSeconds(60), cancellationToken);
    }

    #endregion

    #region TS Query methods

    /// <summary>
    /// Interrupt and return the CLI's receipt (<c>interrupt_receipt_v1</c>), or null
    /// from a CLI that sends none. With <paramref name="cancelQueued"/>, also
    /// cancels queued async user messages (<c>cancel_queued: true</c>).
    /// </summary>
    public Task<InterruptReceipt?> InterruptAsync(bool cancelQueued, CancellationToken cancellationToken = default) =>
        Handler.InterruptWithReceiptAsync(cancelQueued, cancellationToken);

    /// <summary>
    /// Rewind tracked files and return the result; with <paramref name="dryRun"/>
    /// only preview the change. Requires <see cref="ClaudeAgentOptions.EnableFileCheckpointing"/>.
    /// </summary>
    public Task<RewindFilesResult> RewindFilesAsync(string userMessageId, bool dryRun, CancellationToken cancellationToken = default) =>
        Handler.RewindFilesWithResultAsync(userMessageId, dryRun, cancellationToken);

    /// <summary>
    /// Context window usage; <see cref="ContextUsageDetail.Summary"/> skips the
    /// per-category token-count calls.
    /// </summary>
    public async Task<ContextUsageResponse> GetContextUsageAsync(ContextUsageDetail detail, CancellationToken cancellationToken = default)
    {
        var raw = await Handler.GetContextUsageAsync(detail, cancellationToken);
        return raw.Deserialize(SdkJsonContext.Default.ContextUsageResponse)
            ?? throw new ClaudeSDKException("Empty context usage response");
    }

    /// <summary>
    /// Set (or clear, with null) the thinking-token limit. <paramref name="thinkingDisplay"/>
    /// replaces the display mode; <paramref name="clearThinkingDisplay"/> sends null to
    /// clear the override; neither keeps the current mode. Deprecated in TS in favor of
    /// <see cref="ClaudeAgentOptions.Thinking"/>.
    /// </summary>
    public Task SetMaxThinkingTokensAsync(
        int? maxThinkingTokens,
        ThinkingDisplayMode? thinkingDisplay = null,
        bool clearThinkingDisplay = false,
        CancellationToken cancellationToken = default) =>
        Handler.SetMaxThinkingTokensAsync(maxThinkingTokens, thinkingDisplay, clearThinkingDisplay, cancellationToken);

    /// <summary>
    /// Pin (or clear, with null) a tighten-only permission-mode override for one MCP
    /// server. Returns the CLI's warning (e.g. unknown server name), if any.
    /// </summary>
    public Task<string?> SetMcpPermissionModeOverrideAsync(
        string serverName, McpPermissionModeOverride? mode, CancellationToken cancellationToken = default) =>
        Handler.SetMcpPermissionModeOverrideAsync(serverName, mode, cancellationToken);

    /// <summary>
    /// Merge settings into the flag settings layer (the inline settings of the
    /// session). A null value clears that key.
    /// </summary>
    public Task ApplyFlagSettingsAsync(IReadOnlyDictionary<string, object?> settings, CancellationToken cancellationToken = default) =>
        Handler.ApplyFlagSettingsAsync(settings, cancellationToken);

    /// <summary>
    /// Write allow-listed keys into a settings file through the CLI's own writer
    /// (localSettings: outputStyle; userSettings: effortLevel).
    /// </summary>
    public Task UpdateSettingsAsync(
        SettingsFileSource source, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default) =>
        Handler.UpdateSettingsAsync(source, settings, cancellationToken);

    /// <summary>The typed initialize response from connect.</summary>
    public Task<InitializeResponse> InitializationResultAsync(CancellationToken cancellationToken = default)
    {
        var raw = Handler.GetInitializationResult()
                  ?? throw new CliConnectionException("The initialize handshake has not completed.");
        return Task.FromResult(InitializeResponse.Parse(raw));
    }

    /// <summary>
    /// Re-send initialize (after a transport gap). Pending permission prompts and
    /// user dialogs in the answer are redelivered to the callbacks, deduplicated by
    /// request id; callbacks should be idempotent per request id.
    /// </summary>
    public async Task<InitializeResponse> ReinitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeResponse.Parse(await Handler.ReinitializeAsync(cancellationToken));

    /// <summary>
    /// Available commands and skills: the latest <c>system/commands_changed</c>
    /// list, else the initialize response's.
    /// </summary>
    public async Task<IReadOnlyList<SlashCommand>> SupportedCommandsAsync(CancellationToken cancellationToken = default)
    {
        if (Handler.GetLatestCommands() is { } latest)
            return QueryHandler.ParseCommands(latest);
        return (await InitializationResultAsync(cancellationToken)).Commands;
    }

    /// <summary>Available models (initialize <c>models</c>).</summary>
    public async Task<IReadOnlyList<ModelInfo>> SupportedModelsAsync(CancellationToken cancellationToken = default) =>
        (await InitializationResultAsync(cancellationToken)).Models;

    /// <summary>Available subagents (initialize <c>agents</c>).</summary>
    public async Task<IReadOnlyList<AgentInfo>> SupportedAgentsAsync(CancellationToken cancellationToken = default) =>
        (await InitializationResultAsync(cancellationToken)).Agents;

    /// <summary>The authenticated account (initialize <c>account</c>).</summary>
    public async Task<AccountInfo?> AccountInfoAsync(CancellationToken cancellationToken = default) =>
        (await InitializationResultAsync(cancellationToken)).Account;

    /// <summary>
    /// Read a file through the session (resolved against cwd, gated by Read
    /// permissions). Returns null on denial, a missing file or a CLI error.
    /// </summary>
    public Task<ReadFileResult?> ReadFileAsync(
        string path, int? maxBytes = null, ReadFileEncoding? encoding = null, CancellationToken cancellationToken = default) =>
        Handler.ReadFileAsync(path, maxBytes, encoding, cancellationToken);

    /// <summary>
    /// Reload plugins from disk. With <paramref name="holdOnCacheImpact"/>, a reload
    /// that would change the tool list under a live prompt cache is held
    /// (<see cref="ReloadPluginsResult.Held"/>).
    /// </summary>
    public Task<ReloadPluginsResult> ReloadPluginsAsync(bool holdOnCacheImpact = false, CancellationToken cancellationToken = default) =>
        Handler.ReloadPluginsAsync(holdOnCacheImpact, cancellationToken);

    /// <summary>Reload skills from disk and return them.</summary>
    public Task<IReadOnlyList<SlashCommand>> ReloadSkillsAsync(CancellationToken cancellationToken = default) =>
        Handler.ReloadSkillsAsync(cancellationToken);

    /// <summary>Re-read output styles from disk and return the available style names.</summary>
    public Task<IReadOnlyList<string>> ReloadOutputStylesAsync(CancellationToken cancellationToken = default) =>
        Handler.ReloadOutputStylesAsync(cancellationToken);

    /// <summary>
    /// Seed the CLI's read-state cache so a later Edit does not fail with "file
    /// not read yet". <paramref name="mtimeMs"/> is the file's floored mtime in ms.
    /// </summary>
    public Task SeedReadStateAsync(string path, long mtimeMs, CancellationToken cancellationToken = default) =>
        Handler.SeedReadStateAsync(path, mtimeMs, cancellationToken);

    /// <summary>
    /// Read a <c>ui://</c> MCP Apps resource from a server the CLI connected to.
    /// The contents are untrusted third-party HTML. (TS: @alpha.)
    /// </summary>
    public Task<McpReadResourceResult> ReadMcpResourceAsync(string serverName, string uri, CancellationToken cancellationToken = default) =>
        Handler.ReadMcpResourceAsync(serverName, uri, cancellationToken);

    /// <summary>
    /// Replace the dynamically added MCP servers. Values are
    /// <see cref="McpStdioServerConfig"/>, <see cref="McpSSEServerConfig"/>,
    /// <see cref="McpHttpServerConfig"/>, raw dictionaries, or in-process
    /// <see cref="McpSdkServerConfig"/> servers (served locally by the SDK).
    /// </summary>
    public Task<McpSetServersResult> SetMcpServersAsync(
        IReadOnlyDictionary<string, object> servers, CancellationToken cancellationToken = default) =>
        Handler.SetMcpServersAsync(servers, cancellationToken);

    /// <summary>
    /// Background foreground tasks (all, or the one started by <paramref name="toolUseId"/>).
    /// Returns false only when <paramref name="toolUseId"/> matched nothing.
    /// </summary>
    public Task<bool> BackgroundTasksAsync(string? toolUseId = null, CancellationToken cancellationToken = default) =>
        Handler.BackgroundTasksAsync(toolUseId, cancellationToken);

    /// <summary>
    /// The data behind <c>/usage</c>: session cost and plan rate-limit windows.
    /// <paramref name="skipBehaviors"/> skips the local transcript scan. Unstable in TS.
    /// </summary>
    [Experimental(ExperimentalDiagnostics.UnstableControlApi)]
    public async Task<UsageReport> GetUsageAsync(bool skipBehaviors = false, CancellationToken cancellationToken = default)
    {
        var r = await Handler.GetUsageAsync(skipBehaviors, cancellationToken);
        JsonElement? Opt(string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
        return new UsageReport
        {
            Session = Opt("session"),
            SubscriptionType = r.TryGetProperty("subscription_type", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : null,
            RateLimitsAvailable = r.TryGetProperty("rate_limits_available", out var rla) && rla.ValueKind == JsonValueKind.True,
            RateLimits = Opt("rate_limits"),
            Behaviors = Opt("behaviors"),
            Raw = r
        };
    }

    /// <summary>
    /// Push a JSON-RPC message (notification or request) from an in-process SDK MCP
    /// server to the CLI, e.g. <c>notifications/tools/list_changed</c>. Fire-and-forget:
    /// the CLI's acknowledgement is not awaited.
    /// </summary>
    public Task SendMcpServerMessageAsync(string serverName, JsonElement message, CancellationToken cancellationToken = default) =>
        Handler.SendMcpServerMessageAsync(serverName, message, cancellationToken);

    /// <summary>Tell the CLI an in-process server's tool list changed.</summary>
    public Task NotifyMcpToolsListChangedAsync(string serverName, CancellationToken cancellationToken = default) =>
        SendMcpServerMessageAsync(
            serverName,
            SdkJson.ParseElement("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}"""),
            cancellationToken);

    #endregion

    #region Undocumented TS subtypes (claude.ai / IDE hosts)

    /// <summary>Start a Claude login (<c>claude_authenticate</c>); returns the manual/auto URLs.</summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public Task<JsonElement> ClaudeAuthenticateAsync(bool loginWithClaudeAi, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("claude_authenticate",
            new Dictionary<string, object?> { ["loginWithClaudeAi"] = loginWithClaudeAi },
            cancellationToken: cancellationToken);

    /// <summary>Complete a login with the OAuth callback values (<c>claude_oauth_callback</c>).</summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public Task<JsonElement> ClaudeOAuthCallbackAsync(string authorizationCode, string state, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("claude_oauth_callback",
            new Dictionary<string, object?> { ["authorizationCode"] = authorizationCode, ["state"] = state },
            cancellationToken: cancellationToken);

    /// <summary>
    /// Wait for an in-progress login to finish (<c>claude_oauth_wait_for_completion</c>).
    /// Waits without a timeout by default; cancel with <paramref name="cancellationToken"/>.
    /// </summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public Task<JsonElement> ClaudeOAuthWaitForCompletionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("claude_oauth_wait_for_completion", null, timeout ?? Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>The session's effective settings (<c>get_settings</c>).</summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public Task<JsonElement> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("get_settings", cancellationToken: cancellationToken);

    /// <summary>
    /// Set the running session's title (<c>rename_session</c>, <c>source: "host"</c>).
    /// For stored sessions use <see cref="ClaudeSessions.RenameSession"/>.
    /// </summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public async Task RenameSessionAsync(string title, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?> { ["title"] = title, ["source"] = "host" };
        if (sessionId != null)
            fields["session_id"] = sessionId;
        await SendControlRequestAsync("rename_session", fields, cancellationToken: cancellationToken);
    }

    /// <summary>Generate a title from a description (<c>generate_session_title</c>).</summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public async Task<string?> GenerateSessionTitleAsync(
        string description, bool? persist = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?> { ["description"] = description };
        if (persist is { } p)
            fields["persist"] = p;
        var r = await SendControlRequestAsync("generate_session_title", fields, timeout, cancellationToken);
        return r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    }

    /// <summary>
    /// Withdraw a queued async user message by uuid (<c>cancel_async_message</c>).
    /// Returns whether it was still queued and is now cancelled.
    /// </summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public async Task<bool> CancelAsyncMessageAsync(string messageUuid, CancellationToken cancellationToken = default)
    {
        var r = await SendControlRequestAsync("cancel_async_message",
            new Dictionary<string, object?> { ["message_uuid"] = messageUuid },
            cancellationToken: cancellationToken);
        return r.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Ask a side question outside the main conversation (<c>side_question</c>).
    /// Returns null when the CLI has no answer. Cancelling sends <c>control_cancel_request</c>.
    /// </summary>
    [Experimental(ExperimentalDiagnostics.InternalControlApi)]
    public async Task<SideQuestionResult?> AskSideQuestionAsync(
        string question,
        IReadOnlyList<JsonElement>? history = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?> { ["question"] = question };
        if (history is { Count: > 0 })
            fields["history"] = history.ToList();
        var r = await SendControlRequestAsync("side_question", fields, timeout, cancellationToken);
        if (!r.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.String)
            return null;

        SideQuestionRefusalFallback? fallback = null;
        if (r.TryGetProperty("refusal_fallback", out var rf) && rf.ValueKind == JsonValueKind.Object)
        {
            fallback = new SideQuestionRefusalFallback(
                rf.TryGetProperty("original_model", out var om) && om.ValueKind == JsonValueKind.String ? om.GetString() : null,
                rf.TryGetProperty("fallback_model", out var fm) && fm.ValueKind == JsonValueKind.String ? fm.GetString() : null,
                rf.TryGetProperty("content", out var content) ? content.Clone() : null);
        }
        return new SideQuestionResult(
            resp.GetString()!,
            r.TryGetProperty("synthetic", out var syn) && syn.ValueKind == JsonValueKind.True,
            fallback);
    }

    #endregion
}
