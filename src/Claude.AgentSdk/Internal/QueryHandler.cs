// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/_internal/query.py, extended with the
// TypeScript SDK's control-protocol behaviour (sdk.mjs Query class).

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.Internal;

/// <summary>
/// Handles bidirectional control protocol on top of Transport.
/// Manages control request/response routing, hook callbacks, tool permission callbacks,
/// message streaming, and initialization handshake.
/// </summary>
internal partial class QueryHandler : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ClaudeAgentOptions _options;
    private readonly TimeSpan _initializeTimeout;
    private readonly Channel<JsonElement> _messageChannel;
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new();
    private readonly Dictionary<string, HookCallback> _hookCallbacks = new();
    private readonly ConcurrentDictionary<string, SdkMcpBridge> _sdkMcpBridges = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private bool _initialized;
    private volatile bool _closed;
    private int _closeState;
    private int _requestCounter;
    private int _nextCallbackId;
    private JsonElement? _initializationResult;

    // The initialize request as first built (hooks registered once), re-sent
    // verbatim by ReinitializeAsync (TS: initHooksPayload is built once).
    private Dictionary<string, object?>? _initializeRequest;

    // TS: latestCommands, refreshed from system/commands_changed frames.
    private JsonElement? _latestCommands;

    // Python commit 9aafd84: suppress redundant ProcessError after error result.
    // When the CLI emits a result with is_error=true and then exits non-zero,
    // the trailing ProcessError carries no information beyond "exit code N".
    // Replace it with the structured error the CLI already reported.
    // Python keeps the whole result payload (self._last_error_result) so the
    // replacement can be a typed ResultError rather than just a message.
    private JsonElement? _lastErrorResult;

    // Python commit 2c29362: inflight server-initiated control requests so we
    // can cancel them on control_cancel_request. TS also uses this map to skip
    // a duplicate delivery of an in-flight request_id.
    private readonly Dictionary<string, CancellationTokenSource> _inflightRequests = new();

    /// <summary>Mirror callback invoked when the CLI emits transcript_mirror frames.</summary>
    /// <remarks>Takes precedence over the batcher set via <see cref="SetTranscriptMirrorBatcher"/>.</remarks>
    public Action<JsonElement>? TranscriptMirrorHandler { get; set; }

    // SessionStore mirroring (Python: set_transcript_mirror_batcher).
    private TranscriptMirrorBatcher? _transcriptMirrorBatcher;

    // Python #1088: delegated agent tasks still running. A result frame ends one
    // turn, not the run; stdin must stay open while these may still send
    // hook / SDK-MCP control requests.
    private readonly HashSet<string> _inflightTasks = new();
    private static readonly HashSet<string> DeferringTaskTypes = ["local_agent", "local_workflow"];
    private static readonly HashSet<string> TerminalTaskStatuses = ["completed", "failed", "stopped", "killed"];

    private readonly ILogger _logger;

    private sealed record PendingRequest(TaskCompletionSource<JsonElement> Tcs, string? Subtype);

    public QueryHandler(
        ITransport transport,
        ClaudeAgentOptions options,
        TimeSpan? initializeTimeout = null)
    {
        _transport = transport;
        _options = options;
        _logger = options.Logger ?? NullLogger.Instance;
        _initializeTimeout = initializeTimeout ?? TimeSpan.FromSeconds(60);
        _runEndCeilingMs = RunEndCeilingMs(options.Env);
        _messageChannel = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Start reading messages from transport.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = ReadMessagesLoopAsync(_readCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attach a batcher that receives <c>transcript_mirror</c> frames and is
    /// flushed before each result frame and on shutdown.
    /// </summary>
    public void SetTranscriptMirrorBatcher(TranscriptMirrorBatcher batcher) => _transcriptMirrorBatcher = batcher;

    /// <summary>
    /// Surface a <see cref="ISessionStore.AppendAsync"/> failure as a
    /// <c>system/mirror_error</c> message. Non-blocking: dropped if the buffer is full.
    /// </summary>
    public void ReportMirrorError(SessionKey? key, string error)
    {
        var msg = new Dictionary<string, object?>
        {
            ["type"] = "system",
            ["subtype"] = "mirror_error",
            ["error"] = error,
            ["key"] = key,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["session_id"] = key?.SessionId ?? ""
        };
        if (!_messageChannel.Writer.TryWrite(SdkJson.SerializeToElement(msg)))
            _logger.LogWarning("Dropping mirror_error message (message buffer full): {Error}", error);
    }

    /// <summary>
    /// Initialize control protocol if in streaming mode.
    /// </summary>
    public async Task<JsonElement?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return _initializationResult;

        _initializeRequest ??= BuildInitializeRequest();
        var response = await SendControlRequestAsync(_initializeRequest, _initializeTimeout, cancellationToken);
        _initialized = true;
        _initializationResult = response;
        WarnIfPluginsNotApplied(response);
        return response;
    }

    /// <summary>
    /// Re-send the initialize request (same hook callback ids) and return the
    /// fresh response. The CLI's answer can carry <c>pending_permission_requests</c>
    /// / <c>pending_user_dialog_requests</c>, which are redelivered to the
    /// callbacks (deduplicated by request id). TS: <c>Query.reinitialize</c>.
    /// </summary>
    public async Task<JsonElement> ReinitializeAsync(CancellationToken cancellationToken = default)
    {
        _initializeRequest ??= BuildInitializeRequest();
        var response = await SendControlRequestAsync(_initializeRequest, _initializeTimeout, cancellationToken);
        WarnIfPluginsNotApplied(response);
        return response;
    }

    private void WarnIfPluginsNotApplied(JsonElement response)
    {
        if (_options.PluginDelivery != PluginDelivery.Initialize || _options.Plugins.Count == 0)
            return;
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("plugins_applied", out var applied) &&
            applied.ValueKind == JsonValueKind.True)
            return;
        _logger.LogWarning(
            "Claude Code did not report plugins_applied=true for {Count} plugins sent with PluginDelivery.Initialize; " +
            "the process is running with the plugins it was launched with.", _options.Plugins.Count);
    }

    private Dictionary<string, object?> BuildInitializeRequest()
    {
        // Build hooks configuration for initialization
        var hooksConfig = new Dictionary<string, List<Dictionary<string, object?>>>();

        if (_options.Hooks != null)
        {
            foreach (var (hookEvent, matchers) in _options.Hooks)
            {
                var eventName = hookEvent.ToString();
                hooksConfig[eventName] = [];

                foreach (var matcher in matchers)
                {
                    var callbackIds = new List<string>();
                    if (matcher.Hooks != null)
                    {
                        foreach (var callback in matcher.Hooks)
                        {
                            var callbackId = $"hook_{_nextCallbackId++}";
                            _hookCallbacks[callbackId] = callback;
                            callbackIds.Add(callbackId);
                        }
                    }

                    var hookMatcherConfig = new Dictionary<string, object?>
                    {
                        ["matcher"] = matcher.Matcher,
                        ["hookCallbackIds"] = callbackIds
                    };

                    if (matcher.Timeout.HasValue)
                        hookMatcherConfig["timeout"] = matcher.Timeout.Value;

                    hooksConfig[eventName].Add(hookMatcherConfig);
                }
            }
        }

        var request = new Dictionary<string, object?>
        {
            ["subtype"] = "initialize",
            ["hooks"] = hooksConfig.Count > 0 ? hooksConfig : null
        };
        // Python commit 7c6902b: agents travel in the initialize request, not on argv.
        if (_options.Agents is { Count: > 0 })
            request["agents"] = BuildAgentsPayload(_options.Agents);
        if (_options.SystemPrompt is SystemPromptPreset { ExcludeDynamicSections: { } eds })
            request["excludeDynamicSections"] = eds;
        // Python reads `snapshot` only from the preset and custom forms.
        if (_options.SystemPrompt?.SnapshotForInitialize is { } snapshot)
            request["systemPromptSnapshot"] = snapshot;
        // 'all' and omitted are equivalent at the wire level (no filter), so only
        // send the field when it's an explicit list.
        var skills = SubprocessTransport.NormalizeSkills(_options.Skills);
        if (skills != null)
            request["skills"] = skills;
        if (_options.ForwardSubagentText)
            request["forwardSubagentText"] = true;

        AddTsInitializeFields(request);
        return request;
    }

    /// <summary>
    /// TS-only initialize keys (Query.buildInitializeRequest). Each is sent only
    /// when set, so CLIs that predate a key never see it.
    /// </summary>
    private void AddTsInitializeFields(Dictionary<string, object?> request)
    {
        var o = _options;

        // The block-list system prompt has no argv form (TS sends every system
        // prompt this way; the string forms stay on argv for Python parity).
        if (o.SystemPrompt is SystemPromptBlocks blocks)
            request["systemPrompt"] = blocks.Blocks;

        if (SubprocessTransport.JsonSchemaOf(o.OutputFormat) is { } schema)
            request["jsonSchema"] = schema;

        if (o.Title != null) request["title"] = o.Title;
        if (o.PlanModeInstructions != null) request["planModeInstructions"] = o.PlanModeInstructions;
        if (o.AppendSubagentSystemPrompt != null) request["appendSubagentSystemPrompt"] = o.AppendSubagentSystemPrompt;
        if (o.ToolAliases != null) request["toolAliases"] = o.ToolAliases;
        if (o.WebSearchIsolationExemptMcpServers != null)
            request["webSearchIsolationExemptMcpServers"] = o.WebSearchIsolationExemptMcpServers;
        if (o.PromptSuggestions is { } ps) request["promptSuggestions"] = ps;
        if (o.AgentProgressSummaries is { } aps) request["agentProgressSummaries"] = aps;
        if (o.SupportedDialogKinds != null) request["supportedDialogKinds"] = o.SupportedDialogKinds;
        if (o.PerTaskStopAffordance is { } pts) request["perTaskStopAffordance"] = pts;
        if (o.RapidFollowupPreempt is { } rfp) request["rapidFollowupPreempt"] = rfp;

        if (ValidatedWorkspaceTrust(o.WorkspaceTrust, _logger) is { } trust)
        {
            var t = new Dictionary<string, object?>();
            if (trust.Accepted is { } accepted) t["accepted"] = accepted;
            if (trust.Directory != null) t["directory"] = trust.Directory;
            request["workspaceTrust"] = t;
        }

        // SDK server timeouts (TS sdkMcpServerConfigs). The servers themselves
        // stay in --mcp-config (Python parity).
        if (o.McpServers is McpServersConfig.ServerMap map)
        {
            var configs = new Dictionary<string, object?>();
            foreach (var (name, cfg) in map.Servers)
            {
                if (cfg is McpSdkServerConfig { Timeout: { } timeout })
                    configs[name] = new Dictionary<string, object?> { ["timeout"] = timeout };
            }
            if (configs.Count > 0)
                request["sdkMcpServerConfigs"] = configs;
        }

        if (o.PluginDelivery == PluginDelivery.Initialize && o.Plugins.Count > 0)
            request["plugins"] = o.Plugins.Select(PluginPayload).ToList();
    }

    internal static Dictionary<string, object?> PluginPayload(SdkPluginConfig plugin)
    {
        if (plugin.Type != "local")
            throw new ArgumentException($"Unsupported plugin type: {plugin.Type}");
        var d = new Dictionary<string, object?> { ["type"] = plugin.Type, ["path"] = plugin.Path };
        if (plugin.SkipMcpDiscovery is { } skip) d["skipMcpDiscovery"] = skip;
        return d;
    }

    /// <summary>
    /// TS <c>yW</c>: an accepted trust needs an absolute directory (it names a
    /// path where the CLI runs); otherwise the option is dropped with a warning.
    /// </summary>
    internal static WorkspaceTrust? ValidatedWorkspaceTrust(WorkspaceTrust? trust, ILogger logger)
    {
        if (trust is null)
            return null;
        if (trust.Accepted == true && (trust.Directory is null || !IsAbsolutePathAnyPlatform(trust.Directory)))
        {
            logger.LogWarning("WorkspaceTrust ignored: Accepted requires an absolute Directory, as it exists where Claude Code runs");
            return null;
        }
        return trust;
    }

    // TS checks both path.posix and path.win32.
    private static bool IsAbsolutePathAnyPlatform(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));

    /// <summary>
    /// Serialize agent definitions the way Python does (<c>asdict</c> minus
    /// <c>None</c> values), with enums in their CLI string form.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, object?>> BuildAgentsPayload(
        IReadOnlyDictionary<string, AgentDefinition> agents)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (name, def) in agents)
        {
            var d = new Dictionary<string, object?>
            {
                ["description"] = def.Description,
                ["prompt"] = def.Prompt
            };
            if (def.Tools != null) d["tools"] = def.Tools;
            if (def.DisallowedTools != null) d["disallowedTools"] = def.DisallowedTools;
            if (def.Model != null) d["model"] = def.Model;
            if (def.Skills != null) d["skills"] = def.Skills;
            if (def.Memory != null) d["memory"] = def.Memory;
            if (def.McpServers != null) d["mcpServers"] = def.McpServers.Select(s => s.ToWire()).ToList();
            if (def.InitialPrompt != null) d["initialPrompt"] = def.InitialPrompt;
            if (def.MaxTurns != null) d["maxTurns"] = def.MaxTurns;
            if (def.Background != null) d["background"] = def.Background;
            if (def.Effort != null)
                d["effort"] = def.Effort.ToWire();
            if (def.PermissionMode != null)
                d["permissionMode"] = SubprocessTransport.PermissionModeToCliValue(def.PermissionMode.Value);
            // TS-only AgentDefinition keys.
            if (def.CriticalSystemReminderExperimental != null)
                d["criticalSystemReminder_EXPERIMENTAL"] = def.CriticalSystemReminderExperimental;
            if (def.OmitClaudeMd != null) d["omitClaudeMd"] = def.OmitClaudeMd;
            if (def.Observer != null) d["observer"] = def.Observer;
            if (def.ObserverMessage != null) d["observerMessage"] = def.ObserverMessage;
            result[name] = d;
        }
        return result;
    }

    /// <summary>
    /// Get initialization result.
    /// </summary>
    public JsonElement? GetInitializationResult() => _initializationResult;

    /// <summary>
    /// The latest command list from a <c>system/commands_changed</c> frame, or
    /// null when none has arrived (TS: <c>latestCommands</c>).
    /// </summary>
    public JsonElement? GetLatestCommands() => _latestCommands;

    private async Task ReadMessagesLoopAsync(CancellationToken cancellationToken)
    {
        Exception? finalException = null;
        try
        {
            await foreach (var message in _transport.ReadMessagesAsync(cancellationToken))
            {
                if (_closed)
                    break;

                // Raw frame tap (.NET addition): every frame, before routing.
                if (_options.OnRawMessage is { } tap)
                {
                    try { tap(message); }
                    catch (Exception ex) { _logger.LogWarning(ex, "OnRawMessage callback threw; continuing"); }
                }

                if (!message.TryGetProperty("type", out var typeElement))
                    continue;

                var msgType = typeElement.GetString();

                // Route control messages
                if (msgType == "control_response")
                {
                    await HandleControlResponseAsync(message);
                    continue;
                }

                if (msgType == "control_request")
                {
                    await DispatchInboundControlRequestAsync(message);
                    continue;
                }

                if (msgType == "control_cancel_request")
                {
                    // Python commit 2c29362: cancel the matching inflight request.
                    var cancelId = message.TryGetProperty("request_id", out var cidElem)
                        ? cidElem.GetString()
                        : null;
                    if (cancelId != null)
                    {
                        _logger.LogDebug("CLI cancelled control request {RequestId}", cancelId);
                        await _lock.WaitAsync(CancellationToken.None);
                        try
                        {
                            if (_inflightRequests.Remove(cancelId, out var cts))
                            {
                                try { cts.Cancel(); } catch { }
                            }
                        }
                        finally
                        {
                            _lock.Release();
                        }
                    }
                    continue;
                }

                // TS drops keep_alive frames before they reach the stream.
                if (msgType == "keep_alive")
                    continue;

                if (msgType == "transcript_mirror")
                {
                    // Python commit 6e3d54f: peel mirror frames off stdout and
                    // hand to the SessionStore batcher; do NOT yield to consumers.
                    if (TranscriptMirrorHandler != null)
                        TranscriptMirrorHandler(message);
                    else if (_transcriptMirrorBatcher != null)
                        EnqueueTranscriptMirror(message);
                    continue;
                }

                var subtype = message.TryGetProperty("subtype", out var sst) && sst.ValueKind == JsonValueKind.String
                    ? sst.GetString()
                    : null;

                // Track task lifecycle frames so results can tell "one turn
                // ended" apart from "the run is done" (Python #1088).
                if (msgType == "system")
                {
                    bool hadTasksInFlight;
                    bool hasTasksInFlight;
                    lock (_inflightTasks) hadTasksInFlight = _inflightTasks.Count > 0;
                    TrackTaskLifecycle(message);
                    lock (_inflightTasks) hasTasksInFlight = _inflightTasks.Count > 0;
                    if (hadTasksInFlight && !hasTasksInFlight)
                    {
                        // The ceiling left the last tracked agent alone; the
                        // wait between turns starts over now that it settled.
                        lock (_runLock) RearmRunEndCeilingBetweenTurns();
                    }

                    if (subtype == "commands_changed" &&
                        message.TryGetProperty("commands", out var cmds) &&
                        cmds.ValueKind == JsonValueKind.Array)
                        _latestCommands = cmds.Clone();

                    if (subtype == "session_state_changed")
                    {
                        var state = message.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
                            ? st.GetString()
                            : null;
                        lock (_runLock) OnSessionState(state);
                        // Frames the CLI sent only because the transport asked
                        // for them (CLAUDE_CODE_SDK_READS_SESSION_STATE); the
                        // caller did not opt in.
                        if (message.TryGetProperty("sdk_host_only", out var hostOnly) &&
                            hostOnly.ValueKind == JsonValueKind.True)
                            continue;
                    }
                }

                // Track results for proper stream closure
                if (msgType == "result")
                {
                    // Flush pending transcript mirror entries before yielding the
                    // result so consumers can rely on the store being up to date.
                    if (_transcriptMirrorBatcher != null)
                        await _transcriptMirrorBatcher.FlushAsync(CancellationToken.None);

                    lock (_runLock)
                    {
                        _resultReceived = true;
                        _turnInProgress = false;
                        // A result ends a turn, not necessarily the run: a CLI
                        // that reports session state stays "running" while a
                        // follow-up turn is owed, so wait for "idle". Without
                        // state events the result is all there is to go on.
                        if (_sessionState is null or "idle" || !HasBidirectionalNeeds())
                            MaybeEndRun();
                        else if (_sessionState != "requires_action")
                            ArmRunEndCeiling();
                    }

                    // Python commit 9aafd84: remember the error text from the
                    // result, then suppress the trailing ProcessError below.
                    _lastErrorResult =
                        message.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True
                            ? message.Clone()
                            : null;
                }
                else if (!(msgType == "system" && subtype == "session_state_changed"))
                {
                    // Anything other than the post-turn session_state_changed marker
                    // means the conversation moved on; reset the suppression marker.
                    _lastErrorResult = null;

                    // A main-thread turn is under way, so the ceiling stops and
                    // the run reopens even if the ceiling ended it.
                    if (msgType is "assistant" or "stream_event" &&
                        (!message.TryGetProperty("parent_tool_use_id", out var ptu) ||
                         ptu.ValueKind == JsonValueKind.Null))
                    {
                        lock (_runLock)
                        {
                            _turnInProgress = true;
                            ReopenRun();
                            ClearRunEndCeiling();
                        }
                    }
                }

                // Regular SDK messages go to the stream
                await _messageChannel.Writer.WriteAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            // When the CLI emits a result with is_error=true it then exits
            // non-zero on purpose; the trailing ProcessException carries nothing
            // beyond "exit code 1". Replace it with a ResultException carrying the
            // result the CLI already reported (Python: Query._read_messages,
            // ResultError). ResultException subclasses ProcessException.
            Exception finalEx = ex;
            if (ex is ProcessException pex && ex is not ResultException && _lastErrorResult is { } lastError)
            {
                finalEx = ResultException.FromResultFrame(lastError, pex.ExitCode, pex);
            }
            finalException = finalEx;

            // Signal all pending control requests
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                foreach (var (_, pending) in _pendingRequests)
                {
                    pending.Tcs.TrySetException(finalEx);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            // Flush remaining transcript mirror entries so an early EOF or
            // transport error doesn't drop entries batched this turn.
            if (_transcriptMirrorBatcher != null)
            {
                try { await _transcriptMirrorBatcher.FlushAsync(CancellationToken.None); } catch { }
            }
            // Unblock any waiters (e.g. string-prompt path waiting for the end
            // of the run) so they don't stall on early exit.
            lock (_runLock)
            {
                _runFinal = true;
                EndRun();
            }
            // Python commit 9aafd84 (port): propagate the fatal exception through
            // the message channel so ReceiveMessagesAsync re-throws it for the
            // consumer instead of silently completing.
            if (finalException != null)
                _messageChannel.Writer.TryComplete(finalException);
            else
                _messageChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Start handling an inbound control request, unless a request with the same
    /// id is still in flight (TS: duplicate deliveries are skipped, which matters
    /// when <c>reinitialize</c> redelivers pending permission prompts).
    /// </summary>
    private async Task DispatchInboundControlRequestAsync(JsonElement message)
    {
        var reqId = message.TryGetProperty("request_id", out var ridElem) && ridElem.ValueKind == JsonValueKind.String
            ? ridElem.GetString()
            : null;
        if (reqId == null || _closed)
            return;

        var cts = new CancellationTokenSource();
        bool added;
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            added = _inflightRequests.TryAdd(reqId, cts);
        }
        finally
        {
            _lock.Release();
        }

        if (!added)
        {
            _logger.LogDebug("Duplicate delivery of in-flight control request {RequestId}; skipping", reqId);
            cts.Dispose();
            return;
        }

        _ = HandleControlRequestAsync(message, reqId, cts);
    }

    private void EnqueueTranscriptMirror(JsonElement message)
    {
        try
        {
            var filePath = message.GetProperty("filePath").GetString()!;
            var entries = message.GetProperty("entries").Deserialize(SdkJsonContext.Default.ListSessionStoreEntry)
                ?? [];
            _transcriptMirrorBatcher!.Enqueue(filePath, entries);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            ReportMirrorError(null, $"Malformed transcript_mirror frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Track in-flight delegated agent tasks from system lifecycle frames.
    /// Python: <c>_track_task_lifecycle</c>. Only <c>local_agent</c> /
    /// <c>local_workflow</c> tasks are tracked: background shells may never
    /// reach a terminal status and would hold stdin open forever.
    /// </summary>
    private void TrackTaskLifecycle(JsonElement message)
    {
        if (!message.TryGetProperty("task_id", out var tid) || tid.ValueKind != JsonValueKind.String)
            return;
        var taskId = tid.GetString();
        if (string.IsNullOrEmpty(taskId))
            return;
        var subtype = message.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;

        lock (_inflightTasks)
        {
            switch (subtype)
            {
                case "task_started":
                    if (message.TryGetProperty("task_type", out var tt) &&
                        tt.ValueKind == JsonValueKind.String &&
                        DeferringTaskTypes.Contains(tt.GetString()!))
                        _inflightTasks.Add(taskId);
                    break;
                case "task_notification":
                    _inflightTasks.Remove(taskId);
                    break;
                case "task_updated":
                    if (message.TryGetProperty("patch", out var patch) &&
                        patch.ValueKind == JsonValueKind.Object &&
                        patch.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String &&
                        TerminalTaskStatuses.Contains(status.GetString()!))
                        _inflightTasks.Remove(taskId);
                    break;
            }
        }
    }

    private async Task HandleControlRequestAsync(JsonElement message, string requestId, CancellationTokenSource cts)
    {
        try
        {
            await HandleControlRequestInnerAsync(message, cts.Token);
        }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                // Only remove our own entry (a cancel may already have removed it).
                if (_inflightRequests.TryGetValue(requestId, out var current) && ReferenceEquals(current, cts))
                    _inflightRequests.Remove(requestId);
            }
            finally
            {
                _lock.Release();
            }
            cts.Dispose();
        }
    }

    private async Task HandleControlResponseAsync(JsonElement message)
    {
        if (!message.TryGetProperty("response", out var response))
            return;

        if (!response.TryGetProperty("request_id", out var requestIdElement))
            return;

        var requestId = requestIdElement.GetString();
        if (requestId == null)
            return;

        string? subtype = null;
        var matched = false;
        var success = false;
        await _lock.WaitAsync();
        try
        {
            if (_pendingRequests.TryGetValue(requestId, out var pending))
            {
                matched = true;
                subtype = pending.Subtype;
                if (response.TryGetProperty("subtype", out var subtypeElement) &&
                    subtypeElement.GetString() == "error")
                {
                    var errorMsg = response.TryGetProperty("error", out var e)
                        ? e.GetString() ?? "Unknown error"
                        : "Unknown error";
                    pending.Tcs.TrySetException(new ClaudeSDKException(errorMsg));
                }
                else
                {
                    success = true;
                    // Python returns the inner `response` payload (or {}), not
                    // the {subtype, request_id, response} envelope.
                    var payload = response.TryGetProperty("response", out var inner) &&
                                  inner.ValueKind == JsonValueKind.Object
                        ? inner.Clone()
                        : SdkJson.EmptyObject();
                    pending.Tcs.TrySetResult(payload);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        if (!matched || !success)
            return;

        // TS: prompt-redelivery fields are honored only on initialize responses.
        var hasPermissions = response.TryGetProperty("pending_permission_requests", out var perms) &&
                             perms.ValueKind == JsonValueKind.Array;
        var hasDialogs = response.TryGetProperty("pending_user_dialog_requests", out var dialogs) &&
                         dialogs.ValueKind == JsonValueKind.Array;
        if (!hasPermissions && !hasDialogs)
            return;
        if (subtype != "initialize")
        {
            _logger.LogDebug("Ignoring prompt-redelivery fields on non-initialize response ({Subtype})", subtype);
            return;
        }

        if (hasPermissions)
            await RedeliverAsync(perms, "can_use_tool");
        if (hasDialogs)
            await RedeliverAsync(dialogs, "request_user_dialog");
    }

    private async Task RedeliverAsync(JsonElement requests, string subtype)
    {
        foreach (var req in requests.EnumerateArray())
        {
            if (req.ValueKind == JsonValueKind.Object &&
                req.TryGetProperty("request", out var body) &&
                body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("subtype", out var st) &&
                st.ValueKind == JsonValueKind.String &&
                st.GetString() == subtype)
            {
                await DispatchInboundControlRequestAsync(req.Clone());
            }
        }
    }

    /// <summary>Returned by an inbound handler to write no response at all.</summary>
    private static readonly object SuppressResponse = new();

    // TS DG: requests for the machine serving this session's tools; another
    // host answers them, so this SDK stays silent.
    private static readonly HashSet<string> SilentSubtypes =
        ["remote_tool_call", "remote_plumbing_call", "remote_tools_probe", "remote_tools_reannounce"];

    private async Task HandleControlRequestInnerAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("request_id", out var requestIdElement) ||
            !message.TryGetProperty("request", out var request))
            return;

        var requestId = requestIdElement.GetString()!;
        var subtype = request.TryGetProperty("subtype", out var stElem) ? stElem.GetString() : null;
        _logger.LogDebug("Control request {Subtype} ({RequestId}) from CLI", subtype, requestId);

        if (subtype != null && SilentSubtypes.Contains(subtype))
        {
            _logger.LogDebug("{Subtype} {RequestId} is for the machine serving this session's tools; leaving it unanswered", subtype, requestId);
            return;
        }

        try
        {
            object? responseData = null;
            var context = new ControlRequestContext(requestId);

            switch (subtype)
            {
                case "can_use_tool":
                    responseData = await HandleCanUseToolAsync(request, requestId, cancellationToken);
                    break;

                case "hook_callback":
                    responseData = await HandleHookCallbackAsync(request, cancellationToken);
                    break;

                case "mcp_message":
                    responseData = await HandleMcpMessageAsync(request, cancellationToken);
                    break;

                case "elicitation":
                    responseData = await HandleElicitationAsync(request, context, cancellationToken);
                    break;

                case "request_user_dialog":
                    responseData = await HandleUserDialogAsync(request, context, cancellationToken);
                    break;

                case "oauth_token_refresh":
                    responseData = await HandleOAuthTokenRefreshAsync(cancellationToken);
                    break;

                case "host_auth_token_refresh":
                    responseData = await HandleHostAuthTokenRefreshAsync(cancellationToken);
                    break;

                default:
                    throw new ClaudeSDKException($"Unsupported control request subtype: {subtype}");
            }

            if (ReferenceEquals(responseData, SuppressResponse))
                return;

            // Send success response
            var successResponse = new Dictionary<string, object?>
            {
                ["type"] = "control_response",
                ["response"] = new Dictionary<string, object?>
                {
                    ["subtype"] = "success",
                    ["request_id"] = requestId,
                    ["response"] = responseData
                }
            };

            // Write with a token that outlives the request: cancelling mid-write
            // would mark the transport broken for every later write.
            await _transport.WriteAsync(SdkJson.Serialize(successResponse) + "\n", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Request was cancelled via control_cancel_request; the CLI has
            // already abandoned it, so don't write a response (Python parity).
        }
        catch (Exception ex)
        {
            // Callback failures fail closed: the CLI gets an error response.
            _logger.LogWarning(ex, "Control request {Subtype} ({RequestId}) failed; replying with an error", subtype, requestId);

            // Send error response
            var errorResponse = new Dictionary<string, object?>
            {
                ["type"] = "control_response",
                ["response"] = new Dictionary<string, object?>
                {
                    ["subtype"] = "error",
                    ["request_id"] = requestId,
                    ["error"] = ex.Message
                }
            };

            try
            {
                await _transport.WriteAsync(SdkJson.Serialize(errorResponse) + "\n", CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                // Fire-and-forget task: don't leave an unobserved exception behind.
                _logger.LogWarning(writeEx, "Failed to write control error response for {Subtype} request {RequestId}", subtype, requestId);
            }
        }
    }

    private async Task<object> HandleCanUseToolAsync(JsonElement request, string requestId, CancellationToken cancellationToken)
    {
        if (_options.CanUseTool == null)
            throw new ClaudeSDKException("canUseTool callback is not provided");

        var toolName = request.GetProperty("tool_name").GetString()!;
        var input = request.GetProperty("input");
        var suggestions = request.TryGetProperty("permission_suggestions", out var s) &&
                          s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray()
                .Select(PermissionUpdate.FromControlProtocol)
                .OfType<PermissionUpdate>()
                .ToList()
            : new List<PermissionUpdate>();

        var toolUseId = OptString(request, "tool_use_id");
        var context = new ToolPermissionContext(
            Signal: null,
            Suggestions: suggestions,
            ToolUseId: toolUseId,
            AgentId: OptString(request, "agent_id"),
            BlockedPath: OptString(request, "blocked_path"),
            DecisionReason: OptString(request, "decision_reason"),
            Title: OptString(request, "title"),
            DisplayName: OptString(request, "display_name"),
            Description: OptString(request, "description"))
        {
            RequestId = requestId,
            McpServer = request.TryGetProperty("mcp_server", out var ms) && ms.ValueKind == JsonValueKind.Object
                ? new McpServerProvenance(OptString(ms, "name"), OptString(ms, "source"))
                : null,
            DefaultToNo = OptBool(request, "default_to_no"),
            SuppressAlwaysAllowRule = OptBool(request, "suppress_always_allow_rule"),
            MatchedAskRule = request.TryGetProperty("matched_ask_rule", out var mar) && mar.ValueKind == JsonValueKind.Object
                ? new MatchedAskRule(OptString(mar, "source"), OptString(mar, "tool_name"), OptString(mar, "rule_content"))
                : null,
            RequiresUserInteraction = OptBool(request, "requires_user_interaction"),
            DecisionReasonType = OptString(request, "decision_reason_type"),
            ClassifierApprovable = OptBool(request, "classifier_approvable"),
            ServerPrompt = OptString(request, "server_prompt"),
            ComputerFolder = request.TryGetProperty("computer_folder", out var cf) &&
                             cf.ValueKind == JsonValueKind.Object &&
                             OptString(cf, "path") is { } cfPath &&
                             OptString(cf, "computer_name") is { } cfName
                ? new ComputerFolder(cfPath, cfName)
                : null
        };
        var result = await _options.CanUseTool(toolName, input, context, cancellationToken);

        // TS: a null result means the host answered out-of-band; write nothing.
        if (result is null)
            return SuppressResponse;

        if (result is PermissionResultAllow allow)
        {
            var response = new Dictionary<string, object?>
            {
                ["behavior"] = "allow",
                ["updatedInput"] = allow.UpdatedInput.HasValue
                    ? allow.UpdatedInput.Value.Clone()
                    : input.Clone()
            };

            if (allow.UpdatedPermissions != null)
            {
                response["updatedPermissions"] = allow.UpdatedPermissions
                    .Select(p => p.ToDictionary())
                    .ToList();
            }

            if (allow.DecisionClassification is { } dc)
                response["decisionClassification"] = dc.ToWire();
            // TS spreads the result then sets toolUseID from the request.
            if ((toolUseId ?? allow.ToolUseId) is { } echo)
                response["toolUseID"] = echo;

            return response;
        }
        else if (result is PermissionResultDeny deny)
        {
            var response = new Dictionary<string, object?>
            {
                ["behavior"] = "deny",
                ["message"] = deny.Message
            };

            if (deny.Interrupt)
                response["interrupt"] = true;
            if (deny.DecisionClassification is { } dc)
                response["decisionClassification"] = dc.ToWire();
            if ((toolUseId ?? deny.ToolUseId) is { } echo)
                response["toolUseID"] = echo;

            return response;
        }

        throw new ClaudeSDKException($"Invalid permission result type: {result.GetType().Name}");
    }

    private static string? OptString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? OptBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private async Task<object> HandleHookCallbackAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var callbackId = request.GetProperty("callback_id").GetString()!;

        if (!_hookCallbacks.TryGetValue(callbackId, out var callback))
            throw new ClaudeSDKException($"No hook callback found for ID: {callbackId}");

        var input = request.TryGetProperty("input", out var i) ? i : default;
        var toolUseId = request.TryGetProperty("tool_use_id", out var t) ? t.GetString() : null;
        var context = new HookContext(null);

        var output = await callback(input, toolUseId, context, cancellationToken);

        // Convert to dictionary, converting C# property names to CLI expected names
        var result = new Dictionary<string, object?>();

        if (output.Continue.HasValue)
            result["continue"] = output.Continue.Value;
        if (output.SuppressOutput.HasValue)
            result["suppressOutput"] = output.SuppressOutput.Value;
        if (output.StopReason != null)
            result["stopReason"] = output.StopReason;
        if (output.Decision != null)
            result["decision"] = output.Decision;
        if (output.SystemMessage != null)
            result["systemMessage"] = output.SystemMessage;
        if (output.Reason != null)
            result["reason"] = output.Reason;
        if (output.HookSpecificOutput.HasValue)
            result["hookSpecificOutput"] = WithoutNullMembers(output.HookSpecificOutput.Value);
        if (output.Async.HasValue)
            result["async"] = output.Async.Value;
        if (output.AsyncTimeout.HasValue)
            result["asyncTimeout"] = output.AsyncTimeout.Value;

        return result;
    }

    /// <summary>
    /// Drop <c>null</c>-valued top-level members of a hook-specific output.
    /// Python sends TypedDicts that only contain the keys that were set; the
    /// CLI treats an explicit null (e.g. <c>"updatedInput": null</c>) as
    /// invalid and ignores the whole decision, so a permission deny would not
    /// be enforced. Unset and null mean the same thing here.
    /// </summary>
    internal static JsonElement WithoutNullMembers(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Object)
            return output.Clone();
        var members = new Dictionary<string, object?>();
        foreach (var prop in output.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Null)
                members[prop.Name] = prop.Value;
        }
        return SdkJson.SerializeToElement(members);
    }

    private async Task<object> HandleMcpMessageAsync(JsonElement request, CancellationToken cancellationToken)
    {
        // Python: _handle_control_request, mcp_message branch.
        var serverName = request.TryGetProperty("server_name", out var sn) && sn.ValueKind == JsonValueKind.String
            ? sn.GetString()
            : null;
        var hasMessage = request.TryGetProperty("message", out var message) &&
                         message.ValueKind == JsonValueKind.Object &&
                         message.EnumerateObject().Any();
        if (string.IsNullOrEmpty(serverName) || !hasMessage)
            throw new ClaudeSDKException("Missing server_name or message for MCP request");

        // JSON-RPC notifications get no reply, but the control request that
        // carried one still expects an ack.
        var mcpResponse = await HandleSdkMcpRequestAsync(serverName, message, cancellationToken)
                          ?? SdkJson.ParseElement("""{"jsonrpc":"2.0","result":{}}""");
        return new Dictionary<string, object?> { ["mcp_response"] = mcpResponse };
    }

    /// <summary>
    /// Route a JSON-RPC message to the named SDK MCP server. Returns the
    /// JSON-RPC response for requests, or null for notifications and
    /// responses (which expect no reply). A message that cannot be delivered
    /// is answered with a JSON-RPC error. Python: <c>_handle_sdk_mcp_request</c>.
    /// </summary>
    private async Task<JsonElement?> HandleSdkMcpRequestAsync(
        string serverName, JsonElement message, CancellationToken cancellationToken)
    {
        object? MessageId() => message.TryGetProperty("id", out var id) ? id.Clone() : null;

        if (!_sdkMcpBridges.TryGetValue(serverName, out var bridge))
        {
            return SdkJson.SerializeToElement(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = MessageId(),
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = -32601,
                    ["message"] = $"Server '{serverName}' not found"
                }
            });
        }

        try
        {
            return await bridge.HandleAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The control request was cancelled; no response is written.
            throw;
        }
        catch (Exception ex)
        {
            return SdkJson.SerializeToElement(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = MessageId(),
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = -32603,
                    // Python: str(e) or type(e).__name__
                    ["message"] = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message
                }
            });
        }
    }

    /// <summary>
    /// Register an SDK MCP server bridge.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    /// <param name="bridge">The bridge instance.</param>
    internal void RegisterSdkMcpBridge(string serverName, SdkMcpBridge bridge)
    {
        _sdkMcpBridges[serverName] = bridge;
    }

    /// <summary>
    /// Send an SDK-initiated control request and return the inner response
    /// payload.
    /// </summary>
    /// <remarks>
    /// <para>Cancelling <paramref name="cancellationToken"/> before the request is
    /// written sends nothing; cancelling after it was written also sends
    /// <c>{"type":"control_cancel_request","request_id":...}</c> so the CLI can
    /// abandon it (TS parity). Both throw <see cref="OperationCanceledException"/>.</para>
    /// <para>Only a real timeout raises <c>ClaudeSDKException("Control request timeout: ...")</c>.</para>
    /// </remarks>
    internal async Task<JsonElement> SendControlRequestAsync(
        Dictionary<string, object?> request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var subtype = request.GetValueOrDefault("subtype") as string;
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = $"req_{Interlocked.Increment(ref _requestCounter)}_{Guid.NewGuid():N}";
        _logger.LogDebug("Sending control request {Subtype} ({RequestId})", subtype, requestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _pendingRequests[requestId] = new PendingRequest(tcs, subtype);
        }
        finally
        {
            _lock.Release();
        }

        var written = false;
        try
        {
            var controlRequest = new Dictionary<string, object?>
            {
                ["type"] = "control_request",
                ["request_id"] = requestId,
                ["request"] = request
            };

            cancellationToken.ThrowIfCancellationRequested();
            // Never cancel mid-write: a torn line would break the transport for
            // every later write. Cancellation is honored before and after.
            await _transport.WriteAsync(SdkJson.Serialize(controlRequest) + "\n", CancellationToken.None);
            written = true;

            return await tcs.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new ClaudeSDKException($"Control request timeout: {subtype}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (written)
            {
                try
                {
                    var cancel = new Dictionary<string, object?>
                    {
                        ["type"] = "control_cancel_request",
                        ["request_id"] = requestId
                    };
                    await _transport.WriteAsync(SdkJson.Serialize(cancel) + "\n", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write control_cancel_request for {RequestId}", requestId);
                }
            }
            throw;
        }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                _pendingRequests.Remove(requestId);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// Write an SDK-originated MCP message (a notification such as
    /// <c>notifications/tools/list_changed</c>, or a request) from an in-process
    /// server to the CLI: <c>control_request/mcp_message</c> with a fresh id.
    /// Fire-and-forget like TS <c>sendMcpServerMessageToCli</c>: the CLI's
    /// acknowledgement is not awaited.
    /// </summary>
    internal async Task SendMcpServerMessageAsync(string serverName, JsonElement message, CancellationToken cancellationToken)
    {
        var frame = new Dictionary<string, object?>
        {
            ["type"] = "control_request",
            ["request_id"] = Guid.NewGuid().ToString(),
            ["request"] = new Dictionary<string, object?>
            {
                ["subtype"] = "mcp_message",
                ["server_name"] = serverName,
                ["message"] = message
            }
        };
        cancellationToken.ThrowIfCancellationRequested();
        await _transport.WriteAsync(SdkJson.Serialize(frame) + "\n", CancellationToken.None);
    }

    private static readonly TimeSpan DefaultControlTimeout = TimeSpan.FromSeconds(60);

    private Task<JsonElement> SendAsync(Dictionary<string, object?> request, CancellationToken cancellationToken) =>
        SendControlRequestAsync(request, DefaultControlTimeout, cancellationToken);

    /// <summary>
    /// Send interrupt control request.
    /// </summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(new Dictionary<string, object?> { ["subtype"] = "interrupt" }, cancellationToken);
    }

    /// <summary>
    /// Change permission mode.
    /// </summary>
    public async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new Dictionary<string, object?> { ["subtype"] = "set_permission_mode", ["mode"] = mode },
            cancellationToken);
    }

    /// <summary>
    /// Change the AI model.
    /// </summary>
    public async Task SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new Dictionary<string, object?> { ["subtype"] = "set_model", ["model"] = model },
            cancellationToken);
    }

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new Dictionary<string, object?> { ["subtype"] = "rewind_files", ["user_message_id"] = userMessageId },
            cancellationToken);
    }

    /// <summary>
    /// Get the current MCP server status.
    /// </summary>
    public Task<JsonElement> GetMcpStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new Dictionary<string, object?> { ["subtype"] = "mcp_status" }, cancellationToken);

    /// <summary>
    /// Get a breakdown of current context window usage. Python commit ac900bd.
    /// </summary>
    public Task<JsonElement> GetContextUsageAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new Dictionary<string, object?> { ["subtype"] = "get_context_usage" }, cancellationToken);

    /// <summary>Reconnect a disconnected or failed MCP server. Python commit 28f9b4b.</summary>
    public async Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new Dictionary<string, object?> { ["subtype"] = "mcp_reconnect", ["serverName"] = serverName },
            cancellationToken);
    }

    /// <summary>Enable or disable an MCP server. Python commit 28f9b4b.</summary>
    public async Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new Dictionary<string, object?> { ["subtype"] = "mcp_toggle", ["serverName"] = serverName, ["enabled"] = enabled },
            cancellationToken);
    }

    /// <summary>Stop a running task. Python commit 28f9b4b.</summary>
    public async Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new Dictionary<string, object?> { ["subtype"] = "stop_task", ["task_id"] = taskId },
            cancellationToken);
    }

    /// <summary>
    /// Whether the CLI may still send control requests that need a reply
    /// (SDK MCP servers, hooks, can_use_tool, and the TS callbacks: elicitation,
    /// user dialogs, OAuth / host-auth refresh). Closing stdin while any are
    /// configured makes later requests fail CLI-side with "Stream closed".
    /// </summary>
    private bool HasBidirectionalNeeds() =>
        !_sdkMcpBridges.IsEmpty ||
        (_options.Hooks != null && _options.Hooks.Count > 0) ||
        _options.CanUseTool != null ||
        _options.OnElicitation != null ||
        _options.OnUserDialog != null ||
        _options.GetOAuthToken != null ||
        _options.GetHostAuthToken != null;

    /// <summary>
    /// Wait for the end of the run (if bidirectional needs exist) then close
    /// stdin. The run ends at the CLI's <c>idle</c> session state after a
    /// result or, from a CLI that reports no session state, at the first result
    /// with no tracked tasks in flight; the between-turns wait is bounded by
    /// <c>CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS</c>. Python:
    /// <c>wait_for_result_and_end_input</c>.
    /// </summary>
    public async Task WaitForResultAndEndInputAsync(CancellationToken cancellationToken = default)
    {
        if (HasBidirectionalNeeds())
        {
            Task runEnded;
            lock (_runLock) runEnded = _runEnded.Task;
            try { await runEnded.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        lock (_runLock)
        {
            _runFinal = true;
            ClearRunEndCeiling();
        }
        await _transport.EndInputAsync(CancellationToken.None);
    }

    /// <summary>
    /// Apply <see cref="ClaudeAgentOptions.VerbatimPrompts"/> to an outgoing
    /// user message: returns <paramref name="message"/> unchanged when off,
    /// otherwise a copy with <c>client_composed = true</c> (overwriting any
    /// caller-supplied value; the caller's dictionary is never mutated).
    /// Python: <c>stamp_user_message</c>.
    /// </summary>
    internal static Dictionary<string, object?> StampUserMessage(Dictionary<string, object?> message, bool verbatimPrompts)
    {
        if (!verbatimPrompts)
            return message;
        return new Dictionary<string, object?>(message) { ["client_composed"] = true };
    }

    /// <summary>
    /// Stream input messages to transport, then close stdin once the run ends.
    /// Each prompt owes a run of its own, so the wait is for the last prompt's
    /// run, not an earlier one's.
    /// </summary>
    public async Task StreamInputAsync(
        IAsyncEnumerable<Dictionary<string, object?>> stream,
        CancellationToken cancellationToken = default)
    {
        var written = 0;
        try
        {
            await foreach (var message in stream.WithCancellation(cancellationToken))
            {
                if (_closed)
                    break;
                lock (_runLock)
                {
                    ReopenRun();
                    _resultReceived = false;
                    ClearRunEndCeiling();
                }
                await _transport.WriteAsync(
                    SdkJson.Serialize(StampUserMessage(message, _options.VerbatimPrompts)) + "\n",
                    cancellationToken);
                written++;
            }
        }
        catch (Exception ex)
        {
            // A user-supplied prompt stream (or the write) failed. Don't leave
            // stdin open — the CLI would wait for input forever — fall through
            // and close it like a normal end of input (Python parity).
            _logger.LogError(ex, "Prompt stream failed; closing stdin");
        }

        try
        {
            if (written > 0)
            {
                await WaitForResultAndEndInputAsync(cancellationToken);
            }
            else
            {
                // Nothing was sent, so no result will arrive to release the hold.
                lock (_runLock) _runFinal = true;
                await _transport.EndInputAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing input stream");
        }
    }

    /// <summary>
    /// Receive SDK messages (not control messages).
    /// </summary>
    public async IAsyncEnumerable<Message> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var json in _messageChannel.Reader.ReadAllAsync(cancellationToken))
        {
            var msg = MessageParser.ParseOrNull(json);
            if (msg != null)
                yield return msg;
        }
    }

    /// <summary>
    /// Close the query handler.
    /// </summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeState, 1) == 1)
            return;

        _closed = true;
        lock (_runLock) ClearRunEndCeiling();

        var readCts = Interlocked.Exchange(ref _readCts, null);
        if (readCts != null)
        {
            try
            {
                await readCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                readCts.Dispose();
            }
        }

        var readTask = Interlocked.Exchange(ref _readTask, null);
        if (readTask != null)
        {
            try { await readTask; }
            catch { }
        }

        // Python commit 91998d3: close receive stream on disconnect so consumers
        // observing ReceiveMessagesAsync exit cleanly instead of hanging.
        _messageChannel.Writer.TryComplete();

        // Final transcript mirror flush before teardown (never raises).
        if (_transcriptMirrorBatcher != null)
            await _transcriptMirrorBatcher.CloseAsync(CancellationToken.None);

        await _transport.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();

        // Dispose SDK MCP bridges
        foreach (var bridge in _sdkMcpBridges.Values)
        {
            await bridge.DisposeAsync();
        }
        _sdkMcpBridges.Clear();

        _lock.Dispose();
    }

    #region Run lifecycle (Python 0.2.160 session-state handshake, #1088 / #1190)

    // The CLI's own wait for background work once stdin is closed; the SDK
    // bounds its wait for "idle" by the same value.
    internal const string RunEndCeilingEnv = "CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS";
    internal const int DefaultRunEndCeilingMs = 600_000;

    private readonly object _runLock = new();
    private readonly int _runEndCeilingMs;
    // Completed when the run is over, so the stdin-closing waiter can wake.
    // Work the CLI takes up after the run ended swaps in a fresh one (ReopenRun).
    private TaskCompletionSource _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _resultReceived;
    // The CLI's latest session_state_changed state, or null while it sends
    // none (a CLI too old to honor CLAUDE_CODE_SDK_READS_SESSION_STATE).
    private string? _sessionState;
    // A main-thread turn is under way: the ceiling counts only the wait
    // between turns, so it is not armed meanwhile.
    private bool _turnInProgress;
    // Set once stdin is closed or the reader is gone: the run stays ended.
    private bool _runFinal;
    private int _runEndCeilingGeneration;
    private CancellationTokenSource? _runEndCeilingCts;

    /// <summary>
    /// Read <c>CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS</c> as the CLI will see it:
    /// <paramref name="optionsEnv"/> overrides the inherited environment. 0 means
    /// no limit; anything that is not a non-negative integer falls back to 10
    /// minutes. Python: <c>run_end_ceiling_ms</c>.
    /// </summary>
    internal static int RunEndCeilingMs(IReadOnlyDictionary<string, string> optionsEnv)
    {
        var raw = optionsEnv.TryGetValue(RunEndCeilingEnv, out var v)
            ? v
            : Environment.GetEnvironmentVariable(RunEndCeilingEnv);
        if (raw is null)
            return DefaultRunEndCeilingMs;
        if (!long.TryParse(raw.Trim().Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            return DefaultRunEndCeilingMs;
        if (value < 0)
            return DefaultRunEndCeilingMs;
        return (int)Math.Min(value, int.MaxValue);
    }

    // All members below require _runLock.

    private void OnSessionState(string? state)
    {
        _sessionState = state;
        if (state == "idle")
        {
            if (_resultReceived)
                MaybeEndRun();
            return;
        }
        // Work the CLI took up after the run ended (a finished background task
        // woke it) reopens the run until the next "idle".
        ReopenRun();
        if (state == "requires_action")
            ClearRunEndCeiling(); // the host is answering a request; stdin must outlast it
        else
            RearmRunEndCeilingBetweenTurns();
    }

    private void MaybeEndRun()
    {
        bool anyInflight;
        lock (_inflightTasks) anyInflight = _inflightTasks.Count > 0;
        if (anyInflight)
        {
            _logger.LogDebug("Turn ended with task(s) in flight; keeping stdin open");
            return;
        }
        EndRun();
    }

    private void EndRun()
    {
        ClearRunEndCeiling();
        _runEnded.TrySetResult();
    }

    private void ReopenRun()
    {
        if (_runEnded.Task.IsCompleted && !_runFinal)
            _runEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ArmRunEndCeiling()
    {
        ClearRunEndCeiling();
        if (_runEndCeilingMs <= 0 ||
            _runEnded.Task.IsCompleted ||
            _runFinal ||
            _closed ||
            _turnInProgress ||
            !HasBidirectionalNeeds())
            return;

        var generation = _runEndCeilingGeneration;
        var cts = new CancellationTokenSource();
        _runEndCeilingCts = cts;
        _ = EndRunAtCeilingAsync(generation, cts.Token);
    }

    private void RearmRunEndCeilingBetweenTurns()
    {
        if (_resultReceived && _sessionState is not (null or "idle" or "requires_action"))
            ArmRunEndCeiling();
    }

    private async Task EndRunAtCeilingAsync(int generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(_runEndCeilingMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_runLock)
        {
            // Cleared or re-armed while this sleeper was already waking up.
            if (generation != _runEndCeilingGeneration)
                return;
            _runEndCeilingCts = null;
            bool anyInflight;
            lock (_inflightTasks) anyInflight = _inflightTasks.Count > 0;
            if (anyInflight)
            {
                _logger.LogDebug("No 'idle' {Ms}ms after the last result, but tracked task(s) still in flight; keeping stdin open", _runEndCeilingMs);
                return;
            }
            _logger.LogDebug("No 'idle' {Ms}ms after the last result; ending the run", _runEndCeilingMs);
            EndRun();
        }
    }

    private void ClearRunEndCeiling()
    {
        _runEndCeilingGeneration++;
        var cts = _runEndCeilingCts;
        _runEndCeilingCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    #endregion
}
