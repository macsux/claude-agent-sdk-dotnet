// Python-parity tests for the public session API (ClaudeSessions / SessionMutations /
// SessionImport / SessionResume), ported from the Python SDK's
// tests/test_session_helpers_store.py, test_sessions.py, test_session_mutations.py,
// test_session_import.py and test_session_resume.py.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claude.AgentSdk.Sessions;
using Xunit;

namespace Claude.AgentSdk.Tests;

internal static class ParityUtil
{
    public const string Dir = "/workspace/project";
    public static readonly string ProjectKey = SessionPaths.ProjectKeyForDirectory(Dir);

    public static JsonObject User(string text, string uid, string? parent, string sid) => new()
    {
        ["type"] = "user",
        ["uuid"] = uid,
        ["parentUuid"] = parent,
        ["sessionId"] = sid,
        ["timestamp"] = "2024-01-01T00:00:00.000Z",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    };

    public static JsonObject Assistant(string text, string uid, string parent, string sid) => new()
    {
        ["type"] = "assistant",
        ["uuid"] = uid,
        ["parentUuid"] = parent,
        ["sessionId"] = sid,
        ["timestamp"] = "2024-01-01T00:00:01.000Z",
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        },
    };

    public static SessionStoreEntry E(JsonObject o) => SessionSummary.JsonObjectToEntry(o);

    public static List<JsonObject> Chain(string sid, int n = 2)
    {
        var entries = new List<JsonObject>();
        string? parent = null;
        for (var i = 0; i < n; i++)
        {
            var u = Guid.NewGuid().ToString();
            var a = Guid.NewGuid().ToString();
            entries.Add(User($"prompt {i}", u, parent, sid));
            entries.Add(Assistant($"reply {i}", a, u, sid));
            parent = a;
        }
        return entries;
    }

    public static async Task<List<string>> SeedChain(ISessionStore store, string sid, int n = 2, string? projectKey = null)
    {
        var chain = Chain(sid, n);
        await store.AppendAsync(new SessionKey { ProjectKey = projectKey ?? ProjectKey, SessionId = sid }, chain.Select(E).ToList());
        return chain.Select(e => (string)e["uuid"]!).ToList();
    }

    public static string Text(SessionMessage m)
    {
        var content = m.MessageData.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
    }
}

/// <summary>Store implementing only the required Append/Load (Python _MinimalStore).</summary>
internal sealed class MinimalStore : ISessionStore
{
    private readonly Dictionary<string, List<SessionStoreEntry>> _data = new();
    private static string K(SessionKey key) => $"{key.ProjectKey}/{key.SessionId}/{key.Subpath}";

    public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
    {
        if (!_data.TryGetValue(K(key), out var l)) _data[K(key)] = l = new();
        l.AddRange(entries);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(_data.TryGetValue(K(key), out var l) ? l.ToList() : null);
}

public sealed class SessionStoreReadParityTests
{
    private const string Dir = ParityUtil.Dir;

    [Fact]
    public async Task InvalidSessionId_ReadersReturnEmptyOrNull_MutatorsThrow()
    {
        var store = new InMemorySessionStore();
        Assert.Null(await ClaudeSessions.GetSessionInfoAsync(store, "not-a-uuid", Dir));
        Assert.Empty(await ClaudeSessions.GetSessionMessagesAsync(store, "not-a-uuid", Dir));
        Assert.Empty(await ClaudeSessions.ListSubagentsAsync(store, "not-a-uuid", Dir));
        Assert.Empty(await ClaudeSessions.GetSubagentMessagesAsync(store, "not-a-uuid", "abc", Dir));
        // Validation runs before the list_subkeys capability check (Python order).
        Assert.Empty(await ClaudeSessions.ListSubagentsAsync(new MinimalStore(), "not-a-uuid", Dir));

        await Assert.ThrowsAsync<InvalidSessionIdException>(() => ClaudeSessions.RenameSessionAsync(store, "bad", "t", Dir));
        await Assert.ThrowsAsync<InvalidSessionIdException>(() => ClaudeSessions.TagSessionAsync(store, "bad", "t", Dir));
        await Assert.ThrowsAsync<InvalidSessionIdException>(() => ClaudeSessions.DeleteSessionAsync(store, "bad", Dir));
        await Assert.ThrowsAsync<InvalidSessionIdException>(() => ClaudeSessions.ForkSessionAsync(store, "bad", Dir));
        var sid = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, sid);
        var ex = await Assert.ThrowsAsync<InvalidSessionIdException>(() => ClaudeSessions.ForkSessionAsync(store, sid, Dir, upToMessageId: "nope"));
        Assert.Equal("Invalid up_to_message_id: nope", ex.Message);
    }

    [Fact]
    public async Task UnknownSession_MessagesEmpty_InfoNull()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        Assert.Empty(await ClaudeSessions.GetSessionMessagesAsync(store, sid, Dir));
        Assert.Null(await ClaudeSessions.GetSessionInfoAsync(store, sid, Dir));
    }

    [Fact]
    public async Task ListSubagents_ThrowsWhenStoreLacksListSubkeys()
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => ClaudeSessions.ListSubagentsAsync(new MinimalStore(), Guid.NewGuid().ToString(), Dir));
        Assert.Contains("ListSubkeysAsync", ex.Message);
    }

    [Fact]
    public async Task GetSessionMessages_ReturnsChainInOrder_IgnoresMetadata_Pages()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        var uuids = await ParityUtil.SeedChain(store, sid, 3);
        await store.AppendAsync(new SessionKey { ProjectKey = ParityUtil.ProjectKey, SessionId = sid },
        [
            ParityUtil.E(new JsonObject { ["type"] = "custom-title", ["customTitle"] = "x", ["sessionId"] = sid }),
            ParityUtil.E(new JsonObject { ["type"] = "tag", ["tag"] = "t", ["sessionId"] = sid, ["uuid"] = Guid.NewGuid().ToString() }),
        ]);

        var messages = await ClaudeSessions.GetSessionMessagesAsync(store, sid, Dir);
        Assert.Equal(uuids, messages.Select(m => m.Uuid));
        Assert.Equal(["user", "assistant", "user", "assistant", "user", "assistant"], messages.Select(m => m.Type));
        Assert.All(messages, m => Assert.Equal(sid, m.SessionId));
        Assert.All(messages, m => Assert.Null(m.ParentToolUseId));
        Assert.All(messages, m => Assert.Null(m.ParentAgentId));

        var page = await ClaudeSessions.GetSessionMessagesAsync(store, sid, Dir, limit: 2, offset: 1);
        Assert.Equal(uuids.Skip(1).Take(2), page.Select(m => m.Uuid));
        var tail = await ClaudeSessions.GetSessionMessagesAsync(store, sid, Dir, offset: 4);
        Assert.Equal(uuids.Skip(4), tail.Select(m => m.Uuid));
    }

    [Fact]
    public async Task GetSessionMessages_FollowsLatestMainBranch_SkipsSidechainAndMeta()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        var root = ParityUtil.User("root", "11111111-0000-0000-0000-000000000001", null, sid);
        var a1 = ParityUtil.Assistant("old branch", "11111111-0000-0000-0000-000000000002", "11111111-0000-0000-0000-000000000001", sid);
        var a2 = ParityUtil.Assistant("new branch", "11111111-0000-0000-0000-000000000003", "11111111-0000-0000-0000-000000000001", sid);
        var side = ParityUtil.User("side", "11111111-0000-0000-0000-000000000004", "11111111-0000-0000-0000-000000000001", sid);
        side["isSidechain"] = true;
        var meta = ParityUtil.User("meta", "11111111-0000-0000-0000-000000000005", "11111111-0000-0000-0000-000000000001", sid);
        meta["isMeta"] = true;
        await store.AppendAsync(new SessionKey { ProjectKey = ParityUtil.ProjectKey, SessionId = sid },
            new[] { root, a1, a2, side, meta }.Select(ParityUtil.E).ToList());

        var messages = await ClaudeSessions.GetSessionMessagesAsync(store, sid, Dir);
        Assert.Equal(["root", "new branch"], messages.Select(ParityUtil.Text));
    }

    [Fact]
    public async Task Subagents_ListAndGet_WithParentIdsFromAgentMetadata()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, sid, 1);
        var subKey = new SessionKey { ProjectKey = ParityUtil.ProjectKey, SessionId = sid, Subpath = "subagents/workflows/run-1/agent-abc" };
        var chain = ParityUtil.Chain(sid, 1);
        await store.AppendAsync(subKey, chain.Select(ParityUtil.E).ToList());
        await store.AppendAsync(subKey, [ParityUtil.E(new JsonObject { ["type"] = "agent_metadata", ["toolUseId"] = "toolu_old" })]);
        await store.AppendAsync(subKey, [ParityUtil.E(new JsonObject
        {
            ["type"] = "agent_metadata", ["toolUseId"] = "toolu_1", ["parentAgentId"] = "parent-agent",
        })]);

        Assert.Equal(["abc"], await ClaudeSessions.ListSubagentsAsync(store, sid, Dir));
        var messages = await ClaudeSessions.GetSubagentMessagesAsync(store, sid, "abc", Dir);
        Assert.Equal(chain.Select(e => (string)e["uuid"]!), messages.Select(m => m.Uuid));
        Assert.All(messages, m => Assert.Equal("toolu_1", m.ParentToolUseId));
        Assert.All(messages, m => Assert.Equal("parent-agent", m.ParentAgentId));

        var paged = await ClaudeSessions.GetSubagentMessagesAsync(store, sid, "abc", Dir, limit: 1, offset: 1);
        Assert.Equal((string)chain[1]["uuid"]!, Assert.Single(paged).Uuid);
    }

    [Fact]
    public async Task SubagentParentIds_IgnoreNonStringMetadata()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        var subKey = new SessionKey { ProjectKey = ParityUtil.ProjectKey, SessionId = sid, Subpath = "subagents/agent-x" };
        await store.AppendAsync(subKey, ParityUtil.Chain(sid, 1).Select(ParityUtil.E).ToList());
        await store.AppendAsync(subKey, [ParityUtil.E(new JsonObject { ["type"] = "agent_metadata", ["toolUseId"] = 5, ["parentAgentId"] = new JsonArray() })]);

        var messages = await ClaudeSessions.GetSubagentMessagesAsync(store, sid, "x", Dir);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Null(m.ParentToolUseId));
        Assert.All(messages, m => Assert.Null(m.ParentAgentId));
    }

    [Fact]
    public async Task ListSessions_ThrowsWhenStoreLacksListing_LoadsViaFallback()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => ClaudeSessions.ListSessionsAsync(new MinimalStore(), Dir));

        var store = new InMemorySessionStore();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, a);
        await ParityUtil.SeedChain(store, b);

        var sessions = await ClaudeSessions.ListSessionsAsync(store, Dir);
        Assert.Equal(new HashSet<string> { a, b }, sessions.Select(s => s.SessionId).ToHashSet());
        Assert.All(sessions, s => Assert.Equal("prompt 0", s.Summary));
        Assert.All(sessions, s => Assert.Equal("prompt 0", s.FirstPrompt));
        Assert.Equal(sessions.Select(s => s.LastModified).OrderByDescending(x => x), sessions.Select(s => s.LastModified));
        // cwd falls back to the canonical project directory.
        Assert.All(sessions, s => Assert.Equal(SessionPaths.CanonicalizePath(Dir), s.Cwd));
    }

    /// <summary>A store with list_sessions + load (no summaries) whose load fails for one id.</summary>
    private sealed class FlakyListingStore : ISessionStore
    {
        public readonly InMemorySessionStore Inner = new();
        public string? FailId;
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Inner.AppendAsync(key, entries, cancellationToken);
        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => key.SessionId == FailId ? throw new InvalidOperationException("boom") : Inner.LoadAsync(key, cancellationToken);
        public Task<IReadOnlyList<SessionStoreListEntry>> ListSessionsAsync(string projectKey, CancellationToken cancellationToken = default)
            => Inner.ListSessionsAsync(projectKey, cancellationToken);
    }

    [Fact]
    public async Task ListSessions_AdapterLoadErrorDegradesRow()
    {
        var store = new FlakyListingStore();
        var ok = Guid.NewGuid().ToString();
        var bad = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, ok);
        await ParityUtil.SeedChain(store, bad);
        store.FailId = bad;

        var sessions = await ClaudeSessions.ListSessionsAsync(store, Dir);
        Assert.Equal("prompt 0", sessions.Single(s => s.SessionId == ok).Summary);
        Assert.Equal("", sessions.Single(s => s.SessionId == bad).Summary);
    }

    /// <summary>Summaries are served but one is stale (older than list_sessions' mtime).</summary>
    private sealed class StaleSummaryStore : ISessionStore
    {
        public readonly InMemorySessionStore Inner = new();
        public string? StaleId;
        public int Loads;
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Inner.AppendAsync(key, entries, cancellationToken);
        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Loads);
            return Inner.LoadAsync(key, cancellationToken);
        }
        public Task<IReadOnlyList<SessionStoreListEntry>> ListSessionsAsync(string projectKey, CancellationToken cancellationToken = default)
            => Inner.ListSessionsAsync(projectKey, cancellationToken);
        public async Task<IReadOnlyList<SessionSummaryEntry>> ListSessionSummariesAsync(string projectKey, CancellationToken cancellationToken = default)
            => (await Inner.ListSessionSummariesAsync(projectKey, cancellationToken))
                .Select(s => s.SessionId == StaleId ? s with { Mtime = s.Mtime - 1_000 } : s).ToList();
    }

    [Fact]
    public async Task ListSessions_FastPath_GapFillsOnlyStaleSummaries()
    {
        var store = new StaleSummaryStore();
        var fresh = Guid.NewGuid().ToString();
        var stale = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, fresh);
        await ParityUtil.SeedChain(store, stale);
        store.StaleId = stale;

        var sessions = await ClaudeSessions.ListSessionsAsync(store, Dir);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, store.Loads); // only the stale one is re-derived via load()
        Assert.All(sessions, s => Assert.Equal("prompt 0", s.Summary));
    }

    [Fact]
    public async Task GetSessionInfo_ReflectsTitleAndTag_CwdFallsBackToDirectory()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, sid);
        await ClaudeSessions.RenameSessionAsync(store, sid, "  My title ", Dir);
        await ClaudeSessions.TagSessionAsync(store, sid, "exp", Dir);

        var info = await ClaudeSessions.GetSessionInfoAsync(store, sid, Dir);
        Assert.NotNull(info);
        Assert.Equal("My title", info!.Summary);
        Assert.Equal("My title", info.CustomTitle);
        Assert.Equal("exp", info.Tag);
        Assert.Equal("prompt 0", info.FirstPrompt);
        Assert.Equal(SessionPaths.CanonicalizePath(Dir), info.Cwd);
        Assert.NotNull(info.FileSize);
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00Z").ToUnixTimeMilliseconds(), info.CreatedAt);
    }

    [Fact]
    public async Task ForkViaStore_DerivesTitleFromFirstPrompt_WhenNoTitles()
    {
        var store = new InMemorySessionStore();
        var sid = Guid.NewGuid().ToString();
        await ParityUtil.SeedChain(store, sid);

        var fork = await ClaudeSessions.ForkSessionAsync(store, sid, Dir);
        var info = await ClaudeSessions.GetSessionInfoAsync(store, fork.SessionId, Dir);
        Assert.Equal("prompt 0 (fork)", info!.CustomTitle);
        var messages = await ClaudeSessions.GetSessionMessagesAsync(store, fork.SessionId, Dir);
        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.Equal(fork.SessionId, m.SessionId));
    }

    [Fact]
    public void PythonJson_IsCompactAndAsciiEscaped()
    {
        var node = JsonNode.Parse("{\"type\":\"user\",\"t\":\"caf\u00e9 <b> & \u2028\",\"n\":1.5,\"a\":[true,null]}");
        Assert.Equal("{\"type\":\"user\",\"t\":\"caf\\u00e9 <b> & \\u2028\",\"n\":1.5,\"a\":[true,null]}",
            SessionTranscripts.ToPythonJson(node));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClaudeConfigDirCollection
{
    public const string Name = "CLAUDE_CONFIG_DIR (process env)";
}

/// <summary>Disk-backed session API against a temp CLAUDE_CONFIG_DIR.</summary>
[Collection(ClaudeConfigDirCollection.Name)]
public sealed class SessionDiskParityTests : IDisposable
{
    private readonly string? _prevConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    private readonly string _configDir = SessTestUtil.TempDir("claude_cfg_");
    private readonly string _projectDir = SessTestUtil.TempDir("claude_proj_");
    private readonly string _projectStoreDir;

    public SessionDiskParityTests()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _configDir);
        _projectStoreDir = Path.Combine(_configDir, "projects", SessionPaths.ProjectKeyForDirectory(_projectDir));
        Directory.CreateDirectory(_projectStoreDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _prevConfigDir);
        try { Directory.Delete(_configDir, true); } catch { }
        try { Directory.Delete(_projectDir, true); } catch { }
    }

    private string WriteSession(string sid, IEnumerable<JsonObject> entries, string? extraLines = null)
    {
        var path = Path.Combine(_projectStoreDir, sid + ".jsonl");
        File.WriteAllText(path, string.Join("\n", entries.Select(e => e.ToJsonString())) + "\n" + (extraLines ?? ""));
        return path;
    }

    [Fact]
    public void Readers_RoundTrip_AndReturnEmptyForInvalidOrMissing()
    {
        var sid = Guid.NewGuid().ToString();
        var chain = ParityUtil.Chain(sid, 2);
        chain[0]["cwd"] = "/some/cwd";
        chain[0]["gitBranch"] = "main";
        WriteSession(sid, chain, "not json at all\n");

        var list = ClaudeSessions.ListSessions(_projectDir);
        var info = Assert.Single(list);
        Assert.Equal(sid, info.SessionId);
        Assert.Equal("prompt 0", info.Summary);
        Assert.Equal("/some/cwd", info.Cwd);
        Assert.Equal("main", info.GitBranch);
        Assert.True(info.FileSize > 0);
        Assert.Contains(ClaudeSessions.ListSessions(), s => s.SessionId == sid); // all projects

        Assert.Equal(sid, ClaudeSessions.GetSessionInfo(sid, _projectDir)!.SessionId);
        Assert.Equal(sid, ClaudeSessions.GetSessionInfo(sid)!.SessionId); // search all projects
        var messages = ClaudeSessions.GetSessionMessages(sid, _projectDir);
        Assert.Equal(chain.Select(e => (string)e["uuid"]!), messages.Select(m => m.Uuid));
        Assert.Equal(2, ClaudeSessions.GetSessionMessages(sid, null, limit: 2).Count);

        Assert.Null(ClaudeSessions.GetSessionInfo("bad", _projectDir));
        Assert.Empty(ClaudeSessions.GetSessionMessages("bad", _projectDir));
        Assert.Empty(ClaudeSessions.ListSubagents("bad", _projectDir));
        Assert.Empty(ClaudeSessions.GetSubagentMessages("bad", "a", _projectDir));
        var missing = Guid.NewGuid().ToString();
        Assert.Null(ClaudeSessions.GetSessionInfo(missing, _projectDir));
        Assert.Empty(ClaudeSessions.GetSessionMessages(missing, _projectDir));
        Assert.Empty(ClaudeSessions.ListSubagents(missing, _projectDir));
    }

    [Fact]
    public void ListSessions_DropsSidechains_AndPaginates()
    {
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var sid = Guid.NewGuid().ToString();
            ids.Add(sid);
            var path = WriteSession(sid, ParityUtil.Chain(sid, 1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
        }
        var side = Guid.NewGuid().ToString();
        var sideChain = ParityUtil.Chain(side, 1);
        sideChain[0]["isSidechain"] = true;
        WriteSession(side, sideChain);

        Assert.Equal(ids, ClaudeSessions.ListSessions(_projectDir).Select(s => s.SessionId));
        Assert.Equal(ids.Skip(1).Take(1), ClaudeSessions.ListSessions(_projectDir, limit: 1, offset: 1).Select(s => s.SessionId));
    }

    [Fact]
    public void Subagents_ReadFromDiskWithSidecarParentIds()
    {
        var sid = Guid.NewGuid().ToString();
        WriteSession(sid, ParityUtil.Chain(sid, 1));
        var nested = Path.Combine(_projectStoreDir, sid, "subagents", "workflows", "run-1");
        Directory.CreateDirectory(nested);
        var subChain = ParityUtil.Chain(sid, 1);
        File.WriteAllText(Path.Combine(nested, "agent-deep.jsonl"), string.Join("\n", subChain.Select(e => e.ToJsonString())));
        File.WriteAllText(Path.Combine(nested, "agent-deep.meta.json"), """{"toolUseId":"toolu_9","parentAgentId":"top"}""");
        File.WriteAllText(Path.Combine(_projectStoreDir, sid, "subagents", "agent-top.jsonl"), subChain[0].ToJsonString());

        Assert.Equal(["top", "deep"], ClaudeSessions.ListSubagents(sid, _projectDir));
        var messages = ClaudeSessions.GetSubagentMessages(sid, "deep", _projectDir);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal("toolu_9", m.ParentToolUseId));
        Assert.All(messages, m => Assert.Equal("top", m.ParentAgentId));
        var top = Assert.Single(ClaudeSessions.GetSubagentMessages(sid, "top", _projectDir));
        Assert.Null(top.ParentToolUseId);
        Assert.Empty(ClaudeSessions.GetSubagentMessages(sid, "nope", _projectDir));
    }

    [Fact]
    public void Mutations_RenameTagDeleteFork()
    {
        var sid = Guid.NewGuid().ToString();
        var path = WriteSession(sid, ParityUtil.Chain(sid, 2));
        File.WriteAllText(Path.Combine(_projectStoreDir, Guid.NewGuid() + ".jsonl"), ""); // 0-byte stub is skipped

        ClaudeSessions.RenameSession(sid, "  Renamed  ", _projectDir);
        ClaudeSessions.TagSession(sid, "x\u200b-tag", _projectDir);
        var info = ClaudeSessions.GetSessionInfo(sid, _projectDir)!;
        Assert.Equal("Renamed", info.CustomTitle);
        Assert.Equal("x-tag", info.Tag);
        Assert.EndsWith(
            $"{{\"type\":\"custom-title\",\"customTitle\":\"Renamed\",\"sessionId\":\"{sid}\"}}\n{{\"type\":\"tag\",\"tag\":\"x-tag\",\"sessionId\":\"{sid}\"}}\n",
            File.ReadAllText(path));
        ClaudeSessions.TagSession(sid, null, _projectDir);
        Assert.Null(ClaudeSessions.GetSessionInfo(sid, _projectDir)!.Tag);

        var fork = ClaudeSessions.ForkSession(sid, _projectDir);
        var forkInfo = ClaudeSessions.GetSessionInfo(fork.SessionId, _projectDir)!;
        Assert.Equal("Renamed (fork)", forkInfo.CustomTitle);
        var forkMessages = ClaudeSessions.GetSessionMessages(fork.SessionId, _projectDir);
        Assert.Equal(4, forkMessages.Count);
        Assert.DoesNotContain(forkMessages, m => ClaudeSessions.GetSessionMessages(sid, _projectDir).Any(o => o.Uuid == m.Uuid));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_projectStoreDir, fork.SessionId + ".jsonl")));

        var upTo = ClaudeSessions.GetSessionMessages(sid, _projectDir)[1].Uuid;
        var partial = ClaudeSessions.ForkSession(sid, _projectDir, upToMessageId: upTo, title: "Mine");
        Assert.Equal(2, ClaudeSessions.GetSessionMessages(partial.SessionId, _projectDir).Count);
        Assert.Equal("Mine", ClaudeSessions.GetSessionInfo(partial.SessionId, _projectDir)!.CustomTitle);
        Assert.Throws<InvalidOperationException>(() => ClaudeSessions.ForkSession(sid, _projectDir, upToMessageId: Guid.NewGuid().ToString()));

        Directory.CreateDirectory(Path.Combine(_projectStoreDir, sid, "subagents"));
        ClaudeSessions.DeleteSession(sid, _projectDir);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(_projectStoreDir, sid)));

        var missing = Guid.NewGuid().ToString();
        Assert.Throws<SessionNotFoundException>(() => ClaudeSessions.RenameSession(missing, "t", _projectDir));
        Assert.Throws<SessionNotFoundException>(() => ClaudeSessions.TagSession(missing, "t"));
        Assert.Throws<SessionNotFoundException>(() => ClaudeSessions.DeleteSession(missing, _projectDir));
        Assert.Throws<SessionNotFoundException>(() => ClaudeSessions.ForkSession(missing, _projectDir));
        Assert.Throws<InvalidSessionIdException>(() => ClaudeSessions.RenameSession("bad", "t"));
        Assert.Throws<InvalidSessionIdException>(() => ClaudeSessions.DeleteSession("bad"));
        Assert.Throws<ArgumentException>(() => ClaudeSessions.RenameSession(missing, "   "));
        Assert.Throws<ArgumentException>(() => ClaudeSessions.TagSession(missing, "\u200b "));
    }

    [Fact]
    public async Task Import_RaisesOnMalformedLine()
    {
        var sid = Guid.NewGuid().ToString();
        WriteSession(sid, ParityUtil.Chain(sid, 1), "{not json\n");
        var store = new InMemorySessionStore();
        await Assert.ThrowsAsync<JsonException>(() => ClaudeSessions.ImportSessionToStoreAsync(sid, store, _projectDir));

        var sid2 = Guid.NewGuid().ToString();
        WriteSession(sid2, ParityUtil.Chain(sid2, 1), "[1,2]\n");
        await Assert.ThrowsAsync<JsonException>(() => ClaudeSessions.ImportSessionToStoreAsync(sid2, store, _projectDir));
    }

    [Fact]
    public async Task Import_SubagentsInPythonOrder()
    {
        var sid = Guid.NewGuid().ToString();
        WriteSession(sid, ParityUtil.Chain(sid, 1));
        var sub = Path.Combine(_projectStoreDir, sid, "subagents");
        Directory.CreateDirectory(Path.Combine(sub, "a-dir"));
        File.WriteAllText(Path.Combine(sub, "a-dir", "agent-1.jsonl"), ParityUtil.Chain(sid, 1)[0].ToJsonString());
        File.WriteAllText(Path.Combine(sub, "agent-2.jsonl"), ParityUtil.Chain(sid, 1)[0].ToJsonString());

        var store = new RecordingStore();
        await ClaudeSessions.ImportSessionToStoreAsync(sid, store, _projectDir);
        Assert.Equal([null, "subagents/a-dir/agent-1", "subagents/agent-2"], store.Subpaths.Distinct());
    }

    private sealed class RecordingStore : ISessionStore
    {
        public readonly List<string?> Subpaths = new();
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
        {
            Subpaths.Add(key.Subpath);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);
    }

    [Fact]
    public async Task Resume_FifoSeedFileIsSkippedNotRead()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = SessTestUtil.TempDir();
        var fifo = Path.Combine(src, "settings.json");
        using (var p = Process.Start(new ProcessStartInfo("mkfifo", fifo) { UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(File.Exists(fifo));
        File.WriteAllText(Path.Combine(src, "cowork_settings.json"), "{\"a\":1}");

        var tmp = SessTestUtil.TempDir();
        await Task.Run(() => SessionResume.CopyAuthFiles(tmp, new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = src }))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(Path.Combine(tmp, "settings.json")));
        Assert.True(File.Exists(Path.Combine(tmp, "cowork_settings.json")));
    }
}
