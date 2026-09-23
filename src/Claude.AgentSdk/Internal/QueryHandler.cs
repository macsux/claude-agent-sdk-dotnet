// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/_internal/query.py

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk.Internal;

/// <summary>
/// Handles bidirectional control protocol on top of Transport.
/// Manages control request/response routing, hook callbacks, tool permission callbacks,
/// message streaming, and initialization handshake.
/// </summary>
internal class QueryHandler : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ClaudeAgentOptions _options;
    private readonly TimeSpan _initializeTimeout;
    private readonly Channel<JsonElement> _messageChannel;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly Dictionary<string, HookCallback> _hookCallbacks = new();
    private readonly Dictionary<string, SdkMcpBridge> _sdkMcpBridges = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private bool _initialized;
    private bool _closed;
    private int _closeState;
    private int _requestCounter;
    private int _nextCallbackId;
    private TaskCompletionSource _firstResultEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonElement? _initializationResult;

    // Python commit 9aafd84: suppress redundant ProcessError after error result.
    // When the CLI emits a result with is_error=true and then exits non-zero,
    // the trailing ProcessError carries no information beyond "exit code N".
    // Replace it with the structured error the CLI already reported.
    // Python keeps the whole result payload (self._last_error_result) so the
    // replacement can be a typed ResultError rather than just a message.
    private JsonElement? _lastErrorResult;

    // Python commit 2c29362: inflight server-initiated control requests so we
    // can cancel them on control_cancel_request.
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

    public QueryHandler(
        ITransport transport,
        ClaudeAgentOptions options,
        TimeSpan? initializeTimeout = null)
    {
        _transport = transport;
        _options = options;
        _initializeTimeout = initializeTimeout ?? TimeSpan.FromSeconds(60);
        _messageChannel = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Start reading messages from transport.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = ReadMessagesLoopAsync(_readCts.Token);
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
        if (!_messageChannel.Writer.TryWrite(JsonSerializer.SerializeToElement(msg)))
            System.Diagnostics.Debug.WriteLine($"[QueryHandler] Dropping mirror_error message (buffer full): {error}");
    }

    /// <summary>
    /// Initialize control protocol if in streaming mode.
    /// </summary>
    public async Task<JsonElement?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return _initializationResult;

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

        var response = await SendControlRequestAsync(request, _initializeTimeout, cancellationToken);
        _initialized = true;
        _initializationResult = response;
        return response;
    }

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
            result[name] = d;
        }
        return result;
    }

    /// <summary>
    /// Get initialization result.
    /// </summary>
    public JsonElement? GetInitializationResult() => _initializationResult;

    private async Task ReadMessagesLoopAsync(CancellationToken cancellationToken)
    {
        Exception? finalException = null;
        try
        {
            await foreach (var message in _transport.ReadMessagesAsync(cancellationToken))
            {
                if (_closed)
                    break;

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
                    var reqId = message.TryGetProperty("request_id", out var ridElem)
                        ? ridElem.GetString()
                        : null;
                    if (reqId != null && !_closed)
                    {
                        var cts = new CancellationTokenSource();
                        await _lock.WaitAsync(CancellationToken.None);
                        try
                        {
                            _inflightRequests[reqId] = cts;
                        }
                        finally
                        {
                            _lock.Release();
                        }

                        _ = HandleControlRequestAsync(message, reqId, cts);
                    }
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

                // Track task lifecycle frames so results can tell "one turn
                // ended" apart from "the run is done" (Python #1088).
                if (msgType == "system")
                    TrackTaskLifecycle(message);

                // Track results for proper stream closure
                if (msgType == "result")
                {
                    // Flush pending transcript mirror entries before yielding the
                    // result so consumers can rely on the store being up to date.
                    if (_transcriptMirrorBatcher != null)
                        await _transcriptMirrorBatcher.FlushAsync(CancellationToken.None);

                    // Background tasks still running may need control responses
                    // over stdin; a later result (with none in flight) closes it.
                    bool anyInflight;
                    lock (_inflightTasks) anyInflight = _inflightTasks.Count > 0;
                    if (!anyInflight)
                        _firstResultEvent.TrySetResult();

                    // Python commit 9aafd84: remember the error text from the
                    // result, then suppress the trailing ProcessError below.
                    _lastErrorResult =
                        message.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True
                            ? message.Clone()
                            : null;
                }
                else if (!(msgType == "system" &&
                           message.TryGetProperty("subtype", out var sst) &&
                           sst.ValueKind == JsonValueKind.String &&
                           sst.GetString() == "session_state_changed"))
                {
                    // Anything other than the post-turn session_state_changed marker
                    // means the conversation moved on; reset the suppression marker.
                    _lastErrorResult = null;
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
                foreach (var (_, tcs) in _pendingRequests)
                {
                    tcs.TrySetException(finalEx);
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
            // Unblock any waiters (e.g. string-prompt path waiting for first result)
            // so they don't stall on early exit.
            _firstResultEvent.TrySetResult();
            // Python commit 9aafd84 (port): propagate the fatal exception through
            // the message channel so ReceiveMessagesAsync re-throws it for the
            // consumer instead of silently completing.
            if (finalException != null)
                _messageChannel.Writer.TryComplete(finalException);
            else
                _messageChannel.Writer.TryComplete();
        }
    }

    private void EnqueueTranscriptMirror(JsonElement message)
    {
        try
        {
            var filePath = message.GetProperty("filePath").GetString()!;
            var entries = JsonSerializer.Deserialize<List<SessionStoreEntry>>(message.GetProperty("entries").GetRawText())
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

        await _lock.WaitAsync();
        try
        {
            if (_pendingRequests.TryGetValue(requestId, out var tcs))
            {
                if (response.TryGetProperty("subtype", out var subtypeElement) &&
                    subtypeElement.GetString() == "error")
                {
                    var errorMsg = response.TryGetProperty("error", out var e)
                        ? e.GetString() ?? "Unknown error"
                        : "Unknown error";
                    tcs.TrySetException(new ClaudeSDKException(errorMsg));
                }
                else
                {
                    // Python returns the inner `response` payload (or {}), not
                    // the {subtype, request_id, response} envelope.
                    var payload = response.TryGetProperty("response", out var inner) &&
                                  inner.ValueKind == JsonValueKind.Object
                        ? inner.Clone()
                        : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
                    tcs.TrySetResult(payload);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task HandleControlRequestInnerAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("request_id", out var requestIdElement) ||
            !message.TryGetProperty("request", out var request))
            return;

        var requestId = requestIdElement.GetString()!;
        var subtype = request.GetProperty("subtype").GetString();

        try
        {
            object? responseData = null;

            switch (subtype)
            {
                case "can_use_tool":
                    responseData = await HandleCanUseToolAsync(request, cancellationToken);
                    break;

                case "hook_callback":
                    responseData = await HandleHookCallbackAsync(request, cancellationToken);
                    break;

                case "mcp_message":
                    responseData = await HandleMcpMessageAsync(request, cancellationToken);
                    break;

                default:
                    throw new ClaudeSDKException($"Unsupported control request subtype: {subtype}");
            }

            // Send success response
            var successResponse = new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = requestId,
                    response = responseData
                }
            };

            // Write with a token that outlives the request: cancelling mid-write
            // would mark the transport broken for every later write.
            await _transport.WriteAsync(JsonSerializer.Serialize(successResponse) + "\n", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Request was cancelled via control_cancel_request; the CLI has
            // already abandoned it, so don't write a response (Python parity).
        }
        catch (Exception ex)
        {
            // Send error response
            var errorResponse = new
            {
                type = "control_response",
                response = new
                {
                    subtype = "error",
                    request_id = requestId,
                    error = ex.Message
                }
            };

            try
            {
                await _transport.WriteAsync(JsonSerializer.Serialize(errorResponse) + "\n", CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                // Fire-and-forget task: don't leave an unobserved exception behind.
                System.Diagnostics.Debug.WriteLine($"[QueryHandler] Failed to write control error response: {writeEx.Message}");
            }
        }
    }

    private async Task<object> HandleCanUseToolAsync(JsonElement request, CancellationToken cancellationToken)
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

        string? Opt(string name) =>
            request.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var context = new ToolPermissionContext(
            Signal: null,
            Suggestions: suggestions,
            ToolUseId: Opt("tool_use_id"),
            AgentId: Opt("agent_id"),
            BlockedPath: Opt("blocked_path"),
            DecisionReason: Opt("decision_reason"),
            Title: Opt("title"),
            DisplayName: Opt("display_name"),
            Description: Opt("description"));
        var result = await _options.CanUseTool(toolName, input, context, cancellationToken);

        if (result is PermissionResultAllow allow)
        {
            var response = new Dictionary<string, object?>
            {
                ["behavior"] = "allow",
                ["updatedInput"] = allow.UpdatedInput.HasValue
                    ? JsonSerializer.Deserialize<object>(allow.UpdatedInput.Value.GetRawText())
                    : JsonSerializer.Deserialize<object>(input.GetRawText())
            };

            if (allow.UpdatedPermissions != null)
            {
                response["updatedPermissions"] = allow.UpdatedPermissions
                    .Select(p => p.ToDictionary())
                    .ToList();
            }

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

            return response;
        }

        throw new ClaudeSDKException($"Invalid permission result type: {result.GetType().Name}");
    }

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
            result["hookSpecificOutput"] = JsonSerializer.Deserialize<object>(output.HookSpecificOutput.Value.GetRawText());
        if (output.Async.HasValue)
            result["async"] = output.Async.Value;
        if (output.AsyncTimeout.HasValue)
            result["asyncTimeout"] = output.AsyncTimeout.Value;

        return result;
    }

    private async Task<object> HandleMcpMessageAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var serverName = request.GetProperty("server_name").GetString()!;
        var message = request.GetProperty("message");

        if (!_sdkMcpBridges.TryGetValue(serverName, out var bridge))
        {
            // Return JSONRPC error for unknown server wrapped in mcp_response
            return new Dictionary<string, object?>
            {
                ["mcp_response"] = new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message.TryGetProperty("id", out var id) ? id.Clone() : null,
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["code"] = -32601,
                        ["message"] = $"SDK MCP server '{serverName}' not found"
                    }
                }
            };
        }

        try
        {
            var response = await bridge.SendMessageAsync(message, cancellationToken);
            // Wrap the MCP response as expected by the control protocol
            return new Dictionary<string, object?>
            {
                ["mcp_response"] = JsonSerializer.Deserialize<object>(response.GetRawText())
            };
        }
        catch (Exception ex)
        {
            // Return JSONRPC error wrapped in mcp_response
            return new Dictionary<string, object?>
            {
                ["mcp_response"] = new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message.TryGetProperty("id", out var id) ? id.Clone() : null,
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["code"] = -32603,
                        ["message"] = $"MCP server error: {ex.Message}"
                    }
                }
            };
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

    private async Task<JsonElement> SendControlRequestAsync(
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var requestId = $"req_{Interlocked.Increment(ref _requestCounter)}_{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _pendingRequests[requestId] = tcs;
        }
        finally
        {
            _lock.Release();
        }

        var controlRequest = new
        {
            type = "control_request",
            request_id = requestId,
            request
        };

        await _transport.WriteAsync(JsonSerializer.Serialize(controlRequest) + "\n", cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ClaudeSDKException($"Control request timeout: {request}");
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
    /// Send interrupt control request.
    /// </summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "interrupt" },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Change permission mode.
    /// </summary>
    public async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "set_permission_mode", mode },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Change the AI model.
    /// </summary>
    public async Task SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "set_model", model },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "rewind_files", user_message_id = userMessageId },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Get the current MCP server status.
    /// </summary>
    public async Task<JsonElement> GetMcpStatusAsync(CancellationToken cancellationToken = default)
    {
        return await SendControlRequestAsync(
            new { subtype = "mcp_status" },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Get a breakdown of current context window usage. Python commit ac900bd.
    /// </summary>
    public async Task<JsonElement> GetContextUsageAsync(CancellationToken cancellationToken = default)
    {
        return await SendControlRequestAsync(
            new { subtype = "get_context_usage" },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>Reconnect a disconnected or failed MCP server. Python commit 28f9b4b.</summary>
    public async Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "mcp_reconnect", serverName },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>Enable or disable an MCP server. Python commit 28f9b4b.</summary>
    public async Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "mcp_toggle", serverName, enabled },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>Stop a running task. Python commit 28f9b4b.</summary>
    public async Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(
            new { subtype = "stop_task", task_id = taskId },
            TimeSpan.FromSeconds(60),
            cancellationToken
        );
    }

    /// <summary>
    /// Whether the CLI may still send control requests that need a reply
    /// (SDK MCP servers, hooks, or a can_use_tool callback). Closing stdin while
    /// any are configured makes later requests fail CLI-side with "Stream closed".
    /// </summary>
    private bool HasBidirectionalNeeds() =>
        _sdkMcpBridges.Count > 0 ||
        (_options.Hooks != null && _options.Hooks.Count > 0) ||
        _options.CanUseTool != null;

    /// <summary>
    /// Wait for the run-ending result (if bidirectional needs exist) then close
    /// stdin. No timeout: the control protocol needs stdin for the whole run,
    /// and the read loop always releases the wait on exit. Python:
    /// <c>wait_for_result_and_end_input</c>.
    /// </summary>
    public async Task WaitForResultAndEndInputAsync(CancellationToken cancellationToken = default)
    {
        if (HasBidirectionalNeeds())
        {
            try { await _firstResultEvent.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
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
                await _transport.WriteAsync(
                    JsonSerializer.Serialize(StampUserMessage(message, _options.VerbatimPrompts)) + "\n",
                    cancellationToken);
                written++;
            }
        }
        catch (Exception ex)
        {
            // A user-supplied prompt stream (or the write) failed. Don't leave
            // stdin open — the CLI would wait for input forever — fall through
            // and close it like a normal end of input (Python parity).
            System.Diagnostics.Debug.WriteLine($"[QueryHandler] Prompt stream failed; closing stdin: {ex.Message}");
        }

        try
        {
            if (written > 0)
                await WaitForResultAndEndInputAsync(cancellationToken);
            else
                // Nothing was sent, so no result will arrive to release the hold.
                await _transport.EndInputAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QueryHandler] Error closing input stream: {ex.Message}");
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
}
