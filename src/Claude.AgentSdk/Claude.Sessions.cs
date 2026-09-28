// Claude Agent SDK for .NET — Top-level session API surface.
// Reference: reference/claude-agent-sdk-python/src/claude_agent_sdk/__init__.py
// (re-exports list_sessions, get_session_info, get_session_messages, list_subagents,
// get_subagent_messages, rename_session, tag_session, delete_session, fork_session and
// their *_from_store / *_via_store variants, import_session_to_store, etc.)

using System.Text.Json.Nodes;
using Claude.AgentSdk.Sessions;

namespace Claude.AgentSdk;

/// <summary>
/// Top-level session API. Mirrors the free functions Python re-exports from
/// <c>claude_agent_sdk</c>: the synchronous disk readers/mutators over
/// <c>~/.claude/projects</c> (<c>list_sessions</c>, <c>get_session_info</c>,
/// <c>get_session_messages</c>, <c>list_subagents</c>, <c>get_subagent_messages</c>,
/// <c>rename_session</c>, <c>tag_session</c>, <c>delete_session</c>, <c>fork_session</c>)
/// and their <see cref="ISessionStore"/>-backed async variants.
/// </summary>
/// <remarks>
/// Error semantics follow Python per function: readers return empty/null for an invalid
/// session id or a missing session; mutators throw (<see cref="InvalidSessionIdException"/>
/// for an invalid id, <see cref="SessionNotFoundException"/> for a missing session).
/// </remarks>
public static class ClaudeSessions
{
    // ---- disk-backed (Python sessions.py / session_mutations.py) ----------------------------

    /// <summary>
    /// List sessions with metadata from stat + head/tail reads. With
    /// <paramref name="directory"/>, lists that project (and, when
    /// <paramref name="includeWorktrees"/>, its git worktrees); otherwise all projects.
    /// Sorted by <see cref="SDKSessionInfo.LastModified"/> descending. Mirrors Python
    /// <c>list_sessions</c>. <paramref name="includeProgrammatic"/> false (TS-only)
    /// hides programmatic sessions (SDK entrypoints, daemon workers), as terminal
    /// <c>/resume</c> does.
    /// </summary>
    public static IReadOnlyList<SDKSessionInfo> ListSessions(
        string? directory = null,
        int? limit = null,
        int offset = 0,
        bool includeWorktrees = true,
        bool includeProgrammatic = true)
        => SessionTranscripts.ListSessions(directory, limit, offset, includeWorktrees, includeProgrammatic);

    /// <summary>
    /// Metadata for one session, or null if the id is invalid, the session is not found,
    /// is a sidechain, or has no extractable summary. Mirrors Python <c>get_session_info</c>.
    /// </summary>
    public static SDKSessionInfo? GetSessionInfo(string sessionId, string? directory = null)
        => SessionTranscripts.GetSessionInfo(sessionId, directory);

    /// <summary>
    /// A session's user/assistant messages in chronological order (conversation chain rebuilt
    /// via <c>parentUuid</c>). Empty if the id is invalid or the session is not found. Mirrors
    /// Python <c>get_session_messages</c>. <paramref name="includeSystemMessages"/> (TS-only)
    /// also returns system entries (compact boundaries, notices) with type <c>"system"</c>.
    /// </summary>
    public static IReadOnlyList<SessionMessage> GetSessionMessages(
        string sessionId,
        string? directory = null,
        int? limit = null,
        int offset = 0,
        bool includeSystemMessages = false)
        => SessionTranscripts.GetSessionMessages(sessionId, directory, limit, offset, includeSystemMessages);

    /// <summary>
    /// Subagent ids of a session (from <c>&lt;session&gt;/subagents/**/agent-&lt;id&gt;.jsonl</c>).
    /// Empty if the id is invalid or the session is not found. Mirrors Python <c>list_subagents</c>.
    /// </summary>
    public static IReadOnlyList<string> ListSubagents(string sessionId, string? directory = null)
        => SessionTranscripts.ListSubagents(sessionId, directory);

    /// <summary>
    /// A subagent's messages, each carrying <see cref="SessionMessage.ParentToolUseId"/> /
    /// <see cref="SessionMessage.ParentAgentId"/> from its <c>.meta.json</c> sidecar. Empty if
    /// the session or subagent is not found. Mirrors Python <c>get_subagent_messages</c>.
    /// </summary>
    public static IReadOnlyList<SessionMessage> GetSubagentMessages(
        string sessionId,
        string agentId,
        string? directory = null,
        int? limit = null,
        int offset = 0)
        => SessionTranscripts.GetSubagentMessages(sessionId, agentId, directory, limit, offset);

    /// <summary>Rename a session on disk (Python <c>rename_session</c>).</summary>
    /// <exception cref="InvalidSessionIdException">The id is not a UUID.</exception>
    /// <exception cref="ArgumentException">The title is empty after trimming.</exception>
    /// <exception cref="SessionNotFoundException">The session file cannot be found.</exception>
    public static void RenameSession(string sessionId, string title, string? directory = null)
        => SessionMutations.RenameSession(sessionId, title, directory);

    /// <summary>Tag a session on disk; null clears the tag (Python <c>tag_session</c>).</summary>
    /// <exception cref="InvalidSessionIdException">The id is not a UUID.</exception>
    /// <exception cref="ArgumentException">The tag is empty after sanitization.</exception>
    /// <exception cref="SessionNotFoundException">The session file cannot be found.</exception>
    public static void TagSession(string sessionId, string? tag, string? directory = null)
        => SessionMutations.TagSession(sessionId, tag, directory);

    /// <summary>Hard-delete a session file and its subagent directory (Python <c>delete_session</c>).</summary>
    /// <exception cref="InvalidSessionIdException">The id is not a UUID.</exception>
    /// <exception cref="SessionNotFoundException">The session file cannot be found.</exception>
    public static void DeleteSession(string sessionId, string? directory = null)
        => SessionMutations.DeleteSession(sessionId, directory);

    /// <summary>Fork a session on disk with fresh UUIDs (Python <c>fork_session</c>).</summary>
    /// <exception cref="InvalidSessionIdException">An id is not a UUID.</exception>
    /// <exception cref="SessionNotFoundException">The source session file cannot be found.</exception>
    /// <exception cref="InvalidOperationException">Nothing to fork, or <paramref name="upToMessageId"/> not in the transcript.</exception>
    public static ForkSessionResult ForkSession(
        string sessionId,
        string? directory = null,
        string? upToMessageId = null,
        string? title = null)
        => SessionMutations.ForkSession(sessionId, directory, upToMessageId, title);

    // ---- SessionStore-backed ------------------------------------------------------------------

    /// <summary>
    /// List sessions for the current project (or <paramref name="directory"/>)
    /// from a <see cref="ISessionStore"/>. Mirrors Python
    /// <c>list_sessions_from_store</c>: uses the store's summaries when available
    /// (gap-filling sessions whose summary is missing or stale via
    /// <see cref="ISessionStore.ListSessionsAsync"/> + load), otherwise loads each session.
    /// </summary>
    /// <exception cref="NotSupportedException">The store implements neither listing method.</exception>
    public static async Task<IReadOnlyList<SDKSessionInfo>> ListSessionsAsync(
        ISessionStore store,
        string? directory = null,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var projectPath = SessionPaths.CanonicalizePath(directory ?? ".");
        var projectKey = SessionPaths.SanitizePath(projectPath);
        var hasListSessions = SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSessionsAsync));

        if (SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSessionSummariesAsync)))
        {
            IReadOnlyList<SessionSummaryEntry>? summaries;
            try
            {
                summaries = await store.ListSessionSummariesAsync(projectKey, cancellationToken).ConfigureAwait(false);
            }
            catch (NotImplementedException)
            {
                summaries = null;
            }

            if (summaries is not null)
            {
                IReadOnlyList<SessionStoreListEntry> listing = hasListSessions
                    ? (await store.ListSessionsAsync(projectKey, cancellationToken).ConfigureAwait(false)).ToList()
                    : Array.Empty<SessionStoreListEntry>();
                var knownMtimes = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var e in listing) knownMtimes[e.SessionId] = e.Mtime;

                // Slots: fresh summaries carry their info; sessions without a (fresh) summary
                // get a placeholder filled by load() after pagination. Summary-backed
                // sidechain/empty sessions are dropped up front so they take no page slots.
                var slots = new List<(long Mtime, string SessionId, SDKSessionInfo? Info)>();
                var freshIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in summaries)
                {
                    if (hasListSessions)
                    {
                        if (!knownMtimes.TryGetValue(s.SessionId, out var known)) continue; // gone
                        if (s.Mtime < known) continue; // stale sidecar: re-fold from source
                    }
                    freshIds.Add(s.SessionId);
                    if (SessionSummary.SummaryEntryToSdkInfo(s, projectPath) is { } info)
                        slots.Add((s.Mtime, s.SessionId, info));
                }
                if (hasListSessions)
                {
                    foreach (var e in listing)
                        if (!freshIds.Contains(e.SessionId)) slots.Add((e.Mtime, e.SessionId, null));
                }

                // Paginate before loading so gap-fill loads are bounded by the page size.
                IEnumerable<(long Mtime, string SessionId, SDKSessionInfo? Info)> pageQuery =
                    slots.OrderByDescending(sl => sl.Mtime);
                if (offset > 0) pageQuery = pageQuery.Skip(offset);
                if (limit is > 0) pageQuery = pageQuery.Take(limit.Value);
                var page = pageQuery.ToList();

                var toFill = page.Where(sl => sl.Info is null)
                    .Select(sl => new SessionStoreListEntry(sl.SessionId, sl.Mtime)).ToList();
                if (toFill.Count > 0)
                {
                    var filled = new Dictionary<string, SDKSessionInfo>(StringComparer.Ordinal);
                    foreach (var f in await DeriveInfosViaLoadAsync(store, toFill, directory, projectPath, cancellationToken).ConfigureAwait(false))
                        filled[f.SessionId] = f;
                    for (var i = 0; i < page.Count; i++)
                    {
                        if (page[i].Info is null)
                            page[i] = page[i] with { Info = filled.GetValueOrDefault(page[i].SessionId) };
                    }
                }
                return page.Where(sl => sl.Info is not null).Select(sl => sl.Info!).ToList();
            }
        }

        if (!hasListSessions)
            throw new NotSupportedException(
                "session_store implements neither ListSessionSummariesAsync() nor ListSessionsAsync() -- " +
                "cannot list sessions. Provide a store with at least one of those methods.");

        var all = (await store.ListSessionsAsync(projectKey, cancellationToken).ConfigureAwait(false)).ToList();
        var results = await DeriveInfosViaLoadAsync(store, all, directory, projectPath, cancellationToken).ConfigureAwait(false);
        return SessionTranscripts.ApplySortLimitOffset(results, limit, offset);
    }

    /// <summary>
    /// Per-session load + lite-parse, bounded at 16 concurrent loads; an adapter error
    /// degrades that row to an empty summary. Python <c>_derive_infos_via_load</c>.
    /// </summary>
    private static async Task<List<SDKSessionInfo>> DeriveInfosViaLoadAsync(
        ISessionStore store,
        IReadOnlyList<SessionStoreListEntry> listing,
        string? directory,
        string projectPath,
        CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(SessionTranscripts.StoreListLoadConcurrency);
        var tasks = listing.Select(async entry =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var jsonl = await LoadStoreEntriesAsJsonlAsync(store, entry.SessionId, directory, cancellationToken).ConfigureAwait(false);
                return (Jsonl: jsonl, Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return (Jsonl: (string?)null, Error: ex);
            }
            finally
            {
                limiter.Release();
            }
        }).ToList();
        var settled = await Task.WhenAll(tasks).ConfigureAwait(false);

        var results = new List<SDKSessionInfo>();
        for (var i = 0; i < listing.Count; i++)
        {
            var (sid, mtime) = (listing[i].SessionId, listing[i].Mtime);
            var (jsonl, error) = settled[i];
            if (error is not null)
            {
                results.Add(new SDKSessionInfo { SessionId = sid, Summary = "", LastModified = mtime });
                continue;
            }
            if (jsonl is null) continue;
            var parsed = SessionTranscripts.ParseSessionInfoFromLite(sid, SessionTranscripts.JsonlToLite(jsonl, mtime), projectPath);
            if (parsed is null) continue; // sidechain / no summary
            results.Add(parsed with { LastModified = mtime });
        }
        return results;
    }

    private static async Task<string?> LoadStoreEntriesAsJsonlAsync(
        ISessionStore store, string sessionId, string? directory, CancellationToken cancellationToken)
    {
        var key = new SessionKey { ProjectKey = SessionPaths.ProjectKeyForDirectory(directory), SessionId = sessionId };
        var entries = await store.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (entries is null || entries.Count == 0) return null;
        return SessionTranscripts.EntriesToJsonl(entries);
    }

    /// <summary>
    /// Metadata for one session from a <see cref="ISessionStore"/>. Returns null if the id is
    /// not a valid UUID, the session is not found, is a sidechain, or has no extractable
    /// summary. Mirrors Python <c>get_session_info_from_store</c>.
    /// </summary>
    public static async Task<SDKSessionInfo?> GetSessionInfoAsync(
        ISessionStore store,
        string sessionId,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return null;
        var jsonl = await LoadStoreEntriesAsJsonlAsync(store, sessionId, directory, cancellationToken).ConfigureAwait(false);
        if (jsonl is null) return null;
        var lite = SessionTranscripts.JsonlToLite(jsonl, SessionTranscripts.MtimeFromJsonlTail(jsonl));
        return SessionTranscripts.ParseSessionInfoFromLite(sessionId, lite, SessionPaths.CanonicalizePath(directory ?? "."));
    }

    /// <summary>
    /// A session's user/assistant messages from a <see cref="ISessionStore"/>, in
    /// chronological order (conversation chain rebuilt via <c>parentUuid</c>). Empty if the
    /// id is invalid or the session is not found. Mirrors Python
    /// <c>get_session_messages_from_store</c>.
    /// </summary>
    public static Task<IReadOnlyList<SessionMessage>> GetSessionMessagesAsync(
        ISessionStore store,
        string sessionId,
        string? directory = null,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
        => GetSessionMessagesAsync(store, sessionId, directory, limit, offset, includeSystemMessages: false, cancellationToken);

    /// <summary>
    /// <see cref="GetSessionMessagesAsync(ISessionStore, string, string?, int?, int, CancellationToken)"/>
    /// with the TS-only <paramref name="includeSystemMessages"/> switch.
    /// </summary>
    public static async Task<IReadOnlyList<SessionMessage>> GetSessionMessagesAsync(
        ISessionStore store,
        string sessionId,
        string? directory,
        int? limit,
        int offset,
        bool includeSystemMessages,
        CancellationToken cancellationToken = default)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return Array.Empty<SessionMessage>();
        var projectKey = SessionPaths.ProjectKeyForDirectory(directory);
        var entries = await store.LoadAsync(new SessionKey { ProjectKey = projectKey, SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        if (entries is null || entries.Count == 0) return Array.Empty<SessionMessage>();
        return SessionTranscripts.EntriesToSessionMessages(
            SessionTranscripts.FilterTranscriptEntries(entries), limit, offset, includeSystemMessages);
    }

    /// <summary>
    /// List subagent IDs for a session in a <see cref="ISessionStore"/>.
    /// Mirrors Python <c>list_subagents_from_store</c>: only sub-keys under
    /// <c>subagents/</c> whose last segment is <c>agent-&lt;id&gt;</c> are
    /// reported, as the bare <c>&lt;id&gt;</c> (deduplicated, first-seen
    /// order). Empty if the id is invalid. Pass the result to
    /// <see cref="GetSubagentMessagesAsync"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The store does not implement <see cref="ISessionStore.ListSubkeysAsync"/>.</exception>
    public static async Task<IReadOnlyList<string>> ListSubagentsAsync(
        ISessionStore store,
        string sessionId,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return Array.Empty<string>();
        if (!SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSubkeysAsync)))
            throw new NotSupportedException(
                "session_store does not implement ListSubkeysAsync() -- cannot list subagents. " +
                "Provide a store with a ListSubkeysAsync() method.");
        var projectKey = SessionPaths.ProjectKeyForDirectory(directory);
        var subkeys = await store.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var subpath in subkeys)
        {
            if (subpath is null || !subpath.StartsWith("subagents/", StringComparison.Ordinal)) continue;
            var last = subpath[(subpath.LastIndexOf('/') + 1)..];
            if (!last.StartsWith("agent-", StringComparison.Ordinal)) continue;
            var agentId = last["agent-".Length..];
            if (seen.Add(agentId)) ids.Add(agentId);
        }
        return ids;
    }

    /// <summary>
    /// Load messages for a single subagent from a <see cref="ISessionStore"/>.
    /// Mirrors Python <c>get_subagent_messages_from_store</c>:
    /// <paramref name="agentId"/> is an id returned by
    /// <see cref="ListSubagentsAsync"/>; the transcript is located at
    /// <c>subagents/agent-&lt;id&gt;</c> or, when the store can enumerate
    /// sub-keys, any nested <c>subagents/**/agent-&lt;id&gt;</c>. Each message carries
    /// <see cref="SessionMessage.ParentToolUseId"/> / <see cref="SessionMessage.ParentAgentId"/>
    /// from the subagent's <c>agent_metadata</c> entry. Returns an empty list when the
    /// session id is invalid or the subagent is not found.
    /// <para>Back-compat: a value containing <c>/</c> is treated as a raw
    /// store sub-key (the pre-parity contract); it must start with
    /// <c>subagents/</c> and contain no empty, <c>.</c> or <c>..</c>
    /// segments.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The raw sub-key is malformed, or the id contains NUL.</exception>
    public static async Task<IReadOnlyList<SessionMessage>> GetSubagentMessagesAsync(
        ISessionStore store,
        string sessionId,
        string agentId,
        string? directory = null,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (!SessionPaths.ValidateUuid(sessionId)) return Array.Empty<SessionMessage>();
        if (string.IsNullOrEmpty(agentId)) return Array.Empty<SessionMessage>();
        if (agentId.Contains('\0'))
            throw new ArgumentException("agentId must not contain NUL", nameof(agentId));

        var projectKey = SessionPaths.ProjectKeyForDirectory(directory);
        string subpath;
        if (agentId.Contains('/') || agentId.Contains('\\'))
        {
            if (!IsValidSubagentSubkey(agentId))
                throw new ArgumentException($"Invalid subagent sub-key: '{agentId}'", nameof(agentId));
            subpath = agentId;
        }
        else
        {
            subpath = $"subagents/agent-{agentId}";
            if (SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSubkeysAsync)))
            {
                var subkeys = await store.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, sessionId), cancellationToken).ConfigureAwait(false);
                var target = "agent-" + agentId;
                var match = subkeys.FirstOrDefault(sk =>
                    sk is not null
                    && sk.StartsWith("subagents/", StringComparison.Ordinal)
                    && sk[(sk.LastIndexOf('/') + 1)..] == target);
                if (match is null) return Array.Empty<SessionMessage>();
                subpath = match;
            }
        }

        var entries = await store.LoadAsync(new SessionKey
        {
            ProjectKey = projectKey,
            SessionId = sessionId,
            Subpath = subpath,
        }, cancellationToken).ConfigureAwait(false);
        if (entries is null || entries.Count == 0) return Array.Empty<SessionMessage>();

        // The synthetic agent_metadata entry (store copy of .meta.json) names the Agent
        // tool_use that spawned this subagent; last one wins (it is rewritten on resume).
        // It is not a transcript line (Python _split_agent_metadata).
        JsonObject? meta = null;
        var transcript = new List<SessionStoreEntry>();
        foreach (var e in entries)
        {
            if (e.Type == "agent_metadata") meta = SessionSummary.EntryToJsonObject(e);
            else transcript.Add(e);
        }
        if (transcript.Count == 0) return Array.Empty<SessionMessage>();
        var (toolUseId, parentAgentId) = SessionTranscripts.ParentIdsFromAgentMetadata(meta);

        return SessionTranscripts.EntriesToSubagentMessages(
            SessionTranscripts.FilterTranscriptEntries(transcript), limit, offset, toolUseId, parentAgentId);
    }

    private static bool IsValidSubagentSubkey(string subkey)
    {
        if (!subkey.StartsWith("subagents/", StringComparison.Ordinal)) return false;
        foreach (var part in subkey.Split('/', '\\'))
        {
            if (part.Length == 0 || part == "." || part == "..") return false;
            if (part.Length >= 2 && char.IsLetter(part[0]) && part[1] == ':') return false;
        }
        return true;
    }

    /// <summary>
    /// Delete a session via <paramref name="store"/>. No-op if the store
    /// does not implement <see cref="ISessionStore.DeleteAsync"/>.
    /// </summary>
    public static Task DeleteSessionAsync(
        ISessionStore store,
        string sessionId,
        string? directory = null,
        CancellationToken cancellationToken = default)
        => SessionMutations.DeleteSessionViaStoreAsync(store, sessionId, directory, cancellationToken);

    /// <summary>Rename a session by appending a custom-title entry.</summary>
    public static Task RenameSessionAsync(
        ISessionStore store,
        string sessionId,
        string title,
        string? directory = null,
        CancellationToken cancellationToken = default)
        => SessionMutations.RenameSessionViaStoreAsync(store, sessionId, title, directory, cancellationToken);

    /// <summary>Tag a session. Pass <c>null</c> to clear.</summary>
    public static Task TagSessionAsync(
        ISessionStore store,
        string sessionId,
        string? tag,
        string? directory = null,
        CancellationToken cancellationToken = default)
        => SessionMutations.TagSessionViaStoreAsync(store, sessionId, tag, directory, cancellationToken);

    /// <summary>
    /// Fork a session into a new session with fresh UUIDs via <paramref name="store"/>.
    /// Mirrors Python <c>fork_session_via_store</c>.
    /// </summary>
    public static Task<ForkSessionResult> ForkSessionAsync(
        ISessionStore store,
        string sessionId,
        string? directory = null,
        string? upToMessageId = null,
        string? title = null,
        CancellationToken cancellationToken = default)
        => SessionMutations.ForkSessionViaStoreAsync(store, sessionId, directory, upToMessageId, title, cancellationToken);

    /// <summary>
    /// Return summaries for all sessions in a project. Mirrors Python
    /// <c>list_session_summaries</c> on the store directly.
    /// </summary>
    public static async Task<IReadOnlyList<SessionSummaryEntry>> ListSessionSummariesAsync(
        ISessionStore store,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        var projectKey = SessionPaths.ProjectKeyForDirectory(directory);
        if (!SessionStoreValidation.StoreImplements(store, nameof(ISessionStore.ListSessionSummariesAsync)))
            return Array.Empty<SessionSummaryEntry>();
        return await store.ListSessionSummariesAsync(projectKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replay a local on-disk session JSONL into a
    /// <see cref="ISessionStore"/>. Mirrors Python <c>import_session_to_store</c>.
    /// </summary>
    public static Task ImportSessionToStoreAsync(
        string sessionId,
        ISessionStore store,
        string? directory = null,
        bool includeSubagents = true,
        int batchSize = TranscriptMirrorBatcher.DefaultMaxPendingEntries,
        CancellationToken cancellationToken = default)
        => SessionImport.ImportSessionToStoreAsync(sessionId, store, directory, includeSubagents, batchSize, cancellationToken);
}
