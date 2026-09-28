// Claude Agent SDK for .NET
// Port of claude-agent-sdk-python/client.py

using System.Runtime.CompilerServices;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Mcp;
using Claude.AgentSdk.Sessions;
using Claude.AgentSdk.Transport;

namespace Claude.AgentSdk;

/// <summary>
/// Client for bidirectional, interactive conversations with Claude Code.
/// </summary>
/// <remarks>
/// <para>
/// This client provides full control over the conversation flow with support
/// for streaming, interrupts, and dynamic message sending. For simple one-shot
/// queries, consider using the <see cref="Claude.QueryAsync(string, ClaudeAgentOptions?, ITransport?, CancellationToken)"/> method instead.
/// </para>
///
/// <para><b>Key features:</b></para>
/// <list type="bullet">
///   <item><description><b>Bidirectional:</b> Send and receive messages at any time</description></item>
///   <item><description><b>Stateful:</b> Maintains conversation context across messages</description></item>
///   <item><description><b>Interactive:</b> Send follow-ups based on responses</description></item>
///   <item><description><b>Control flow:</b> Support for interrupts and session management</description></item>
/// </list>
///
/// <para><b>When to use ClaudeSDKClient:</b></para>
/// <list type="bullet">
///   <item><description>Building chat interfaces or conversational UIs</description></item>
///   <item><description>Interactive debugging or exploration sessions</description></item>
///   <item><description>Multi-turn conversations with context</description></item>
///   <item><description>When you need to react to Claude's responses</description></item>
///   <item><description>Real-time applications with user input</description></item>
///   <item><description>When you need interrupt capabilities</description></item>
/// </list>
///
/// <para><b>When to use QueryAsync() instead:</b></para>
/// <list type="bullet">
///   <item><description>Simple one-off questions</description></item>
///   <item><description>Batch processing of prompts</description></item>
///   <item><description>Fire-and-forget automation scripts</description></item>
///   <item><description>When all inputs are known upfront</description></item>
///   <item><description>Stateless operations</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// await using var client = new ClaudeSDKClient();
/// await client.ConnectAsync();
///
/// await client.QueryAsync("What is 2+2?");
///
/// await foreach (var message in client.ReceiveResponseAsync())
/// {
///     if (message is AssistantMessage am)
///     {
///         foreach (var block in am.Content)
///         {
///             if (block is TextBlock tb)
///                 Console.WriteLine(tb.Text);
///         }
///     }
/// }
/// </code>
/// </example>
public partial class ClaudeSDKClient : IAsyncDisposable
{
    private readonly ClaudeAgentOptions _options;
    private readonly ITransport? _customTransport;
    private ITransport? _transport;
    private QueryHandler? _queryHandler;
    private Task? _inputTask;
    private MaterializedResume? _materialized;

    /// <summary>
    /// Initialize Claude SDK client.
    /// </summary>
    /// <param name="options">Configuration options.</param>
    /// <param name="transport">Optional custom transport implementation.</param>
    public ClaudeSDKClient(ClaudeAgentOptions? options = null, ITransport? transport = null)
    {
        _options = options ?? new ClaudeAgentOptions();
        _customTransport = transport;
    }

    /// <summary>
    /// Connect to Claude with an optional prompt or message stream.
    /// </summary>
    /// <param name="prompt">
    /// Optional initial prompt. Can be a string or null for interactive mode.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ConnectAsync(
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        await ConnectInternalAsync(prompt, promptStream: null, cancellationToken);
    }

    /// <summary>
    /// Connect to Claude with an input message stream.
    /// </summary>
    /// <remarks>
    /// Passing a stream will start streaming it immediately after initialization.
    /// If the stream completes, stdin will be closed and the session may no longer accept new input.
    /// </remarks>
    public async Task ConnectAsync(
        IAsyncEnumerable<Dictionary<string, object?>> promptStream,
        CancellationToken cancellationToken = default)
    {
        await ConnectInternalAsync(prompt: null, promptStream, cancellationToken);
    }

    private async Task ConnectInternalAsync(
        string? prompt,
        IAsyncEnumerable<Dictionary<string, object?>>? promptStream,
        CancellationToken cancellationToken)
    {
        // Fail fast on invalid SessionStore option combinations before spawning.
        SessionStoreValidation.Validate(_options);
        TsOptionsValidation.Validate(_options);

        // resume/continue + SessionStore: materialize the stored session into a
        // temp CLAUDE_CONFIG_DIR (skipped for a custom transport), honoring
        // LoadTimeoutMs (Python: load_timeout_ms).
        _materialized = _customTransport == null
            ? await SessionStoreSupport.MaterializeAsync(_options, cancellationToken)
            : null;

        try
        {
            await ConnectCoreAsync(cancellationToken);
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }

        // If we have an initial prompt stream, start streaming it after initialization.
        if (promptStream != null)
        {
            _inputTask = Task.Run(
                () => _queryHandler!.StreamInputAsync(promptStream, cancellationToken),
                cancellationToken
            );
        }
        else if (prompt != null)
        {
            // Back-compat: if a string prompt was provided, send it as the first user message.
            await QueryAsync(prompt, cancellationToken: cancellationToken);
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        // Validate and configure permission settings (Python:
        // _configure_can_use_tool): rejects CanUseTool + PermissionPromptToolName,
        // warns when the callback is shadowed, routes prompts over stdio.
        var options = CanUseToolConfiguration.Configure(_options);
        if (_materialized != null)
            options = SessionStoreSupport.ApplyMaterialized(options, _materialized);

        // ClaudeSDKClient always uses streaming mode.
        _transport = _customTransport ?? new SubprocessTransport(CreateEmptyStream(), options);

        await _transport.ConnectAsync(cancellationToken);

        _queryHandler = new QueryHandler(_transport, options, SessionStoreSupport.InitializeTimeout());
        SessionStoreSupport.AttachMirrorBatcher(_queryHandler, options, _materialized);

        // Initialize SDK MCP servers (in-process) BEFORE starting query handler
        // This ensures bridges are ready when the CLI sends MCP messages
        await InitializeSdkMcpServersAsync(cancellationToken);

        await _queryHandler.StartAsync(cancellationToken);
        await _queryHandler.InitializeAsync(cancellationToken);
    }

    private async Task InitializeSdkMcpServersAsync(CancellationToken cancellationToken)
    {
        if (_options.McpServers == null || _queryHandler == null)
            return;

        foreach (var (name, sdkConfig) in _options.McpServers.SdkServers())
        {
            var bridge = new SdkMcpBridge(sdkConfig.Handlers, name);
            await bridge.StartAsync(cancellationToken);
            _queryHandler.RegisterSdkMcpBridge(name, bridge);
            _queryHandler.RecordSdkMcpTimeout(name, sdkConfig.Timeout);
        }
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> CreateEmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Receive all messages from Claude.
    /// </summary>
    public async IAsyncEnumerable<Message> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        await foreach (var message in _queryHandler.ReceiveMessagesAsync(cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Receive messages from Claude until and including a ResultMessage.
    /// </summary>
    /// <remarks>
    /// This async iterator yields all messages in sequence and automatically terminates
    /// after yielding a ResultMessage (which indicates the response is complete).
    /// It's a convenience method over ReceiveMessagesAsync() for single-response workflows.
    /// </remarks>
    public async IAsyncEnumerable<Message> ReceiveResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in ReceiveMessagesAsync(cancellationToken))
        {
            yield return message;
            if (message is ResultMessage)
                yield break;
        }
    }

    /// <summary>
    /// Send a new request in streaming mode.
    /// </summary>
    /// <param name="prompt">The message to send to Claude.</param>
    /// <param name="sessionId">Session identifier for the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task QueryAsync(
        string prompt,
        string sessionId = "default",
        CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null || _transport == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        var message = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = prompt },
            ["parent_tool_use_id"] = null,
            ["session_id"] = sessionId
        };

        await _transport.WriteAsync(
            SdkJson.Serialize(QueryHandler.StampUserMessage(message, _options.VerbatimPrompts)) + "\n",
            cancellationToken);
    }

    /// <summary>
    /// Send a stream of user messages in streaming mode. Each message missing a
    /// <c>session_id</c> gets <paramref name="sessionId"/>; with
    /// <see cref="ClaudeAgentOptions.VerbatimPrompts"/> every message is stamped
    /// <c>client_composed</c>. The caller's dictionaries are not mutated.
    /// Python: <c>ClaudeSDKClient.query(AsyncIterable)</c>.
    /// </summary>
    public async Task QueryAsync(
        IAsyncEnumerable<Dictionary<string, object?>> prompt,
        string sessionId = "default",
        CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null || _transport == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        await foreach (var msg in prompt.WithCancellation(cancellationToken))
        {
            var message = msg;
            if (!message.ContainsKey("session_id"))
                message = new Dictionary<string, object?>(message) { ["session_id"] = sessionId };
            await _transport.WriteAsync(
                SdkJson.Serialize(QueryHandler.StampUserMessage(message, _options.VerbatimPrompts)) + "\n",
                cancellationToken);
        }
    }

    /// <summary>
    /// Send interrupt signal.
    /// </summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        await _queryHandler.InterruptAsync(cancellationToken);
    }

    /// <summary>
    /// Change permission mode during conversation.
    /// </summary>
    /// <param name="mode">
    /// The permission mode to set:
    /// <list type="bullet">
    ///   <item><description>'default': CLI prompts for dangerous tools</description></item>
    ///   <item><description>'acceptEdits': Auto-accept file edits</description></item>
    ///   <item><description>'bypassPermissions': Allow all tools (use with caution)</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        await _queryHandler.SetPermissionModeAsync(mode, cancellationToken);
    }

    /// <summary>
    /// Change permission mode during conversation (typed overload; Python's
    /// <c>set_permission_mode</c> takes the <c>PermissionMode</c> literal).
    /// </summary>
    public Task SetPermissionModeAsync(PermissionMode mode, CancellationToken cancellationToken = default) =>
        SetPermissionModeAsync(SubprocessTransport.PermissionModeToCliValue(mode), cancellationToken);

    /// <summary>
    /// Change the AI model during conversation.
    /// </summary>
    /// <param name="model">The model to use, or null to use default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetModelAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        await _queryHandler.SetModelAsync(model, cancellationToken);
    }

    /// <summary>
    /// Rewind tracked files to their state at a specific user message.
    /// </summary>
    /// <param name="userMessageId">UUID of the user message to rewind to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Requires <see cref="ClaudeAgentOptions.EnableFileCheckpointing"/> to be true.
    /// </remarks>
    public async Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        await _queryHandler.RewindFilesAsync(userMessageId, cancellationToken);
    }

    /// <summary>
    /// Get the current MCP server status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The MCP status as a JSON element.</returns>
    public async Task<JsonElement> GetMcpStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        return await _queryHandler.GetMcpStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Get a typed MCP server status. Python commit 28f9b4b.
    /// </summary>
    public async Task<McpStatusResponse> ListMcpServersAsync(CancellationToken cancellationToken = default)
    {
        var raw = await GetMcpStatusAsync(cancellationToken);
        return raw.Deserialize(SdkJsonContext.Default.McpStatusResponse)
            ?? new McpStatusResponse { McpServers = Array.Empty<McpServerStatus>() };
    }

    /// <summary>
    /// Reconnect a disconnected or failed MCP server. Python commit 28f9b4b.
    /// </summary>
    public async Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");
        await _queryHandler.ReconnectMcpServerAsync(serverName, cancellationToken);
    }

    /// <summary>
    /// Enable or disable an MCP server. Python commit 28f9b4b.
    /// </summary>
    public async Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");
        await _queryHandler.ToggleMcpServerAsync(serverName, enabled, cancellationToken);
    }

    /// <summary>
    /// Stop a running task by its task ID (from task_notification events).
    /// Python commit 28f9b4b.
    /// </summary>
    public async Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");
        await _queryHandler.StopTaskAsync(taskId, cancellationToken);
    }

    /// <summary>
    /// Get a breakdown of current context window usage. Python commit ac900bd.
    /// </summary>
    public async Task<ContextUsageResponse> GetContextUsageAsync(CancellationToken cancellationToken = default)
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");
        var raw = await _queryHandler.GetContextUsageAsync(cancellationToken);
        return raw.Deserialize(SdkJsonContext.Default.ContextUsageResponse)
            ?? throw new ClaudeSDKException("Empty context usage response");
    }

    /// <summary>
    /// Get server initialization info including available commands and output styles.
    /// </summary>
    /// <returns>Dictionary with server info, or null if not in streaming mode.</returns>
    public JsonElement? GetServerInfo()
    {
        if (_queryHandler == null)
            throw new CliConnectionException("Not connected. Call ConnectAsync() first.");

        return _queryHandler.GetInitializationResult();
    }

    /// <summary>
    /// Disconnect from Claude.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_inputTask != null)
        {
            try { await _inputTask; }
            catch { }
            _inputTask = null;
        }

        if (_queryHandler != null)
        {
            await _queryHandler.CloseAsync();
            _queryHandler = null;
        }
        else if (_transport != null)
        {
            // Connect failed before the query handler existed.
            try { await _transport.CloseAsync(); } catch { }
        }
        _transport = null;

        await CleanupMaterializedAsync();
    }

    private async Task CleanupMaterializedAsync()
    {
        // The temp dir holds a credentials copy; remove it once the CLI is gone.
        var materialized = Interlocked.Exchange(ref _materialized, null);
        if (materialized != null)
        {
            try { await materialized.CleanupAsync(CancellationToken.None); } catch { }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_inputTask != null)
        {
            try { await _inputTask; }
            catch { }
            _inputTask = null;
        }

        if (_queryHandler != null)
            await _queryHandler.DisposeAsync();
        if (_transport != null)
            await _transport.DisposeAsync();

        await CleanupMaterializedAsync();
    }
}
