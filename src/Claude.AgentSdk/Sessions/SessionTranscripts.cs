// Claude Agent SDK for .NET — on-disk session listing and transcript reading.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/_internal/sessions.py
// (lite head/tail parsing, _parse_session_info_from_lite, _build_conversation_chain,
// _build_subagent_chain, project-dir / worktree resolution, _entries_to_jsonl).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using System.Text.Json.Nodes;

namespace Claude.AgentSdk.Sessions;

/// <summary>Head/tail/stat snapshot of a session file (Python <c>_LiteSessionFile</c>).</summary>
internal sealed record LiteSessionFile(long Mtime, long Size, string Head, string Tail);

/// <summary>
/// Internals shared by the disk-backed and store-backed session readers, ported from
/// Python <c>_internal/sessions.py</c>.
/// </summary>
internal static class SessionTranscripts
{
    /// <summary>Size of the head/tail buffer for lite metadata reads (Python LITE_READ_BUF_SIZE).</summary>
    public const int LiteReadBufSize = 65536;

    /// <summary>Upper bound on concurrent store loads when listing (Python _STORE_LIST_LOAD_CONCURRENCY).</summary>
    public const int StoreListLoadConcurrency = 16;

    /// <summary>Entry types that carry uuid + parentUuid chain links (Python _TRANSCRIPT_ENTRY_TYPES).</summary>
    public static readonly HashSet<string> TranscriptEntryTypes = new(StringComparer.Ordinal)
    {
        "user", "assistant", "progress", "system", "attachment",
    };

    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: false);

    // ---- JSON string field extraction (no full parse; works on truncated lines) -----------

    private static string UnescapeJsonString(string raw)
    {
        if (!raw.Contains('\\')) return raw;
        try
        {
            return JsonSerializer.Deserialize("\"" + raw + "\"", SdkJsonContext.Default.String) ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>First <c>"key":"value"</c> occurrence (Python <c>_extract_json_string_field</c>).</summary>
    public static string? ExtractJsonStringField(string text, string key)
    {
        foreach (var pattern in new[] { $"\"{key}\":\"", $"\"{key}\": \"" })
        {
            var idx = text.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) continue;
            var valueStart = idx + pattern.Length;
            var i = valueStart;
            while (i < text.Length)
            {
                if (text[i] == '\\') { i += 2; continue; }
                if (text[i] == '"') return UnescapeJsonString(text[valueStart..i]);
                i++;
            }
        }
        return null;
    }

    /// <summary>Last <c>"key":"value"</c> occurrence (Python <c>_extract_last_json_string_field</c>).</summary>
    public static string? ExtractLastJsonStringField(string text, string key)
    {
        string? last = null;
        foreach (var pattern in new[] { $"\"{key}\":\"", $"\"{key}\": \"" })
        {
            var searchFrom = 0;
            while (true)
            {
                var idx = text.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                var valueStart = idx + pattern.Length;
                var i = valueStart;
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"')
                    {
                        last = UnescapeJsonString(text[valueStart..i]);
                        break;
                    }
                    i++;
                }
                searchFrom = Math.Min(i + 1, text.Length);
                if (searchFrom >= text.Length) break;
            }
        }
        return last;
    }

    /// <summary>
    /// First meaningful user prompt in a JSONL head chunk (Python
    /// <c>_extract_first_prompt_from_head</c>). Truncated to 200 chars.
    /// </summary>
    public static string ExtractFirstPromptFromHead(string head)
    {
        var commandFallback = "";
        foreach (var line in head.Split('\n'))
        {
            if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal) &&
                !line.Contains("\"type\": \"user\"", StringComparison.Ordinal)) continue;
            if (line.Contains("\"tool_result\"", StringComparison.Ordinal)) continue;
            if (line.Contains("\"isMeta\":true", StringComparison.Ordinal) ||
                line.Contains("\"isMeta\": true", StringComparison.Ordinal)) continue;
            if (line.Contains("\"isCompactSummary\":true", StringComparison.Ordinal) ||
                line.Contains("\"isCompactSummary\": true", StringComparison.Ordinal)) continue;

            if (TryParseObject(line) is not { } entry) continue;
            if (StringOf(entry, "type") != "user") continue;
            if (entry["message"] is not JsonObject message) continue;

            var texts = new List<string>();
            var content = message["content"];
            if (content is JsonValue cv && cv.TryGetValue<string>(out var cs))
                texts.Add(cs);
            else if (content is JsonArray arr)
            {
                foreach (var block in arr)
                {
                    if (block is JsonObject bo && StringOf(bo, "type") == "text" && StringOf(bo, "text") is { } t)
                        texts.Add(t);
                }
            }

            foreach (var raw in texts)
            {
                var result = raw.Replace("\n", " ").Trim();
                if (result.Length == 0) continue;

                var cmd = SessionSummary.CommandNameRegex.Match(result);
                if (cmd.Success)
                {
                    if (commandFallback.Length == 0) commandFallback = cmd.Groups[1].Value;
                    continue;
                }
                if (SessionSummary.SkipFirstPromptPattern.IsMatch(result)) continue;
                if (result.Length > 200) result = result[..200].TrimEnd() + "\u2026";
                return result;
            }
        }
        return commandFallback;
    }

    // ---- lite reads ----------------------------------------------------------------------

    /// <summary>
    /// Stat + head/tail read of a session file (Python <c>_read_session_lite</c>).
    /// Null on any error or for an empty file.
    /// </summary>
    public static LiteSessionFile? ReadSessionLite(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = fs.Length;
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

            var headBuf = new byte[LiteReadBufSize];
            var headLen = fs.ReadAtLeast(headBuf, LiteReadBufSize, throwOnEndOfStream: false);
            if (headLen == 0) return null;
            var head = Utf8.GetString(headBuf, 0, headLen);

            var tailOffset = Math.Max(0, size - LiteReadBufSize);
            string tail;
            if (tailOffset == 0)
            {
                tail = head;
            }
            else
            {
                fs.Seek(tailOffset, SeekOrigin.Begin);
                var tailBuf = new byte[LiteReadBufSize];
                var tailLen = fs.ReadAtLeast(tailBuf, LiteReadBufSize, throwOnEndOfStream: false);
                tail = Utf8.GetString(tailBuf, 0, tailLen);
            }
            return new LiteSessionFile(mtime, size, head, tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Lite shape from an in-memory JSONL string (Python <c>_jsonl_to_lite</c>).</summary>
    public static LiteSessionFile JsonlToLite(string jsonl, long mtime)
    {
        var buf = Utf8.GetBytes(jsonl);
        var size = buf.Length;
        var head = Utf8.GetString(buf, 0, Math.Min(size, LiteReadBufSize));
        var tail = size > LiteReadBufSize
            ? Utf8.GetString(buf, size - LiteReadBufSize, LiteReadBufSize)
            : head;
        return new LiteSessionFile(mtime, size, head, tail);
    }

    /// <summary>Best-effort mtime from the last entry's timestamp (Python <c>_mtime_from_jsonl_tail</c>).</summary>
    public static long MtimeFromJsonlTail(string jsonl)
    {
        var trimmed = jsonl.TrimEnd();
        var lastLine = trimmed[(trimmed.LastIndexOf('\n') + 1)..];
        if (TryParseObject(lastLine) is { } obj && StringOf(obj, "timestamp") is { } ts &&
            SessionSummary.IsoToEpochMs(ts) is { } ms)
            return ms;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// SDKSessionInfo from a lite read; null for sidechain / metadata-only sessions
    /// (Python <c>_parse_session_info_from_lite</c>).
    /// </summary>
    public static SDKSessionInfo? ParseSessionInfoFromLite(string sessionId, LiteSessionFile lite, string? projectPath = null)
    {
        var (head, tail) = (lite.Head, lite.Tail);

        var firstNewline = head.IndexOf('\n');
        var firstLine = firstNewline >= 0 ? head[..firstNewline] : head;
        if (firstLine.Contains("\"isSidechain\":true", StringComparison.Ordinal) ||
            firstLine.Contains("\"isSidechain\": true", StringComparison.Ordinal))
            return null;

        var customTitle = NullIfEmpty(ExtractLastJsonStringField(tail, "customTitle"))
                          ?? NullIfEmpty(ExtractLastJsonStringField(head, "customTitle"))
                          ?? NullIfEmpty(ExtractLastJsonStringField(tail, "aiTitle"))
                          ?? NullIfEmpty(ExtractLastJsonStringField(head, "aiTitle"));
        var firstPrompt = NullIfEmpty(ExtractFirstPromptFromHead(head));
        var summary = customTitle
                      ?? NullIfEmpty(ExtractLastJsonStringField(tail, "lastPrompt"))
                      ?? NullIfEmpty(ExtractLastJsonStringField(tail, "summary"))
                      ?? firstPrompt;
        if (string.IsNullOrEmpty(summary)) return null;

        var gitBranch = NullIfEmpty(ExtractLastJsonStringField(tail, "gitBranch"))
                        ?? NullIfEmpty(ExtractJsonStringField(head, "gitBranch"));
        var sessionCwd = NullIfEmpty(ExtractJsonStringField(head, "cwd")) ?? NullIfEmpty(projectPath);

        // Scope tag extraction to {"type":"tag"} lines — a bare scan for "tag" would match
        // tool_use inputs (git tag, Docker tags, ...).
        string? tag = null;
        var tagLine = tail.Split('\n').Reverse().FirstOrDefault(l => l.StartsWith("{\"type\":\"tag\"", StringComparison.Ordinal));
        if (tagLine is not null)
            tag = NullIfEmpty(ExtractLastJsonStringField(tagLine, "tag"));

        long? createdAt = null;
        if (ExtractJsonStringField(head, "timestamp") is { Length: > 0 } firstTs)
            createdAt = SessionSummary.IsoToEpochMs(firstTs);

        return new SDKSessionInfo
        {
            SessionId = sessionId,
            Summary = summary,
            LastModified = lite.Mtime,
            FileSize = lite.Size,
            CustomTitle = customTitle,
            FirstPrompt = firstPrompt,
            GitBranch = gitBranch,
            Cwd = sessionCwd,
            Tag = tag,
            CreatedAt = createdAt,
        };
    }

    // ---- project directory resolution -------------------------------------------------------

    private static string GetProjectDir(string projectPath)
        => Path.Combine(SessionPaths.GetProjectsDir(), SessionPaths.SanitizePath(projectPath));

    /// <summary>
    /// Project directory for a path, tolerating hash mismatches for long (&gt;200 char)
    /// sanitized names via prefix scan (Python <c>_find_project_dir</c>).
    /// </summary>
    public static string? FindProjectDir(string projectPath)
    {
        var exact = GetProjectDir(projectPath);
        if (Directory.Exists(exact)) return exact;

        var sanitized = SessionPaths.SanitizePath(projectPath);
        if (sanitized.Length <= SessionPaths.MaxSanitizedLength) return null;

        var prefix = sanitized[..SessionPaths.MaxSanitizedLength] + "-";
        foreach (var dir in SafeEnumerateDirectories(SessionPaths.GetProjectsDir()))
        {
            if (Path.GetFileName(dir).StartsWith(prefix, StringComparison.Ordinal))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Absolute git worktree paths for the repo containing <paramref name="cwd"/>; empty if
    /// git is unavailable or cwd is not in a repo (Python <c>_get_worktree_paths</c>).
    /// </summary>
    public static IReadOnlyList<string> GetWorktreePaths(string cwd)
    {
        try
        {
            if (!Directory.Exists(cwd)) return Array.Empty<string>();
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "worktree", "list", "--porcelain" }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<string>();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return Array.Empty<string>();
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(stdout)) return Array.Empty<string>();
            return stdout.Split('\n')
                .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
                .Select(l => l["worktree ".Length..].TrimEnd('\r').Normalize(NormalizationForm.FormC))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Candidate project directories for a session lookup, in Python's search order:
    /// the project dir for <paramref name="directory"/>, then its git worktrees; or,
    /// with no directory, every directory under the projects dir.
    /// Each item is (projectDir, projectPath) where projectPath is the canonical cwd
    /// the directory belongs to (null when searching all projects).
    /// </summary>
    public static IEnumerable<(string ProjectDir, string? ProjectPath)> CandidateProjectDirs(string? directory)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            var canonical = SessionPaths.CanonicalizePath(directory);
            if (FindProjectDir(canonical) is { } pd) yield return (pd, canonical);
            foreach (var wt in GetWorktreePaths(canonical))
            {
                if (wt == canonical) continue;
                if (FindProjectDir(wt) is { } wpd) yield return (wpd, wt);
            }
            yield break;
        }

        foreach (var dir in SafeEnumerateDirectories(SessionPaths.GetProjectsDir()))
            yield return (dir, null);
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Path of the first non-empty <c>{sessionId}.jsonl</c> and its project directory
    /// (Python <c>_resolve_session_file_path</c> / <c>_find_session_file_with_dir</c>).
    /// </summary>
    public static (string FilePath, string ProjectDir)? FindSessionFile(string sessionId, string? directory)
    {
        var fileName = sessionId + ".jsonl";
        foreach (var (projectDir, _) in CandidateProjectDirs(directory))
        {
            var candidate = Path.Combine(projectDir, fileName);
            if (IsNonEmptyFile(candidate)) return (candidate, projectDir);
        }
        return null;
    }

    // ---- listing ---------------------------------------------------------------------------

    private static List<SDKSessionInfo> ReadSessionsFromDir(string projectDir, string? projectPath, bool includeProgrammatic = true)
    {
        var results = new List<SDKSessionInfo>();
        IEnumerable<string> files;
        try
        {
            files = Directory.GetFiles(projectDir, "*.jsonl");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".jsonl", StringComparison.Ordinal)) continue;
            var sessionId = name[..^".jsonl".Length];
            if (!SessionPaths.ValidateUuid(sessionId)) continue;
            if (ReadSessionLite(file) is not { } lite) continue;
            if (!includeProgrammatic && IsProgrammaticSession(lite)) continue;
            if (ParseSessionInfoFromLite(sessionId, lite, projectPath) is { } info) results.Add(info);
        }
        return results;
    }

    private static List<SDKSessionInfo> DeduplicateBySessionId(IEnumerable<SDKSessionInfo> sessions)
    {
        var byId = new Dictionary<string, SDKSessionInfo>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var s in sessions)
        {
            if (!byId.TryGetValue(s.SessionId, out var existing))
            {
                byId[s.SessionId] = s;
                order.Add(s.SessionId);
            }
            else if (s.LastModified > existing.LastModified)
            {
                byId[s.SessionId] = s;
            }
        }
        return order.Select(id => byId[id]).ToList();
    }

    /// <summary>Sort by last_modified desc, then offset/limit (Python <c>_apply_sort_limit_offset</c>).</summary>
    public static List<SDKSessionInfo> ApplySortLimitOffset(IEnumerable<SDKSessionInfo> sessions, int? limit, int offset)
    {
        // Stable sort, as Python's list.sort.
        IEnumerable<SDKSessionInfo> sorted = sessions.OrderByDescending(s => s.LastModified);
        if (offset > 0) sorted = sorted.Skip(offset);
        if (limit is > 0) sorted = sorted.Take(limit.Value);
        return sorted.ToList();
    }

    /// <summary>Python <c>list_sessions</c>.</summary>
    public static List<SDKSessionInfo> ListSessions(
        string? directory, int? limit, int offset, bool includeWorktrees, bool includeProgrammatic = true)
    {
        if (!string.IsNullOrEmpty(directory))
            return ListSessionsForProject(directory, limit, offset, includeWorktrees, includeProgrammatic);

        var all = new List<SDKSessionInfo>();
        foreach (var dir in SafeEnumerateDirectories(SessionPaths.GetProjectsDir()))
            all.AddRange(ReadSessionsFromDir(dir, null, includeProgrammatic));
        return ApplySortLimitOffset(DeduplicateBySessionId(all), limit, offset);
    }

    /// <summary>
    /// TS <c>VO</c>: a programmatic / headless session (SDK entrypoint) or a
    /// daemon / daemon-worker session. TS lists <c>sdk-cli</c>, <c>sdk-ts</c>
    /// and <c>sdk-py</c>; this SDK's own <c>sdk-dotnet</c> counts too.
    /// </summary>
    internal static bool IsProgrammaticSession(LiteSessionFile lite)
    {
        var entrypoint = ExtractJsonStringField(lite.Head, "entrypoint") ?? ExtractLastJsonStringField(lite.Tail, "entrypoint");
        if (entrypoint is "sdk-cli" or "sdk-ts" or "sdk-py" or "sdk-dotnet")
            return true;
        var line = lite.Head.Split('\n').FirstOrDefault(l => l.Contains("\"parentUuid\":", StringComparison.Ordinal)) ?? lite.Head;
        return ExtractJsonStringField(line, "sessionKind") is "daemon" or "daemon-worker";
    }

    private static List<SDKSessionInfo> ListSessionsForProject(
        string directory, int? limit, int offset, bool includeWorktrees, bool includeProgrammatic)
    {
        var canonicalDir = SessionPaths.CanonicalizePath(directory);
        var worktreePaths = includeWorktrees ? GetWorktreePaths(canonicalDir) : Array.Empty<string>();

        if (worktreePaths.Count <= 1)
        {
            var projectDir = FindProjectDir(canonicalDir);
            if (projectDir is null) return new List<SDKSessionInfo>();
            return ApplySortLimitOffset(ReadSessionsFromDir(projectDir, canonicalDir, includeProgrammatic), limit, offset);
        }

        var caseInsensitive = OperatingSystem.IsWindows();
        var indexed = worktreePaths
            .Select(wt =>
            {
                var sanitized = SessionPaths.SanitizePath(wt);
                return (Path: wt, Prefix: caseInsensitive ? sanitized.ToLowerInvariant() : sanitized);
            })
            .OrderByDescending(x => x.Prefix.Length)
            .ToList();

        string[] allDirents;
        try
        {
            var projectsDir = SessionPaths.GetProjectsDir();
            if (!Directory.Exists(projectsDir)) throw new DirectoryNotFoundException(projectsDir);
            allDirents = Directory.GetDirectories(projectsDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var projectDir = FindProjectDir(canonicalDir);
            if (projectDir is null) return new List<SDKSessionInfo>();
            return ApplySortLimitOffset(ReadSessionsFromDir(projectDir, canonicalDir, includeProgrammatic), limit, offset);
        }

        var allSessions = new List<SDKSessionInfo>();
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);

        // Always include the user's actual directory (handles subdirectories of a worktree root).
        if (FindProjectDir(canonicalDir) is { } canonicalProjectDir)
        {
            var dirBase = Path.GetFileName(canonicalProjectDir);
            seenDirs.Add(caseInsensitive ? dirBase.ToLowerInvariant() : dirBase);
            allSessions.AddRange(ReadSessionsFromDir(canonicalProjectDir, canonicalDir, includeProgrammatic));
        }

        foreach (var entry in allDirents)
        {
            var name = Path.GetFileName(entry);
            var dirName = caseInsensitive ? name.ToLowerInvariant() : name;
            if (seenDirs.Contains(dirName)) continue;

            foreach (var (wtPath, prefix) in indexed)
            {
                // Prefix match only for truncated (hash-suffixed) names; exact otherwise.
                var isMatch = dirName == prefix ||
                              (prefix.Length >= SessionPaths.MaxSanitizedLength &&
                               dirName.StartsWith(prefix + "-", StringComparison.Ordinal));
                if (!isMatch) continue;
                seenDirs.Add(dirName);
                allSessions.AddRange(ReadSessionsFromDir(entry, wtPath, includeProgrammatic));
                break;
            }
        }

        return ApplySortLimitOffset(DeduplicateBySessionId(allSessions), limit, offset);
    }

    /// <summary>Python <c>get_session_info</c> (disk).</summary>
    public static SDKSessionInfo? GetSessionInfo(string sessionId, string? directory)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return null;
        var fileName = sessionId + ".jsonl";
        foreach (var (projectDir, projectPath) in CandidateProjectDirs(directory))
        {
            if (ReadSessionLite(Path.Combine(projectDir, fileName)) is { } lite)
                return ParseSessionInfoFromLite(sessionId, lite, projectPath);
        }
        return null;
    }

    // ---- transcript parsing / chain building ---------------------------------------------

    /// <summary>
    /// JSONL → transcript entries with a string uuid and a transcript type; corrupt lines
    /// are skipped (Python <c>_parse_transcript_entries</c>).
    /// </summary>
    public static List<JsonObject> ParseTranscriptEntries(string content)
    {
        var entries = new List<JsonObject>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (TryParseObject(line) is not { } entry) continue;
            if (TranscriptEntryTypes.Contains(StringOf(entry, "type") ?? "") && StringOf(entry, "uuid") is not null)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Store entries filtered like <see cref="ParseTranscriptEntries"/> (Python <c>_filter_transcript_entries</c>).</summary>
    public static List<JsonObject> FilterTranscriptEntries(IEnumerable<SessionStoreEntry> entries)
    {
        var result = new List<JsonObject>();
        foreach (var e in entries)
        {
            if (!TranscriptEntryTypes.Contains(e.Type ?? "")) continue;
            var obj = SessionSummary.EntryToJsonObject(e);
            if (StringOf(obj, "uuid") is not null) result.Add(obj);
        }
        return result;
    }

    /// <summary>
    /// Conversation chain root → leaf via parentUuid from the best leaf (Python
    /// <c>_build_conversation_chain</c>). logicalParentUuid is intentionally not followed.
    /// </summary>
    public static List<JsonObject> BuildConversationChain(List<JsonObject> entries)
    {
        if (entries.Count == 0) return new List<JsonObject>();

        var byUuid = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var entryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var uid = StringOf(entries[i], "uuid")!;
            byUuid[uid] = entries[i];
            entryIndex[uid] = i;
        }

        var parentUuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (NullIfEmpty(StringOf(e, "parentUuid")) is { } p) parentUuids.Add(p);

        var leaves = new List<JsonObject>();
        foreach (var terminal in entries.Where(e => !parentUuids.Contains(StringOf(e, "uuid")!)))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = terminal;
            while (cur is not null)
            {
                var uid = StringOf(cur, "uuid")!;
                if (!seen.Add(uid)) break;
                if (StringOf(cur, "type") is "user" or "assistant")
                {
                    leaves.Add(cur);
                    break;
                }
                cur = NullIfEmpty(StringOf(cur, "parentUuid")) is { } parent && byUuid.TryGetValue(parent, out var pe) ? pe : null;
            }
        }
        if (leaves.Count == 0) return new List<JsonObject>();

        var mainLeaves = leaves.Where(l => !Truthy(l["isSidechain"]) && !Truthy(l["teamName"]) && !Truthy(l["isMeta"])).ToList();

        JsonObject PickBest(List<JsonObject> candidates)
        {
            var best = candidates[0];
            var bestIdx = entryIndex.GetValueOrDefault(StringOf(best, "uuid")!, -1);
            foreach (var c in candidates.Skip(1))
            {
                var idx = entryIndex.GetValueOrDefault(StringOf(c, "uuid")!, -1);
                if (idx > bestIdx) { best = c; bestIdx = idx; }
            }
            return best;
        }

        return WalkToRoot(mainLeaves.Count > 0 ? PickBest(mainLeaves) : PickBest(leaves), byUuid);
    }

    /// <summary>Subagent chain from the last user/assistant entry (Python <c>_build_subagent_chain</c>).</summary>
    public static List<JsonObject> BuildSubagentChain(List<JsonObject> entries)
    {
        if (entries.Count == 0) return new List<JsonObject>();
        var byUuid = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var e in entries) byUuid[StringOf(e, "uuid")!] = e;
        var leaf = entries.LastOrDefault(e => StringOf(e, "type") is "user" or "assistant");
        return leaf is null ? new List<JsonObject>() : WalkToRoot(leaf, byUuid);
    }

    private static List<JsonObject> WalkToRoot(JsonObject leaf, Dictionary<string, JsonObject> byUuid)
    {
        var chain = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? cur = leaf;
        while (cur is not null)
        {
            if (!seen.Add(StringOf(cur, "uuid")!)) break;
            chain.Add(cur);
            cur = NullIfEmpty(StringOf(cur, "parentUuid")) is { } parent && byUuid.TryGetValue(parent, out var pe) ? pe : null;
        }
        chain.Reverse();
        return chain;
    }

    private static bool IsVisibleMessage(JsonObject entry, bool includeSystemMessages = false)
    {
        var type = StringOf(entry, "type");
        if (type is not ("user" or "assistant") && !(includeSystemMessages && type == "system")) return false;
        if (Truthy(entry["isMeta"]) || Truthy(entry["isSidechain"])) return false;
        // isCompactSummary messages are intentionally included.
        return !Truthy(entry["teamName"]);
    }

    private static SessionMessage ToSessionMessage(JsonObject entry, string? parentToolUseId = null, string? parentAgentId = null)
    {
        JsonElement message;
        using (var doc = JsonDocument.Parse(entry["message"]?.ToJsonString() ?? "null"))
            message = doc.RootElement.Clone();
        return new SessionMessage(
            Type: StringOf(entry, "type") switch { "user" => "user", "system" => "system", _ => "assistant" },
            Uuid: StringOf(entry, "uuid") ?? "",
            SessionId: StringOf(entry, "sessionId") ?? "",
            MessageData: message,
            ParentToolUseId: parentToolUseId,
            ParentAgentId: parentAgentId);
    }

    private static List<SessionMessage> Page(List<SessionMessage> messages, int? limit, int offset)
    {
        if (limit is > 0)
            return messages.Skip(Math.Max(0, offset)).Take(limit.Value).ToList();
        if (offset > 0)
            return messages.Skip(offset).ToList();
        return messages;
    }

    /// <summary>Chain → visible messages → paging (Python <c>_entries_to_session_messages</c>).</summary>
    public static List<SessionMessage> EntriesToSessionMessages(
        List<JsonObject> entries, int? limit, int offset, bool includeSystemMessages = false)
        => Page(BuildConversationChain(entries)
                .Where(e => IsVisibleMessage(e, includeSystemMessages))
                .Select(e => ToSessionMessage(e))
                .ToList(), limit, offset);

    /// <summary>Python <c>_entries_to_subagent_messages</c>.</summary>
    public static List<SessionMessage> EntriesToSubagentMessages(
        List<JsonObject> entries, int? limit, int offset, string? parentToolUseId, string? parentAgentId)
        => Page(BuildSubagentChain(entries)
                .Where(e => StringOf(e, "type") is "user" or "assistant")
                .Select(e => ToSessionMessage(e, parentToolUseId, parentAgentId))
                .ToList(), limit, offset);

    /// <summary>Python <c>get_session_messages</c> (disk).</summary>
    public static List<SessionMessage> GetSessionMessages(
        string sessionId, string? directory, int? limit, int offset, bool includeSystemMessages = false)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return new List<SessionMessage>();
        var content = ReadSessionFile(sessionId, directory);
        if (string.IsNullOrEmpty(content)) return new List<SessionMessage>();
        return EntriesToSessionMessages(ParseTranscriptEntries(content), limit, offset, includeSystemMessages);
    }

    private static string? ReadSessionFile(string sessionId, string? directory)
    {
        var fileName = sessionId + ".jsonl";
        foreach (var (projectDir, _) in CandidateProjectDirs(directory))
        {
            try
            {
                var path = Path.Combine(projectDir, fileName);
                if (!File.Exists(path)) continue;
                var content = File.ReadAllText(path, Encoding.UTF8);
                if (content.Length > 0) return content;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Python: OSError -> try the next candidate.
            }
        }
        return null;
    }

    // ---- subagents ---------------------------------------------------------------------------

    private static string? ResolveSubagentsDir(string sessionId, string? directory)
    {
        if (FindSessionFile(sessionId, directory) is not { } found) return null;
        return Path.Combine(found.FilePath[..^".jsonl".Length], "subagents");
    }

    /// <summary>
    /// Recursively collect <c>agent-*.jsonl</c> files, name-sorted per directory
    /// (Python <c>_collect_agent_files</c>).
    /// </summary>
    public static List<(string AgentId, string FilePath)> CollectAgentFiles(string baseDir)
    {
        var results = new List<(string, string)>();
        void Walk(string current)
        {
            string[] dirents;
            try
            {
                dirents = Directory.GetFileSystemEntries(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            Array.Sort(dirents, (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            foreach (var entry in dirents)
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(entry))
                {
                    if (name.StartsWith("agent-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal))
                        results.Add((name["agent-".Length..^".jsonl".Length], entry));
                }
                else if (Directory.Exists(entry))
                {
                    Walk(entry);
                }
            }
        }
        Walk(baseDir);
        return results;
    }

    /// <summary>Python <c>list_subagents</c> (disk).</summary>
    public static List<string> ListSubagents(string sessionId, string? directory)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return new List<string>();
        if (ResolveSubagentsDir(sessionId, directory) is not { } dir) return new List<string>();
        return CollectAgentFiles(dir).Select(a => a.AgentId).ToList();
    }

    /// <summary>Python <c>get_subagent_messages</c> (disk).</summary>
    public static List<SessionMessage> GetSubagentMessages(string sessionId, string agentId, string? directory, int? limit, int offset)
    {
        if (!SessionPaths.ValidateUuid(sessionId) || string.IsNullOrEmpty(agentId)) return new List<SessionMessage>();
        if (ResolveSubagentsDir(sessionId, directory) is not { } dir) return new List<SessionMessage>();

        var match = CollectAgentFiles(dir).FirstOrDefault(a => a.AgentId == agentId).FilePath;
        if (match is null) return new List<SessionMessage>();

        string content;
        try
        {
            content = File.ReadAllText(match, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new List<SessionMessage>();
        }
        if (content.Length == 0) return new List<SessionMessage>();

        // Best-effort sidecar read: any failure degrades to "no metadata".
        JsonObject? meta;
        try
        {
            meta = SessionImport.ReadAgentMetadataSidecarAsync(match, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            meta = null;
        }
        var (toolUseId, parentAgentId) = ParentIdsFromAgentMetadata(meta);
        return EntriesToSubagentMessages(ParseTranscriptEntries(content), limit, offset, toolUseId, parentAgentId);
    }

    /// <summary>(toolUseId, parentAgentId) from agent metadata (Python <c>_parent_ids_from_agent_metadata</c>).</summary>
    public static (string? ToolUseId, string? ParentAgentId) ParentIdsFromAgentMetadata(JsonObject? meta)
        => meta is null ? (null, null) : (StringOf(meta, "toolUseId"), StringOf(meta, "parentAgentId"));

    // ---- JSONL serialization (Python json.dumps(separators=(",", ":")), ensure_ascii) ------

    /// <summary>
    /// Serialize store entries to JSONL the way Python <c>_entries_to_jsonl</c> does: one
    /// compact <c>json.dumps</c> (ASCII-escaped) per line, <c>type</c> hoisted first.
    /// </summary>
    public static string EntriesToJsonl(IEnumerable<SessionStoreEntry> entries)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var e in entries)
        {
            if (!first) sb.Append('\n');
            first = false;
            WritePythonJson(sb, SessionSummary.EntryToJsonObject(e));
        }
        return sb.Append('\n').ToString();
    }

    /// <summary>Compact, ASCII-only JSON like Python <c>json.dumps(obj, separators=(",", ":"))</c>.</summary>
    public static string ToPythonJson(JsonNode? node)
    {
        var sb = new StringBuilder();
        WritePythonJson(sb, node);
        return sb.ToString();
    }

    private static void WritePythonJson(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var firstProp = true;
                foreach (var (k, v) in obj)
                {
                    if (!firstProp) sb.Append(',');
                    firstProp = false;
                    WritePythonString(sb, k);
                    sb.Append(':');
                    WritePythonJson(sb, v);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WritePythonJson(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                var el = val.GetValueKind();
                if (el == JsonValueKind.String && val.TryGetValue<string>(out var s))
                    WritePythonString(sb, s);
                else
                    sb.Append(val.ToJsonString());
                break;
        }
    }

    private static void WritePythonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7F) // ensure_ascii: escape controls and non-ASCII
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ---- small helpers ----------------------------------------------------------------------

    public static JsonObject? TryParseObject(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? StringOf(JsonObject o, string k)
        => o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Python truthiness of a JSON value (null/false/0/""/[]/{} are falsy).</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => v.TryGetValue<double>(out var d) ? d != 0 : true,
            _ => true,
        },
        _ => true,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
