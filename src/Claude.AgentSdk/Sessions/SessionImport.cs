// Claude Agent SDK for .NET — Replay a local on-disk session transcript into a SessionStore.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/_internal/session_import.py

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claude.AgentSdk.Sessions;

/// <summary>
/// Replay a local on-disk session transcript into a
/// <see cref="ISessionStore"/>. Mirrors Python <c>import_session_to_store</c>.
/// </summary>
public static class SessionImport
{
    /// <summary>
    /// Stream-read <paramref name="sessionFilePath"/> line-by-line and flush
    /// to <see cref="ISessionStore.AppendAsync"/> in batches of
    /// <paramref name="batchSize"/> entries (or 1 MiB of line text,
    /// whichever comes first). Skips blank lines.
    /// </summary>
    /// <exception cref="JsonException">
    /// A line is not valid JSON, or is not a JSON object (Python <c>json.loads</c> raises on a
    /// malformed line; a non-object line cannot be represented as a <see cref="SessionStoreEntry"/>).
    /// Batches before the bad line have already been appended.
    /// </exception>
    public static async Task ImportSessionFileAsync(
        string sessionFilePath,
        SessionKey key,
        ISessionStore store,
        int batchSize = TranscriptMirrorBatcher.DefaultMaxPendingEntries,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) batchSize = TranscriptMirrorBatcher.DefaultMaxPendingEntries;

        var batch = new List<SessionStoreEntry>();
        long nbytes = 0;
        using var sr = new StreamReader(sessionFilePath, Encoding.UTF8);
        string? line;
        while ((line = await sr.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            // Malformed lines raise rather than being silently dropped (Python json.loads).
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException e)
            {
                throw new JsonException($"Invalid JSON in {sessionFilePath}: {e.Message}", e);
            }
            if (node is not JsonObject obj)
                throw new JsonException($"Invalid transcript line in {sessionFilePath}: expected a JSON object");
            batch.Add(SessionSummary.JsonObjectToEntry(obj));
            nbytes += line.Length;
            if (batch.Count >= batchSize || nbytes >= TranscriptMirrorBatcher.DefaultMaxPendingBytes)
            {
                await store.AppendAsync(key, batch, cancellationToken).ConfigureAwait(false);
                batch = new List<SessionStoreEntry>();
                nbytes = 0;
            }
        }
        if (batch.Count > 0)
            await store.AppendAsync(key, batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replay a local session transcript into a <see cref="ISessionStore"/>.
    /// Mirrors Python <c>import_session_to_store</c>.
    /// </summary>
    /// <param name="sessionId">UUID of the session to import.</param>
    /// <param name="store">Destination <see cref="ISessionStore"/>.</param>
    /// <param name="directory">
    /// Project directory under which to find the session JSONL. Searched as
    /// <c>{projects_dir}/{sanitize(directory)}/{sessionId}.jsonl</c>. If
    /// <c>null</c>, all project directories are scanned.
    /// </param>
    /// <param name="includeSubagents">
    /// If <c>true</c> (default), also import subagent transcripts under
    /// <c>&lt;sessionId&gt;/subagents/**</c> and their <c>.meta.json</c> sidecars.
    /// </param>
    /// <param name="batchSize">Max entries per <see cref="ISessionStore.AppendAsync"/> call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task ImportSessionToStoreAsync(
        string sessionId,
        ISessionStore store,
        string? directory = null,
        bool includeSubagents = true,
        int batchSize = TranscriptMirrorBatcher.DefaultMaxPendingEntries,
        CancellationToken cancellationToken = default)
    {
        if (!SessionPaths.ValidateUuid(sessionId))
            throw new InvalidSessionIdException(sessionId);

        var resolved = ResolveSessionFilePath(sessionId, directory);
        if (resolved is null)
            throw new SessionNotFoundException(sessionId);

        // Key under the on-disk project directory name (file_path_to_session_key parity).
        var projectKey = new DirectoryInfo(Path.GetDirectoryName(resolved)!).Name;
        var mainKey = new SessionKey { ProjectKey = projectKey, SessionId = sessionId };
        await ImportSessionFileAsync(resolved, mainKey, store, batchSize, cancellationToken).ConfigureAwait(false);

        if (!includeSubagents) return;

        var sessionDir = Path.Combine(Path.GetDirectoryName(resolved)!, sessionId);
        var subagentsDir = Path.Combine(sessionDir, "subagents");

        foreach (var filePath in CollectJsonlFiles(subagentsDir))
        {
            var rel = Path.GetRelativePath(sessionDir, filePath);
            var relNoExt = rel[..^".jsonl".Length];
            var subKey = new SessionKey
            {
                ProjectKey = projectKey,
                SessionId = sessionId,
                Subpath = relNoExt.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'),
            };
            await ImportSessionFileAsync(filePath, subKey, store, batchSize, cancellationToken).ConfigureAwait(false);

            // Sidecar metadata file. A missing, corrupt, or non-object sidecar
            // is treated as absent (the transcript is still imported); other
            // read errors propagate. Mirrors Python _read_agent_metadata_sidecar.
            var metaNode = await ReadAgentMetadataSidecarAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (metaNode is not null)
            {
                // Synthetic discriminator last so a stray "type" key can never shadow it.
                metaNode["type"] = "agent_metadata";
                var entry = SessionSummary.JsonObjectToEntry(metaNode);
                await store.AppendAsync(subKey, new[] { entry }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Search the projects directory tree for the non-empty JSONL for
    /// <paramref name="sessionId"/> (Python <c>_resolve_session_file_path</c>). If
    /// <paramref name="directory"/> is given, its project directory (with the long-path
    /// hash-mismatch prefix fallback) and then its worktrees are checked; otherwise
    /// every project directory.
    /// </summary>
    public static string? ResolveSessionFilePath(string sessionId, string? directory)
        => SessionTranscripts.FindSessionFile(sessionId, directory)?.FilePath;

    internal static async Task<JsonObject?> ReadAgentMetadataSidecarAsync(string transcriptPath, CancellationToken cancellationToken)
    {
        var metaPath = transcriptPath[..^".jsonl".Length] + ".meta.json";
        string metaText;
        try
        {
            metaText = await File.ReadAllTextAsync(metaPath, new UTF8Encoding(false, throwOnInvalidBytes: true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (DecoderFallbackException) { return null; } // Python: UnicodeDecodeError is a ValueError
        try
        {
            return JsonNode.Parse(metaText) as JsonObject;
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; } // e.g. duplicate keys
    }

    /// <summary>
    /// Recursively yield <c>*.jsonl</c> files under <paramref name="baseDir"/>, entries sorted by
    /// name per directory with subdirectories visited in place (Python <c>_collect_jsonl_files</c>).
    /// </summary>
    private static IEnumerable<string> CollectJsonlFiles(string baseDir)
    {
        string[] dirents;
        try
        {
            dirents = Directory.GetFileSystemEntries(baseDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        Array.Sort(dirents, (x, y) => string.CompareOrdinal(Path.GetFileName(x), Path.GetFileName(y)));
        foreach (var entry in dirents)
        {
            if (Directory.Exists(entry))
            {
                foreach (var f in CollectJsonlFiles(entry)) yield return f;
            }
            else if (File.Exists(entry) && entry.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                yield return entry;
            }
        }
    }
}
