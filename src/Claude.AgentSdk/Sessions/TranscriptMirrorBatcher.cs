// Claude Agent SDK for .NET — TranscriptMirrorBatcher.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/_internal/transcript_mirror_batcher.py

using System.Text.Json;
using System.Threading.Channels;

namespace Claude.AgentSdk.Sessions;

/// <summary>
/// Delegate invoked when a batch flush fails after all retries. The
/// <paramref name="key"/> argument may be <c>null</c> if the file path could
/// not be mapped to a <see cref="SessionKey"/>. Never raises.
/// </summary>
public delegate Task MirrorErrorCallback(SessionKey? key, string message, CancellationToken cancellationToken);

/// <summary>
/// Accumulates <c>transcript_mirror</c> frames and flushes them to a
/// <see cref="ISessionStore"/>. <see cref="Enqueue"/> is fire-and-forget;
/// <see cref="FlushAsync"/> is asynchronous. The pending queue is bounded —
/// when it exceeds <see cref="MaxPendingEntries"/> or
/// <see cref="MaxPendingBytes"/>, an eager flush fires in the background.
/// <para>Adapter failures are retried (<see cref="MirrorAppendMaxAttempts"/>
/// attempts) with short backoff; timeouts are not retried. Only after the
/// final attempt fails is the batch dropped and reported via
/// <c>OnError</c>. Failures never raise — the local-disk transcript is
/// already durable so the session must continue unaffected.</para>
/// </summary>
public sealed class TranscriptMirrorBatcher : IAsyncDisposable
{
    /// <summary>Default eager-flush threshold (entry count).</summary>
    public const int DefaultMaxPendingEntries = 500;
    /// <summary>Default eager-flush threshold (bytes).</summary>
    public const int DefaultMaxPendingBytes = 1 << 20;
    /// <summary>Default per-append timeout.</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Total attempts (including the first) for a batch.</summary>
    public const int MirrorAppendMaxAttempts = 3;

    /// <summary>Backoff delays between attempts. Length == MAX_ATTEMPTS - 1.</summary>
    public static readonly TimeSpan[] MirrorAppendBackoff =
    {
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
    };

    private readonly ISessionStore _store;
    private readonly string _projectsDir;
    private readonly MirrorErrorCallback _onError;

    private record struct MirrorEntry(string FilePath, IReadOnlyList<SessionStoreEntry> Entries, int Bytes);

    private readonly object _gate = new();
    private List<MirrorEntry> _pending = new();
    private int _pendingEntries;
    private int _pendingBytes;
    // Tail of the drain chain. Each drain detaches the pending buffer and
    // links itself behind the previous drain atomically (under _gate), so
    // store appends happen in exactly detach order: the .NET equivalent of
    // Python's FIFO anyio.Lock acquired with no await between detach and
    // acquire. Never faults.
    private Task _tail = Task.CompletedTask;
    private int _disposed;

    /// <summary>Per-append timeout (default 60s).</summary>
    public TimeSpan SendTimeout { get; init; } = DefaultSendTimeout;

    /// <summary>Eager-flush threshold (entry count). Zero ⇒ eager mode.</summary>
    public int MaxPendingEntries { get; init; } = DefaultMaxPendingEntries;

    /// <summary>Eager-flush threshold (bytes). Zero ⇒ eager mode.</summary>
    public int MaxPendingBytes { get; init; } = DefaultMaxPendingBytes;

    /// <summary>Construct a batcher.</summary>
    /// <param name="store">Destination session store.</param>
    /// <param name="projectsDir">Resolves transcript file paths to
    /// <see cref="SessionKey"/>s via
    /// <see cref="SessionPaths.FilePathToSessionKey"/>.</param>
    /// <param name="onError">Invoked with retry-exhausted batches. Never
    /// raises from the batcher's perspective.</param>
    public TranscriptMirrorBatcher(ISessionStore store, string projectsDir, MirrorErrorCallback onError)
    {
        _store = store;
        _projectsDir = projectsDir;
        _onError = onError;
    }

    /// <summary>
    /// Buffer a frame; schedule an eager flush if thresholds are exceeded.
    /// </summary>
    public void Enqueue(string filePath, IReadOnlyList<SessionStoreEntry> entries)
    {
        if (entries.Count == 0) return;
        int size = ApproxJsonSize(entries);
        bool overflow;
        lock (_gate)
        {
            _pending.Add(new MirrorEntry(filePath, entries, size));
            _pendingEntries += entries.Count;
            _pendingBytes += size;
            overflow = _pendingEntries > MaxPendingEntries || _pendingBytes > MaxPendingBytes;
        }
        if (overflow)
        {
            // Detach + chain synchronously (ordering); run off the caller's
            // thread (fire-and-forget, so a slow adapter never stalls the
            // read loop).
            var (items, prev, done) = DetachAndChain();
            _ = Task.Run(() => RunDrainAsync(items, prev, done, CancellationToken.None));
        }
    }

    /// <summary>Flush all pending entries, serialized after any in-flight
    /// eager flush (which is awaited even when nothing new is pending).</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var (items, prev, done) = DetachAndChain();
        return Task.Run(() => RunDrainAsync(items, prev, done, cancellationToken), CancellationToken.None);
    }

    /// <summary>Final flush before teardown. Never raises.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        try { await FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Cancellation during shutdown is expected — drop without noise (commit 9d2c650).
        }
        catch
        {
            // Defensive: FlushAsync already wraps everything.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // No disposable synchronization primitive: every in-flight drain
        // completes its own chain link, so nothing is torn down under it.
        await CloseAsync().ConfigureAwait(false);
    }

    private (List<MirrorEntry> Items, Task Prev, TaskCompletionSource Done) DetachAndChain()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var items = _pending;
            _pending = new List<MirrorEntry>();
            _pendingEntries = 0;
            _pendingBytes = 0;
            var prev = _tail;
            _tail = done.Task;
            return (items, prev, done);
        }
    }

    private async Task RunDrainAsync(
        List<MirrorEntry> items, Task prev, TaskCompletionSource done, CancellationToken cancellationToken)
    {
        var errors = new List<(SessionKey Key, string Message)>();
        try
        {
            // Wait for the previous drain (never faults: links complete via
            // TrySetResult). Deliberately not cancellable: skipping ahead
            // would reorder appends.
            await prev.ConfigureAwait(false);
            if (items.Count == 0) return;
            await DoFlushAsync(items, errors, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Quiet on shutdown cancellation (commit 9d2c650). DoFlushAsync
            // already wraps store.AppendAsync; this guards any remaining
            // unguarded path so the "never raises" contract holds.
            return;
        }
        finally
        {
            // Release the next drain before reporting errors so a slow
            // OnError callback cannot block subsequent appends (Python parity).
            done.TrySetResult();
        }

        foreach (var (key, msg) in errors)
        {
            try { await _onError(key, msg, cancellationToken).ConfigureAwait(false); }
            catch { /* defensive — never raise */ }
        }
    }

    private async Task DoFlushAsync(
        List<MirrorEntry> items,
        List<(SessionKey, string)> errors,
        CancellationToken cancellationToken)
    {
        // Coalesce by file_path so each unique file gets one append per flush.
        var byPath = new Dictionary<string, List<SessionStoreEntry>>();
        var pathOrder = new List<string>();
        foreach (var item in items)
        {
            if (!byPath.TryGetValue(item.FilePath, out var bucket))
            {
                bucket = new List<SessionStoreEntry>();
                byPath[item.FilePath] = bucket;
                pathOrder.Add(item.FilePath);
            }
            bucket.AddRange(item.Entries);
        }

        foreach (var filePath in pathOrder)
        {
            var entries = byPath[filePath];
            if (entries.Count == 0) continue;

            var key = SessionPaths.FilePathToSessionKey(filePath, _projectsDir);
            if (key is null)
            {
                // filePath not under projects dir — drop silently (Python logs a warning here).
                continue;
            }

            Exception? lastErr = null;
            bool succeeded = false;
            for (int attempt = 0; attempt < MirrorAppendMaxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(MirrorAppendBackoff[attempt - 1], cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(SendTimeout);
                    await _store.AppendAsync(key, entries, cts.Token).ConfigureAwait(false);
                    succeeded = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException ex)
                {
                    // Timeout — do not retry (in-flight call may still land).
                    lastErr = ex;
                    break;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                }
            }
            if (!succeeded)
            {
                errors.Add((key, lastErr?.Message ?? "unknown error"));
            }
        }
    }

    private static int ApproxJsonSize(IReadOnlyList<SessionStoreEntry> entries)
    {
        try
        {
            return JsonSerializer.Serialize(entries).Length;
        }
        catch
        {
            // Fallback estimate; never block enqueue on serialization quirks.
            return entries.Count * 64;
        }
    }
}
