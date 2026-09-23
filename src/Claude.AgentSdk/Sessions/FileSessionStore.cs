// Claude Agent SDK for .NET — JSONL file-based ISessionStore.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/_internal/sessions.py
// (on-disk transcript layout under <projects_dir>/<project_key>/<session_id>.jsonl
// plus subagents/<...>.jsonl). Pure .NET implementation — there is no direct
// Python counterpart, but the on-disk layout matches what the CLI writes and
// what file_path_to_session_key() decodes. Path-containment policy follows the
// defense-in-depth guards in Python's session_resume._is_safe_subpath and
// examples/session_stores/s3_session_store.py.

using System.Text;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using System.Text.Json.Nodes;

namespace Claude.AgentSdk.Sessions;

/// <summary>
/// JSONL file-based <see cref="ISessionStore"/>. Stores transcripts on disk
/// using the same layout the CLI uses:
/// <list type="bullet">
///   <item><c>{root}/{project_key}/{session_id}.jsonl</c></item>
///   <item><c>{root}/{project_key}/{session_id}/{subpath}.jsonl</c></item>
///   <item><c>{root}/{project_key}/.summaries/{session_id}.json</c> (summary sidecar)</item>
/// </list>
/// <para>Thread-safe: each operation serializes on an internal lock so
/// interleaved appends from concurrent tasks remain ordered.</para>
/// <para>Security: project keys, session ids and subpath segments are
/// validated as single, non-traversing path components (no empty,
/// <c>.</c>, <c>..</c>, separators, NUL, drive prefixes or rooted values) and
/// every resolved path must lie strictly under the root; otherwise an
/// <see cref="ArgumentException"/> is thrown. On Unix, directories are created
/// with mode 0700 and files with mode 0600.</para>
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    /// <summary>Reserved directory name for summary sidecars inside a project dir.</summary>
    private const string SummariesDirName = ".summaries";

    private const UnixFileMode PrivateDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly char[] ForbiddenComponentChars = { '/', '\\', '\0' };

    private readonly string _root;
    private readonly string _rootFull;
    private readonly object _gate = new();

    /// <summary>Construct a store rooted at <paramref name="rootDirectory"/>.
    /// The directory is created if it does not exist.</summary>
    public FileSessionStore(string rootDirectory)
    {
        _root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        if (string.IsNullOrWhiteSpace(_root) || _root.Contains('\0'))
            throw new ArgumentException("rootDirectory must be a non-empty path", nameof(rootDirectory));
        _rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        CreatePrivateDirectory(_rootFull);
    }

    /// <summary>Root directory under which transcripts and summary sidecars are stored.</summary>
    public string RootDirectory => _root;

    // ---- Path containment ------------------------------------------------

    internal static bool IsSafeComponent(string? part)
    {
        if (string.IsNullOrEmpty(part)) return false;
        if (part == "." || part == "..") return false;
        if (part.IndexOfAny(ForbiddenComponentChars) >= 0) return false;
        // Drive prefixes ("C:foo") are rejected on every OS (Python uses
        // ntpath.splitdrive regardless of host); any ':' on Windows (ADS).
        if (part.Length >= 2 && char.IsLetter(part[0]) && part[1] == ':') return false;
        if (OperatingSystem.IsWindows() && part.Contains(':')) return false;
        if (Path.IsPathRooted(part)) return false;
        return true;
    }

    private static string ValidateComponent(string? value, string what)
    {
        if (!IsSafeComponent(value))
            throw new ArgumentException($"Invalid {what} for FileSessionStore: '{value}'", what);
        return value!;
    }

    private static string ValidateSessionId(string sessionId)
    {
        ValidateComponent(sessionId, nameof(SessionKey.SessionId));
        if (string.Equals(sessionId, SummariesDirName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Invalid SessionId for FileSessionStore: '{sessionId}' is reserved", nameof(SessionKey.SessionId));
        return sessionId;
    }

    private static string[] ValidateSubpath(string subpath)
    {
        var parts = subpath.Split('/', '\\');
        foreach (var p in parts)
        {
            if (!IsSafeComponent(p))
                throw new ArgumentException(
                    $"Invalid Subpath for FileSessionStore: '{subpath}'", nameof(SessionKey.Subpath));
        }
        return parts;
    }

    /// <summary>Resolve <paramref name="combined"/> and require it to lie
    /// strictly under the store root.</summary>
    private string Contained(string combined)
    {
        var full = Path.GetFullPath(combined);
        if (!full.StartsWith(_rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Path escapes FileSessionStore root: '{combined}'");
        return full;
    }

    private string ProjectDir(string projectKey)
        => Contained(Path.Combine(_rootFull, ValidateComponent(projectKey, nameof(SessionKey.ProjectKey))));

    private string SessionSubRoot(string projectKey, string sessionId)
        => Contained(Path.Combine(ProjectDir(projectKey), ValidateSessionId(sessionId)));

    private string TranscriptPath(SessionKey key)
    {
        var pdir = ProjectDir(key.ProjectKey);
        var sid = ValidateSessionId(key.SessionId);
        if (string.IsNullOrEmpty(key.Subpath))
            return Contained(Path.Combine(pdir, sid + ".jsonl"));
        // Subpath is always '/'-joined in the wire format; translate to OS sep.
        var parts = ValidateSubpath(key.Subpath);
        return Contained(Path.Combine(pdir, sid, Path.Combine(parts) + ".jsonl"));
    }

    private string SummariesDir(string projectKey)
        => Contained(Path.Combine(ProjectDir(projectKey), SummariesDirName));

    private string SummaryPath(string projectKey, string sessionId)
        => Contained(Path.Combine(SummariesDir(projectKey), ValidateSessionId(sessionId) + ".json"));

    // ---- Permissions -----------------------------------------------------

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, PrivateDirMode);
    }

    internal static FileStream OpenPrivateFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var opts = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = PrivateFileMode;
        return new FileStream(path, opts);
    }

    // ---- ISessionStore ---------------------------------------------------

    /// <inheritdoc />
    public async Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
    {
        // Surface validation failures through the returned Task (async contract).
        await Task.CompletedTask.ConfigureAwait(false);
        var path = TranscriptPath(key);
        if (entries.Count == 0) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) CreatePrivateDirectory(dir);

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(SerializeEntry(e));
            sb.Append('\n');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        lock (_gate)
        {
            // Append synchronously while holding the lock to preserve ordering
            // between concurrent appends (mirrors Python adapters where the
            // event loop serializes writes).
            using (var fs = OpenPrivateFile(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
            var mtime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try { File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime); }
            catch { /* best-effort */ }

            if (string.IsNullOrEmpty(key.Subpath))
            {
                var prev = TryReadSummary(key.ProjectKey, key.SessionId);
                var folded = SessionSummary.FoldSessionSummary(prev, key, entries) with { Mtime = mtime };
                WriteSummary(key.ProjectKey, folded);
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        var path = TranscriptPath(key);
        if (!File.Exists(path)) return Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);

        var results = new List<SessionStoreEntry>();
        lock (_gate)
        {
            using var sr = new StreamReader(path, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // A torn/truncated line (e.g. process killed mid-append) must
                // not make the whole transcript unreadable — skip it.
                var entry = TryParseEntry(line);
                if (entry is not null) results.Add(entry);
            }
        }
        return Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(results);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionStoreListEntry>> ListSessionsAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        var pdir = ProjectDir(projectKey);
        var results = new List<SessionStoreListEntry>();
        if (!Directory.Exists(pdir))
            return Task.FromResult<IReadOnlyList<SessionStoreListEntry>>(results);

        foreach (var file in Directory.EnumerateFiles(pdir, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            // Use real file mtime in ms — shares the same clock as AppendAsync().
            long mtimeMs;
            try { mtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).ToUnixTimeMilliseconds(); }
            catch { mtimeMs = 0; }
            results.Add(new SessionStoreListEntry(name, mtimeMs));
        }
        return Task.FromResult<IReadOnlyList<SessionStoreListEntry>>(results);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionSummaryEntry>> ListSessionSummariesAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        var sdir = SummariesDir(projectKey);
        var results = new List<SessionSummaryEntry>();
        if (!Directory.Exists(sdir))
            return Task.FromResult<IReadOnlyList<SessionSummaryEntry>>(results);

        foreach (var file in Directory.EnumerateFiles(sdir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var summ = JsonSerializer.Deserialize(text, SdkJsonContext.Default.SessionSummaryEntry);
                if (summ is not null) results.Add(summ);
            }
            catch
            {
                // Skip corrupt sidecars — they will be regenerated on the next append.
            }
        }
        return Task.FromResult<IReadOnlyList<SessionSummaryEntry>>(results);
    }

    /// <inheritdoc />
    public Task DeleteAsync(SessionKey key, CancellationToken cancellationToken = default)
    {
        // Resolve and validate every path before removing anything.
        var path = TranscriptPath(key);
        var isMain = string.IsNullOrEmpty(key.Subpath);
        var subRoot = isMain ? SessionSubRoot(key.ProjectKey, key.SessionId) : null;
        var summary = isMain ? SummaryPath(key.ProjectKey, key.SessionId) : null;

        lock (_gate)
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }

            if (subRoot is not null && Directory.Exists(subRoot))
            {
                try
                {
                    // Never recurse through a symlink/junction: that could
                    // delete data outside the store root. Removing the link
                    // itself is safe.
                    var info = new DirectoryInfo(subRoot);
                    if (info.LinkTarget is not null) info.Delete();
                    else Directory.Delete(subRoot, recursive: true);
                }
                catch { /* ignore */ }
            }
            if (summary is not null && File.Exists(summary))
            {
                try { File.Delete(summary); } catch { /* ignore */ }
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListSubkeysAsync(SessionListSubkeysKey key, CancellationToken cancellationToken = default)
    {
        var subRoot = SessionSubRoot(key.ProjectKey, key.SessionId);
        var results = new List<string>();
        if (!Directory.Exists(subRoot))
            return Task.FromResult<IReadOnlyList<string>>(results);

        foreach (var file in Directory.EnumerateFiles(subRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(subRoot, file);
            // Strip .jsonl and normalize to '/'.
            rel = rel[..^".jsonl".Length];
            rel = rel.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            results.Add(rel);
        }
        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    // ---- Helpers ----------------------------------------------------------

    private SessionSummaryEntry? TryReadSummary(string projectKey, string sessionId)
    {
        var path = SummaryPath(projectKey, sessionId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), SdkJsonContext.Default.SessionSummaryEntry);
        }
        catch
        {
            return null;
        }
    }

    private void WriteSummary(string projectKey, SessionSummaryEntry summary)
    {
        var dir = SummariesDir(projectKey);
        CreatePrivateDirectory(dir);
        var path = SummaryPath(projectKey, summary.SessionId);
        // Atomic replace: write a temp file in the same directory (not
        // matching the *.json listing pattern), then rename over the target
        // so readers never observe a torn sidecar.
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(summary, SdkJsonContext.Default.SessionSummaryEntry));
            using (var fs = OpenPrivateFile(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    private static string SerializeEntry(SessionStoreEntry e)
    {
        var obj = SessionSummary.EntryToJsonObject(e);
        return obj.ToJsonString();
    }

    private static SessionStoreEntry? TryParseEntry(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return null; }
        if (node is JsonObject obj)
            return SessionSummary.JsonObjectToEntry(obj);
        // Fallback for non-object lines: pass through opaque.
        return new SessionStoreEntry { Type = "" };
    }
}
