// Claude Agent SDK for .NET — Materialize a SessionStore-backed resume into a temp CLAUDE_CONFIG_DIR.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/_internal/session_resume.py
// (materialize_resume_session @ 122, build_mirror_batcher @ 89,
// apply_materialized_options @ 69)

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claude.AgentSdk.Sessions;

/// <summary>Result of <see cref="SessionResume.MaterializeResumeSessionAsync(ClaudeAgentOptions, CancellationToken)"/>.</summary>
/// <remarks>
/// <para><c>ConfigDir</c> is a temporary directory laid out like
/// <c>~/.claude</c>. Point the subprocess at it via
/// <c>CLAUDE_CONFIG_DIR</c>.</para>
/// <para><c>ResumeSessionId</c> should be passed as <c>--resume</c>.</para>
/// <para><c>CleanupAsync</c> removes <c>ConfigDir</c> (best-effort) and must
/// be called after the subprocess exits.</para>
/// </remarks>
public sealed record MaterializedResume(
    string ConfigDir,
    string ResumeSessionId,
    Func<CancellationToken, Task> CleanupAsync);

/// <summary>
/// Session-resume helpers: materialize a session from a
/// <see cref="ISessionStore"/> to a temporary <c>CLAUDE_CONFIG_DIR</c> so the
/// CLI subprocess (which only knows how to resume from a local file) can pick
/// it up. Mirrors Python <c>session_resume</c>.
/// </summary>
public static class SessionResume
{
    /// <summary>Default load timeout (mirrors Python's
    /// <c>ClaudeAgentOptions.load_timeout_ms = 60_000</c>).</summary>
    public static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromMilliseconds(60_000);

    /// <summary>Default macOS Keychain service name for OAuth credentials when
    /// <c>CLAUDE_CONFIG_DIR</c> is unset (Python <c>_KEYCHAIN_SERVICE_NAME</c>).</summary>
    private const string KeychainServiceName = "Claude Code-credentials";

    /// <summary>User-settings keys stripped for the redirected config dir
    /// (Python <c>_RESUME_SETTINGS_STRIPPED_KEYS</c>).</summary>
    private static readonly string[] ResumeSettingsStrippedKeys = { "enabledPlugins", "extraKnownMarketplaces" };

    /// <summary>Test seam: replaces the macOS Keychain lookup. Returns the
    /// credentials JSON or <c>null</c>.</summary>
    internal static Func<string?> KeychainReader { get; set; } = ReadKeychainCredentials;

    /// <summary>Test seam: overrides the user's home directory used to locate
    /// <c>~/.claude</c> and <c>~/.claude.json</c>.</summary>
    internal static Func<string> HomeDirectory { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Construct the <see cref="TranscriptMirrorBatcher"/> for a session.
    /// <paramref name="flushMode"/> <see cref="SessionStoreFlushMode.Eager"/>
    /// zeroes the batcher thresholds so every enqueued frame schedules a
    /// background flush.
    /// </summary>
    public static TranscriptMirrorBatcher BuildMirrorBatcher(
        ISessionStore store,
        MaterializedResume? materialized,
        IReadOnlyDictionary<string, string>? env,
        MirrorErrorCallback onError,
        SessionStoreFlushMode flushMode = SessionStoreFlushMode.Batched)
    {
        var projectsDir = materialized is not null
            ? Path.Combine(materialized.ConfigDir, "projects")
            : SessionPaths.GetProjectsDir(env);

        var eager = flushMode == SessionStoreFlushMode.Eager;
        return new TranscriptMirrorBatcher(store, projectsDir, onError)
        {
            MaxPendingEntries = eager ? 0 : TranscriptMirrorBatcher.DefaultMaxPendingEntries,
            MaxPendingBytes = eager ? 0 : TranscriptMirrorBatcher.DefaultMaxPendingBytes,
        };
    }

    /// <summary>
    /// Load a session from <paramref name="options"/>.SessionStore and write
    /// it to a temp dir. Returns <c>null</c> when no materialization is
    /// needed. Mirrors Python <c>materialize_resume_session</c>.
    /// </summary>
    public static Task<MaterializedResume?> MaterializeResumeSessionAsync(
        ClaudeAgentOptions options,
        CancellationToken cancellationToken = default)
        => MaterializeResumeSessionAsync(options, DefaultLoadTimeout, cancellationToken);

    /// <summary>
    /// Overload taking an explicit per-call store timeout (Python
    /// <c>options.load_timeout_ms</c>) applied to each
    /// <c>LoadAsync</c> / <c>ListSessionsAsync</c> / <c>ListSubkeysAsync</c>.
    /// Zero or a negative span is an immediate timeout (Python: <c>load_timeout_ms &lt;= 0</c>):
    /// the first store call fails with <see cref="SessionStoreOperationException"/>.
    /// </summary>
    public static async Task<MaterializedResume?> MaterializeResumeSessionAsync(
        ClaudeAgentOptions options,
        TimeSpan loadTimeout,
        CancellationToken cancellationToken = default)
    {
        var store = options.SessionStore;
        if (store is null) return null;
        if (options.Resume is null && !options.ContinueConversation) return null;
        var timeout = loadTimeout;
        var projectKey = SessionPaths.ProjectKeyForDirectory(options.Cwd);

        (string SessionId, IReadOnlyList<SessionStoreEntry> Entries)? resolved;
        if (options.Resume is not null)
        {
            if (!SessionPaths.ValidateUuid(options.Resume)) return null;
            resolved = await LoadCandidateAsync(store, projectKey, options.Resume, timeout, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            resolved = await ResolveContinueCandidateAsync(store, projectKey, timeout, cancellationToken).ConfigureAwait(false);
        }
        if (resolved is null) return null;

        var sessionId = resolved.Value.SessionId;
        var entries = resolved.Value.Entries;

        // Python tempfile.mkdtemp: unique, created 0700 on Unix.
        var tmpBase = Directory.CreateTempSubdirectory("claude-resume-").FullName;
        try
        {
            var projectDir = Path.Combine(tmpBase, "projects", projectKey);
            FileSessionStore.CreatePrivateDirectory(projectDir);
            WriteJsonl(Path.Combine(projectDir, sessionId + ".jsonl"), entries);

            // The subprocess will run with CLAUDE_CONFIG_DIR=tmpBase. Copy auth
            // config from the caller's effective config locations so it can
            // authenticate. Missing files are fine (API-key auth, etc.).
            CopyAuthFiles(tmpBase, options.Env);

            // Materialize subagent transcripts if the store can enumerate them.
            if (SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSubkeysAsync)))
            {
                await MaterializeSubkeysAsync(store, projectDir, projectKey, sessionId, timeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await RmTreeWithRetryAsync(tmpBase).ConfigureAwait(false);
            throw;
        }

        Func<CancellationToken, Task> cleanup = ct => RmTreeWithRetryAsync(tmpBase, ct);
        return new MaterializedResume(tmpBase, sessionId, cleanup);
    }

    private static async Task<(string SessionId, IReadOnlyList<SessionStoreEntry> Entries)?> LoadCandidateAsync(
        ISessionStore store, string projectKey, string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var entries = await TaskCompat.WithTimeoutAsync(
            t => store.LoadAsync(new SessionKey { ProjectKey = projectKey, SessionId = sessionId }, t),
            timeout,
            $"SessionStore.LoadAsync() for session {sessionId}",
            ct).ConfigureAwait(false);
        if (entries is null || entries.Count == 0) return null;
        return (sessionId, entries);
    }

    private static async Task<(string SessionId, IReadOnlyList<SessionStoreEntry> Entries)?> ResolveContinueCandidateAsync(
        ISessionStore store, string projectKey, TimeSpan timeout, CancellationToken ct)
    {
        var sessions = await TaskCompat.WithTimeoutAsync(
            t => store.ListSessionsAsync(projectKey, t),
            timeout,
            "SessionStore.ListSessionsAsync()",
            ct).ConfigureAwait(false);
        if (sessions.Count == 0) return null;
        foreach (var cand in sessions.OrderByDescending(s => s.Mtime))
        {
            if (!SessionPaths.ValidateUuid(cand.SessionId)) continue;
            var loaded = await LoadCandidateAsync(store, projectKey, cand.SessionId, timeout, ct).ConfigureAwait(false);
            if (loaded is null) continue;
            // Skip sidechains.
            var firstObj = SessionSummary.EntryToJsonObject(loaded.Value.Entries[0]);
            if (firstObj.TryGetPropertyValue("isSidechain", out var s)
                && s is JsonValue jv && jv.TryGetValue<bool>(out var b) && b)
                continue;
            return loaded;
        }
        return null;
    }

    private static async Task MaterializeSubkeysAsync(
        ISessionStore store,
        string projectDir,
        string projectKey,
        string sessionId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sessionSubDir = Path.Combine(projectDir, sessionId);
        var subkeys = await TaskCompat.WithTimeoutAsync(
            t => store.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, sessionId), t),
            timeout,
            $"SessionStore.ListSubkeysAsync() for session {sessionId}",
            ct).ConfigureAwait(false);

        foreach (var subpath in subkeys)
        {
            if (!IsSafeSubpath(subpath, sessionSubDir)) continue;

            var subKey = new SessionKey { ProjectKey = projectKey, SessionId = sessionId, Subpath = subpath };
            var subEntries = await TaskCompat.WithTimeoutAsync(
                t => store.LoadAsync(subKey, t),
                timeout,
                $"SessionStore.LoadAsync() for session {sessionId} subpath {subpath}",
                ct).ConfigureAwait(false);
            if (subEntries is null || subEntries.Count == 0) continue;

            var metadata = new List<JsonObject>();
            var transcript = new List<SessionStoreEntry>();
            foreach (var e in subEntries)
            {
                if (e.Type == "agent_metadata") metadata.Add(SessionSummary.EntryToJsonObject(e));
                else transcript.Add(e);
            }

            var subOsPath = subpath.Replace('/', Path.DirectorySeparatorChar);
            var subFile = Path.Combine(sessionSubDir, subOsPath + ".jsonl");
            if (transcript.Count > 0) WriteJsonl(subFile, transcript);
            if (metadata.Count > 0)
            {
                var last = metadata[^1];
                last.Remove("type");
                var metaPath = Path.Combine(Path.GetDirectoryName(subFile)!,
                    Path.GetFileNameWithoutExtension(subFile) + ".meta.json");
                FileSessionStore.CreatePrivateDirectory(Path.GetDirectoryName(metaPath)!);
                WritePrivateFile(metaPath, Encoding.UTF8.GetBytes(last.ToJsonString()));
            }
        }
    }

    private static bool IsSafeSubpath(string subpath, string sessionDir)
    {
        if (string.IsNullOrEmpty(subpath)) return false;
        if (subpath.Contains('\0')) return false;
        if (subpath.StartsWith('/') || subpath.StartsWith('\\')) return false;
        if (Path.IsPathRooted(subpath)) return false;
        // Reject drive-prefixed (C:foo) regardless of host OS.
        if (subpath.Length >= 2 && char.IsLetter(subpath[0]) && subpath[1] == ':') return false;
        foreach (var p in subpath.Split('/', '\\'))
        {
            if (p == "." || p == "..") return false;
        }
        try
        {
            var target = Path.GetFullPath(Path.Combine(sessionDir, subpath) + ".jsonl");
            var root = Path.GetFullPath(sessionDir);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && target != root)
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteJsonl(string path, IReadOnlyList<SessionStoreEntry> entries)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) FileSessionStore.CreatePrivateDirectory(dir);
        using var fs = FileSessionStore.OpenPrivateFile(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(fs, new UTF8Encoding(false));
        foreach (var e in entries)
        {
            sw.Write(SessionSummary.EntryToJsonObject(e).ToJsonString());
            sw.Write('\n');
        }
        sw.Flush();
        EnsurePrivateMode(path);
    }

    /// <summary>Write <paramref name="content"/> to <paramref name="path"/>
    /// with mode 0600 on Unix (Python writes then chmods 0o600).</summary>
    private static void WritePrivateFile(string path, byte[] content)
    {
        using (var fs = FileSessionStore.OpenPrivateFile(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(content, 0, content.Length);
        }
        EnsurePrivateMode(path);
    }

    /// <summary>UnixCreateMode only applies to newly created files; chmod
    /// covers a pre-existing target (Python <c>path.chmod(0o600)</c>).</summary>
    private static void EnsurePrivateMode(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort, like Python's suppress(OSError) */ }
    }

    // ---- Auth / user-config seeding (Python _copy_auth_files) -------------

    /// <summary>
    /// Seed <paramref name="tmpBase"/> with the caller's auth and user config:
    /// <c>.credentials.json</c> (refreshToken redacted), <c>.claude.json</c>,
    /// and user <c>settings.json</c> / <c>cowork_settings.json</c> (plugin
    /// declarations stripped). Mirrors Python <c>_copy_auth_files</c>.
    /// </summary>
    internal static void CopyAuthFiles(string tmpBase, IReadOnlyDictionary<string, string>? optEnv)
    {
        string? Opt(string name)
            => optEnv is not null && optEnv.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        string? Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        var callerConfigDir = Opt("CLAUDE_CONFIG_DIR") ?? Env("CLAUDE_CONFIG_DIR");
        var home = HomeDirectory();
        var sourceConfigDir = callerConfigDir ?? Path.Combine(home, ".claude");

        var credsBytes = ReadIfPresent(Path.Combine(sourceConfigDir, ".credentials.json"));
        string? credsJson = credsBytes is null ? null : Encoding.UTF8.GetString(credsBytes);

        // macOS default setup keeps OAuth tokens in the Keychain. Redirecting
        // CLAUDE_CONFIG_DIR changes the Keychain service-name suffix, so the
        // subprocess falls back to ${tmpBase}/.credentials.json. Skipped when
        // env-based auth or a custom config dir is already in play.
        if (callerConfigDir is null
            && (Opt("ANTHROPIC_API_KEY") ?? Env("ANTHROPIC_API_KEY")) is null
            && (Opt("CLAUDE_CODE_OAUTH_TOKEN") ?? Env("CLAUDE_CODE_OAUTH_TOKEN")) is null)
        {
            string? keychain = null;
            try { keychain = KeychainReader(); } catch { /* best-effort */ }
            if (keychain is not null) credsJson = keychain;
        }

        WriteRedactedCredentials(credsJson, Path.Combine(tmpBase, ".credentials.json"));

        var claudeJsonSrc = callerConfigDir is not null
            ? Path.Combine(callerConfigDir, ".claude.json")
            : Path.Combine(home, ".claude.json");
        CopyIfPresent(claudeJsonSrc, Path.Combine(tmpBase, ".claude.json"));

        foreach (var name in new[] { "settings.json", "cowork_settings.json" })
            CopyIfPresent(Path.Combine(sourceConfigDir, name), Path.Combine(tmpBase, name), StripSettingsForResume);
    }

    /// <summary>Drop settings keys that misbehave under a redirected config
    /// dir (Python <c>_strip_settings_for_resume</c>). Content that doesn't
    /// parse as a JSON object is returned untouched.</summary>
    internal static byte[] StripSettingsForResume(byte[] content)
    {
        JsonObject? parsed;
        try
        {
            // utf-8-sig: tolerate a UTF-8 BOM (PowerShell writes one).
            var span = content.AsSpan();
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
            parsed = JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return content;
        }
        if (parsed is null) return content;

        var stripped = false;
        foreach (var key in ResumeSettingsStrippedKeys)
            stripped |= parsed.Remove(key);
        if (parsed["env"] is JsonObject envBlock && envBlock.Remove("CLAUDE_CONFIG_DIR"))
            stripped = true;
        if (!stripped) return content;
        try
        {
            return Encoding.UTF8.GetBytes(parsed.ToJsonString());
        }
        catch
        {
            return content;
        }
    }

    /// <summary>Write <paramref name="credsJson"/> with
    /// <c>claudeAiOauth.refreshToken</c> removed so the resumed subprocess
    /// can't consume the parent's single-use refresh token (Python
    /// <c>_write_redacted_credentials</c>).</summary>
    internal static void WriteRedactedCredentials(string? credsJson, string dst)
    {
        if (credsJson is null) return;
        var output = credsJson;
        try
        {
            if (JsonNode.Parse(credsJson) is JsonObject data
                && data["claudeAiOauth"] is JsonObject oauth
                && oauth.Remove("refreshToken"))
            {
                output = data.ToJsonString();
            }
        }
        catch
        {
            // Unparseable — write through; subprocess will fail to parse it too.
        }
        WritePrivateFile(dst, new UTF8Encoding(false).GetBytes(output));
    }

    /// <summary>Read a regular file, or return <c>null</c> when missing or
    /// unreadable (best-effort; never aborts the resume). Mirrors Python
    /// <c>_read_if_present</c>, which refuses anything that is not a regular file
    /// (a directory, or a FIFO/device where a file was expected) so an unreadable
    /// one cannot abort -- or, for a FIFO, hang -- the resume.</summary>
    internal static byte[]? ReadIfPresent(string src)
    {
        try
        {
            var info = new FileInfo(src);
            if (!info.Exists) return null; // missing, or a directory
            if (!IsRegularFile(info)) return null;
            return File.ReadAllBytes(src);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort <c>S_ISREG</c>: .NET exposes no file-type bits, and opening a FIFO for
    /// reading blocks until a writer appears, so special files must be rejected before
    /// opening. FIFOs, sockets and character/block devices all stat with size 0 (a pipe's
    /// size is never meaningful), so anything empty is skipped: an empty config file
    /// carries nothing to copy, and skipping it is the only way to be sure not to hang.
    /// </summary>
    private static bool IsRegularFile(FileInfo info)
    {
        if ((info.Attributes & FileAttributes.Directory) != 0) return false;
        if (OperatingSystem.IsWindows()) return (info.Attributes & FileAttributes.Device) == 0;
        return info.Length > 0;
    }

    private static void CopyIfPresent(string src, string dst, Func<byte[], byte[]>? transform = null)
    {
        var content = ReadIfPresent(src);
        if (content is null) return;
        try
        {
            WritePrivateFile(dst, transform is null ? content : transform(content));
        }
        catch
        {
            // Don't leave a truncated dst behind for the subprocess to misparse.
            try { File.Delete(dst); } catch { /* ignore */ }
        }
    }

    /// <summary>Read OAuth credentials JSON from the macOS Keychain (default
    /// service name). Best-effort — <c>null</c> on any error or non-macOS.</summary>
    private static string? ReadKeychainCredentials()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        string user;
        try
        {
            user = Environment.GetEnvironmentVariable("USER") is { Length: > 0 } u ? u : Environment.UserName;
        }
        catch
        {
            user = "claude-code-user";
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "find-generic-password", "-a", user, "-w", "-s", KeychainServiceName })
                psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            if (proc.ExitCode != 0) return null;
            var output = stdoutTask.GetAwaiter().GetResult().Trim();
            return output.Length == 0 ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static async Task RmTreeWithRetryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return;
        const int retries = 4;
        var delay = TimeSpan.FromMilliseconds(100);
        for (int i = 0; i < retries; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        try { Directory.Delete(path, recursive: true); } catch { /* ignore */ }
    }
}
