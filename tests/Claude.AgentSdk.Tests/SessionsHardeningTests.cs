// Regression tests for the Sessions subsystem hardening / Python-parity fixes:
// FileSessionStore path containment, permissions, atomic summaries and
// torn-line tolerance; subagent id API; resume auth seeding + perms + load
// timeout; batcher ordering; realpath project keys; astral Unicode
// sanitization; fork duplicate uuids; corrupt subagent sidecars.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Claude.AgentSdk;
using Claude.AgentSdk.Sessions;
using Xunit;

namespace Claude.AgentSdk.Tests;

internal static class SessTestUtil
{
    public static string TempDir(string prefix = "claude_sess_test_")
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static SessionStoreEntry Entry(string json)
        => SessionSummary.JsonObjectToEntry((JsonObject)JsonNode.Parse(json)!);

    public static SessionStoreEntry Msg(string type, string uuid, string? parent = null)
        => Entry($"{{\"type\":\"{type}\",\"uuid\":\"{uuid}\",\"parentUuid\":{(parent is null ? "null" : $"\"{parent}\"")},\"timestamp\":\"2025-01-01T00:00:00Z\",\"message\":{{\"role\":\"{type}\",\"content\":\"hi\"}}}}");

    public static UnixFileMode Mode(string path) => File.GetUnixFileMode(path);
}

public class FileSessionStorePathContainmentTests
{
    private static readonly SessionStoreEntry[] One = { SessTestUtil.Entry("{\"type\":\"user\",\"uuid\":\"u1\"}") };

    public static IEnumerable<object[]> BadProjectKeys()
    {
        yield return new object[] { "" };
        yield return new object[] { "." };
        yield return new object[] { ".." };
        yield return new object[] { "a/b" };
        yield return new object[] { "a\\b" };
        yield return new object[] { "../escape" };
        yield return new object[] { "a\0b" };
        yield return new object[] { "C:evil" };
        yield return new object[] { Path.GetTempPath() };
        yield return new object[] { OperatingSystem.IsWindows() ? "C:\\Windows" : "/etc" };
    }

    public static IEnumerable<object[]> BadSessionIds()
    {
        yield return new object[] { "" };
        yield return new object[] { "." };
        yield return new object[] { ".." };
        yield return new object[] { "../x" };
        yield return new object[] { "x/y" };
        yield return new object[] { "x\\y" };
        yield return new object[] { "x\0" };
        yield return new object[] { ".summaries" };
        yield return new object[] { OperatingSystem.IsWindows() ? "C:\\x" : "/x" };
    }

    public static IEnumerable<object[]> BadSubpaths()
    {
        yield return new object[] { ".." };
        yield return new object[] { "../../../x" };
        yield return new object[] { "subagents/../../x" };
        yield return new object[] { "subagents/./agent-1" };
        yield return new object[] { "subagents//agent-1" };
        yield return new object[] { "/etc/passwd" };
        yield return new object[] { "\\evil" };
        yield return new object[] { "subagents\\..\\..\\x" };
        yield return new object[] { "C:foo" };
        yield return new object[] { "subagents/a\0b" };
        yield return new object[] { "subagents/" };
    }

    [Theory]
    [MemberData(nameof(BadProjectKeys))]
    public async Task RejectsBadProjectKey(string projectKey)
    {
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        var key = new SessionKey { ProjectKey = projectKey, SessionId = "s1" };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AppendAsync(key, One));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListSessionsAsync(projectKey));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListSessionSummariesAsync(projectKey));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListSubkeysAsync(new SessionListSubkeysKey(projectKey, "s1")));
    }

    [Theory]
    [MemberData(nameof(BadSessionIds))]
    public async Task RejectsBadSessionId(string sessionId)
    {
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        var key = new SessionKey { ProjectKey = "proj", SessionId = sessionId };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AppendAsync(key, One));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListSubkeysAsync(new SessionListSubkeysKey("proj", sessionId)));
    }

    [Theory]
    [MemberData(nameof(BadSubpaths))]
    public async Task RejectsBadSubpath(string subpath)
    {
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        var key = new SessionKey { ProjectKey = "proj", SessionId = "s1", Subpath = subpath };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AppendAsync(key, One));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadAsync(key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    public async Task Delete_WithTraversingSessionId_DoesNotRemoveProjectOrRoot(string sessionId)
    {
        var parent = SessTestUtil.TempDir();
        var root = Path.Combine(parent, "store");
        var sibling = Path.Combine(parent, "sibling.txt");
        File.WriteAllText(sibling, "keep");
        var store = new FileSessionStore(root);
        await store.AppendAsync(new SessionKey { ProjectKey = "proj", SessionId = "keep" }, One);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.DeleteAsync(new SessionKey { ProjectKey = "proj", SessionId = sessionId }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.DeleteAsync(new SessionKey { ProjectKey = "..", SessionId = "store" }));

        Assert.True(File.Exists(Path.Combine(root, "proj", "keep.jsonl")));
        Assert.True(File.Exists(sibling));
        Assert.NotNull(await store.LoadAsync(new SessionKey { ProjectKey = "proj", SessionId = "keep" }));
    }

    [Fact]
    public async Task Delete_DoesNotRecurseThroughSymlinkedSessionDir()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges
        var outside = SessTestUtil.TempDir("claude_outside_");
        var precious = Path.Combine(outside, "precious.txt");
        File.WriteAllText(precious, "do not delete");

        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        await store.AppendAsync(new SessionKey { ProjectKey = "proj", SessionId = "s1" }, One);
        Directory.CreateSymbolicLink(Path.Combine(root, "proj", "s1"), outside);

        await store.DeleteAsync(new SessionKey { ProjectKey = "proj", SessionId = "s1" });

        Assert.True(File.Exists(precious));
        Assert.False(File.Exists(Path.Combine(root, "proj", "s1.jsonl")));
    }

    [Fact]
    public async Task ValidNestedSubpath_RoundTrips()
    {
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        var key = new SessionKey { ProjectKey = "proj", SessionId = "s1", Subpath = "subagents/workflows/run-1/agent-a" };
        await store.AppendAsync(key, One);
        var loaded = await store.LoadAsync(key);
        Assert.Single(loaded!);
        var subkeys = await store.ListSubkeysAsync(new SessionListSubkeysKey("proj", "s1"));
        Assert.Equal(new[] { "subagents/workflows/run-1/agent-a" }, subkeys);
    }
}

public class FileSessionStoreDurabilityTests
{
    [Fact]
    public async Task Load_SkipsTruncatedLine()
    {
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        var key = new SessionKey { ProjectKey = "proj", SessionId = "s1" };
        await store.AppendAsync(key, new[] { SessTestUtil.Msg("user", "u1") });
        File.AppendAllText(Path.Combine(root, "proj", "s1.jsonl"), "{\"type\":\"assis");
        var loaded = await store.LoadAsync(key);
        Assert.Single(loaded!);
        Assert.Equal("u1", loaded![0].Uuid);
    }

    [Fact]
    public async Task SummaryWrite_IsAtomic_LeavesNoTempFiles()
    {
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(root);
        var key = new SessionKey { ProjectKey = "proj", SessionId = "s1" };
        for (int i = 0; i < 5; i++)
            await store.AppendAsync(key, new[] { SessTestUtil.Msg("user", "u" + i) });
        var files = Directory.GetFiles(Path.Combine(root, "proj", ".summaries"));
        Assert.Equal(new[] { "s1.json" }, files.Select(Path.GetFileName).ToArray());
        var summaries = await store.ListSessionSummariesAsync("proj");
        Assert.Single(summaries);
    }

    [Fact]
    public async Task CreatesPrivateFilesAndDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = SessTestUtil.TempDir();
        var store = new FileSessionStore(Path.Combine(root, "store"));
        await store.AppendAsync(new SessionKey { ProjectKey = "proj", SessionId = "s1" }, new[] { SessTestUtil.Msg("user", "u1") });
        await store.AppendAsync(new SessionKey { ProjectKey = "proj", SessionId = "s1", Subpath = "subagents/agent-a" }, new[] { SessTestUtil.Msg("user", "u2") });

        const UnixFileMode rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode rwx = rw | UnixFileMode.UserExecute;
        Assert.Equal(rwx, SessTestUtil.Mode(Path.Combine(root, "store")));
        Assert.Equal(rwx, SessTestUtil.Mode(Path.Combine(root, "store", "proj")));
        Assert.Equal(rwx, SessTestUtil.Mode(Path.Combine(root, "store", "proj", "s1", "subagents")));
        Assert.Equal(rw, SessTestUtil.Mode(Path.Combine(root, "store", "proj", "s1.jsonl")));
        Assert.Equal(rw, SessTestUtil.Mode(Path.Combine(root, "store", "proj", "s1", "subagents", "agent-a.jsonl")));
        Assert.Equal(rw, SessTestUtil.Mode(Path.Combine(root, "store", "proj", ".summaries", "s1.json")));
    }
}

public class SubagentApiParityTests
{
    private const string Sid = "550e8400-e29b-41d4-a716-446655440000";

    private static async Task<(InMemorySessionStore Store, string Dir)> SeedAsync()
    {
        var dir = SessTestUtil.TempDir();
        var pk = SessionPaths.ProjectKeyForDirectory(dir);
        var store = new InMemorySessionStore();
        await store.AppendAsync(new SessionKey { ProjectKey = pk, SessionId = Sid }, new[] { SessTestUtil.Msg("user", "m1") });
        await store.AppendAsync(new SessionKey { ProjectKey = pk, SessionId = Sid, Subpath = "subagents/agent-abc" },
            new[]
            {
                SessTestUtil.Msg("user", "a1"),
                SessTestUtil.Entry("{\"type\":\"agent_metadata\",\"toolUseId\":\"toolu_1\"}"),
            });
        await store.AppendAsync(new SessionKey { ProjectKey = pk, SessionId = Sid, Subpath = "subagents/workflows/run-1/agent-def" },
            new[] { SessTestUtil.Msg("assistant", "d1") });
        await store.AppendAsync(new SessionKey { ProjectKey = pk, SessionId = Sid, Subpath = "other/agent-zzz" },
            new[] { SessTestUtil.Msg("user", "z1") });
        return (store, dir);
    }

    [Fact]
    public async Task ListSubagents_ReturnsAgentIds()
    {
        var (store, dir) = await SeedAsync();
        var ids = await ClaudeSessions.ListSubagentsAsync(store, Sid, dir);
        Assert.Equal(new[] { "abc", "def" }, ids.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetSubagentMessages_ByAgentId_IncludingNested()
    {
        var (store, dir) = await SeedAsync();
        var abc = await ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "abc", dir);
        Assert.Single(abc);
        Assert.Equal("a1", abc[0].Uuid);
        Assert.Equal("toolu_1", abc[0].ParentToolUseId);

        var def = await ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "def", dir);
        Assert.Single(def);
        Assert.Equal("d1", def[0].Uuid);

        // Not under subagents/ -> not a subagent (Python parity).
        Assert.Empty(await ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "zzz", dir));
        Assert.Empty(await ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "missing", dir));
        Assert.Empty(await ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "", dir));
    }

    [Fact]
    public async Task GetSubagentMessages_LegacySubkey_StillWorks_ButIsValidated()
    {
        var (store, dir) = await SeedAsync();
        var legacy = await ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "subagents/agent-abc", dir);
        Assert.Single(legacy);

        await Assert.ThrowsAsync<ArgumentException>(() => ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "subagents/../../x", dir));
        await Assert.ThrowsAsync<ArgumentException>(() => ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "../subagents/agent-abc", dir));
        await Assert.ThrowsAsync<ArgumentException>(() => ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "other/agent-zzz", dir));
        await Assert.ThrowsAsync<ArgumentException>(() => ClaudeSessions.GetSubagentMessagesAsync(store, Sid, "a\0b", dir));
    }
}

public class SessionResumeHardeningTests
{
    private const string Sid = "550e8400-e29b-41d4-a716-446655440000";

    private sealed class SlowStore : ISessionStore
    {
        public Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return null;
        }
    }

    [Fact]
    public async Task Materialize_SeedsAuthFiles_RedactsRefreshToken_StripsSettings_PrivatePerms()
    {
        var cwd = SessTestUtil.TempDir();
        var configDir = SessTestUtil.TempDir("claude_cfg_");
        File.WriteAllText(Path.Combine(configDir, ".credentials.json"),
            "{\"claudeAiOauth\":{\"accessToken\":\"at\",\"refreshToken\":\"rt-secret\"}}");
        File.WriteAllText(Path.Combine(configDir, ".claude.json"), "{\"userID\":\"x\"}");
        File.WriteAllText(Path.Combine(configDir, "settings.json"),
            "{\"apiKeyHelper\":\"h\",\"enabledPlugins\":{\"p\":true},\"extraKnownMarketplaces\":{},\"env\":{\"CLAUDE_CONFIG_DIR\":\"/x\",\"KEEP\":\"1\"}}",
            new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(configDir, "cowork_settings.json"), "not json");

        var store = new InMemorySessionStore();
        var pk = SessionPaths.ProjectKeyForDirectory(cwd);
        await store.AppendAsync(new SessionKey { ProjectKey = pk, SessionId = Sid }, new[] { SessTestUtil.Msg("user", "m1") });
        await store.AppendAsync(new SessionKey { ProjectKey = pk, SessionId = Sid, Subpath = "subagents/agent-a" },
            new[] { SessTestUtil.Msg("user", "a1"), SessTestUtil.Entry("{\"type\":\"agent_metadata\",\"agentType\":\"x\"}") });

        var options = new ClaudeAgentOptions
        {
            SessionStore = store,
            Resume = Sid,
            Cwd = cwd,
            Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configDir },
        };
        var m = await SessionResume.MaterializeResumeSessionAsync(options);
        Assert.NotNull(m);
        try
        {
            var creds = JsonNode.Parse(File.ReadAllText(Path.Combine(m!.ConfigDir, ".credentials.json")))!;
            Assert.Equal("at", (string?)creds["claudeAiOauth"]!["accessToken"]);
            Assert.Null(creds["claudeAiOauth"]!["refreshToken"]);

            Assert.Equal("{\"userID\":\"x\"}", File.ReadAllText(Path.Combine(m.ConfigDir, ".claude.json")));

            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(m.ConfigDir, "settings.json")))!.AsObject();
            Assert.Equal("h", (string?)settings["apiKeyHelper"]);
            Assert.False(settings.ContainsKey("enabledPlugins"));
            Assert.False(settings.ContainsKey("extraKnownMarketplaces"));
            Assert.False(settings["env"]!.AsObject().ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.Equal("1", (string?)settings["env"]!["KEEP"]);
            // Unparseable settings pass through untouched.
            Assert.Equal("not json", File.ReadAllText(Path.Combine(m.ConfigDir, "cowork_settings.json")));

            var transcript = Path.Combine(m.ConfigDir, "projects", pk, Sid + ".jsonl");
            var sub = Path.Combine(m.ConfigDir, "projects", pk, Sid, "subagents", "agent-a.jsonl");
            var meta = Path.Combine(m.ConfigDir, "projects", pk, Sid, "subagents", "agent-a.meta.json");
            Assert.True(File.Exists(transcript));
            Assert.True(File.Exists(sub));
            Assert.True(File.Exists(meta));

            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                Assert.Equal(rw | UnixFileMode.UserExecute, SessTestUtil.Mode(m.ConfigDir));
                foreach (var f in new[] { transcript, sub, meta,
                             Path.Combine(m.ConfigDir, ".credentials.json"),
                             Path.Combine(m.ConfigDir, ".claude.json"),
                             Path.Combine(m.ConfigDir, "settings.json") })
                    Assert.Equal(rw, SessTestUtil.Mode(f));
            }
        }
        finally
        {
            await m!.CleanupAsync(CancellationToken.None);
        }
        Assert.False(Directory.Exists(m.ConfigDir));
    }

    [Fact]
    public void CopyAuthFiles_DoesNotConsultKeychain_WhenConfigDirOrApiKeyGiven()
    {
        var calls = 0;
        var prev = SessionResume.KeychainReader;
        var prevHome = SessionResume.HomeDirectory;
        // Never read the developer's real ~/.claude in tests.
        var fakeHome = SessTestUtil.TempDir("claude_home_");
        SessionResume.HomeDirectory = () => fakeHome;
        SessionResume.KeychainReader = () => { calls++; return "{\"claudeAiOauth\":{\"refreshToken\":\"r\"}}"; };
        try
        {
            var tmp = SessTestUtil.TempDir();
            SessionResume.CopyAuthFiles(tmp, new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = SessTestUtil.TempDir() });
            SessionResume.CopyAuthFiles(tmp, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "k" });
            Assert.Equal(0, calls);
            Assert.False(File.Exists(Path.Combine(tmp, ".credentials.json")));
        }
        finally
        {
            SessionResume.KeychainReader = prev;
            SessionResume.HomeDirectory = prevHome;
        }
    }

    [Fact]
    public void WriteRedactedCredentials_PassesThroughUnparseable()
    {
        var dst = Path.Combine(SessTestUtil.TempDir(), ".credentials.json");
        SessionResume.WriteRedactedCredentials("garbage{", dst);
        Assert.Equal("garbage{", File.ReadAllText(dst));
    }

    [Fact]
    public async Task Materialize_HonorsLoadTimeoutOverload()
    {
        var options = new ClaudeAgentOptions
        {
            SessionStore = new SlowStore(),
            Resume = Sid,
            Cwd = SessTestUtil.TempDir(),
            Env = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = SessTestUtil.TempDir() },
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<SessionStoreOperationException>(() =>
            SessionResume.MaterializeResumeSessionAsync(options, TimeSpan.FromMilliseconds(100)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(60_000), SessionResume.DefaultLoadTimeout);
    }
}

public class TranscriptMirrorBatcherOrderingTests
{
    private sealed class RecordingStore : ISessionStore
    {
        public readonly ConcurrentQueue<string> Order = new();
        public TaskCompletionSource? Gate;
        private int _calls;

        public async Task AppendAsync(SessionKey key, IReadOnlyList<SessionStoreEntry> entries, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1 && Gate is not null) await Gate.Task;
            // Jitter to shake out reordering between concurrent drains.
            await Task.Delay(Random.Shared.Next(0, 3), cancellationToken);
            foreach (var e in entries) Order.Enqueue(e.Uuid!);
        }

        public Task<IReadOnlyList<SessionStoreEntry>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionStoreEntry>?>(null);
    }

    [Fact]
    public async Task EagerDrains_AppendInEnqueueOrder()
    {
        var projects = SessTestUtil.TempDir();
        var file = Path.Combine(projects, "proj", "s1.jsonl");
        var store = new RecordingStore();
        await using var batcher = new TranscriptMirrorBatcher(store, projects, (_, _, _) => Task.CompletedTask)
        {
            MaxPendingEntries = 0,
            MaxPendingBytes = 0,
        };
        var expected = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            var id = "u" + i;
            expected.Add(id);
            batcher.Enqueue(file, new[] { SessTestUtil.Entry($"{{\"type\":\"user\",\"uuid\":\"{id}\"}}") });
        }
        await batcher.FlushAsync();
        Assert.Equal(expected, store.Order.ToList());
    }

    [Fact]
    public async Task Flush_WaitsForInFlightEagerFlush_EvenWithNothingPending()
    {
        var projects = SessTestUtil.TempDir();
        var file = Path.Combine(projects, "proj", "s1.jsonl");
        var store = new RecordingStore { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var batcher = new TranscriptMirrorBatcher(store, projects, (_, _, _) => Task.CompletedTask)
        {
            MaxPendingEntries = 0,
            MaxPendingBytes = 0,
        };
        batcher.Enqueue(file, new[] { SessTestUtil.Entry("{\"type\":\"user\",\"uuid\":\"x\"}") });

        var flush = batcher.FlushAsync();
        await Task.Delay(100);
        Assert.False(flush.IsCompleted);

        // Disposing while a drain is blocked must not throw (no disposed lock).
        var dispose = batcher.DisposeAsync().AsTask();
        store.Gate.SetResult();
        await flush;
        await dispose;
        Assert.Equal(new[] { "x" }, store.Order.ToArray());
    }
}

public class SessionPathsRealPathTests
{
    [Fact]
    public void ProjectKey_ResolvesSymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var baseDir = SessTestUtil.TempDir();
        var real = Path.Combine(baseDir, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(baseDir, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal(SessionPaths.ProjectKeyForDirectory(real), SessionPaths.ProjectKeyForDirectory(link));
        Assert.Equal(SessionPaths.RealPath(real), SessionPaths.RealPath(link));
    }

    [Fact]
    public void RealPath_AppliesDotDotAfterResolvingLink()
    {
        if (OperatingSystem.IsWindows()) return;
        var baseDir = SessionPaths.RealPath(SessTestUtil.TempDir());
        var deep = Path.Combine(baseDir, "a", "b");
        Directory.CreateDirectory(deep);
        var link = Path.Combine(baseDir, "l");
        Directory.CreateSymbolicLink(link, deep);
        // POSIX: l/.. is the parent of the *target* (a), not baseDir.
        Assert.Equal(Path.Combine(baseDir, "a"), SessionPaths.RealPath(Path.Combine(link, "..")));
        // Non-existent tail is appended verbatim.
        Assert.Equal(Path.Combine(deep, "nope", "x"), SessionPaths.RealPath(Path.Combine(link, "nope", "x")));
    }

    [Fact]
    public void RealPath_TerminatesOnSymlinkLoop()
    {
        if (OperatingSystem.IsWindows()) return;
        var baseDir = SessTestUtil.TempDir();
        var a = Path.Combine(baseDir, "a");
        var b = Path.Combine(baseDir, "b");
        File.CreateSymbolicLink(a, b);
        File.CreateSymbolicLink(b, a);
        var result = SessionPaths.RealPath(a);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void MacOsTmp_MapsToPrivateTmp()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(SessionPaths.ProjectKeyForDirectory("/private/tmp"), SessionPaths.ProjectKeyForDirectory("/tmp"));
    }
}

public class SessionMutationsParityTests
{
    [Theory]
    [InlineData("a\U000E0041b", "ab")]          // TAG LATIN CAPITAL LETTER A (Cf, astral)
    [InlineData("x\U000E007Fy", "xy")]          // CANCEL TAG
    [InlineData("p\U000F0001q", "pq")]          // Supplementary private use (Co)
    [InlineData("zw\u200bj", "zwj")]            // BMP format char still stripped
    [InlineData("smile\U0001F600", "smile\U0001F600")] // emoji kept
    public void SanitizeUnicode_StripsAstralFormatAndPrivateUse(string input, string expected)
        => Assert.Equal(expected, SessionMutations.SanitizeUnicode(input));

    [Fact]
    public async Task Fork_WithDuplicateUuids_KeepsLast_DoesNotThrow()
    {
        const string sid = "550e8400-e29b-41d4-a716-446655440000";
        var dir = SessTestUtil.TempDir();
        var store = new InMemorySessionStore();
        var key = new SessionKey { ProjectKey = SessionPaths.ProjectKeyForDirectory(dir), SessionId = sid };
        await store.AppendAsync(key, new[]
        {
            SessTestUtil.Msg("user", "11111111-1111-1111-1111-111111111111"),
            SessTestUtil.Msg("assistant", "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111"),
            SessTestUtil.Msg("assistant", "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111"),
        });

        var result = await SessionMutations.ForkSessionViaStoreAsync(store, sid, dir);
        var forked = await store.LoadAsync(key with { SessionId = result.SessionId });
        Assert.NotNull(forked);
        Assert.Contains(forked!, e => e.Type == "custom-title");
    }
}

public class SessionImportSidecarTests
{
    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"str\"")]
    public async Task CorruptOrNonObjectSidecar_IsTreatedAsAbsent(string content)
    {
        var dir = SessTestUtil.TempDir();
        var transcript = Path.Combine(dir, "agent-a.jsonl");
        File.WriteAllText(transcript, "{}\n");
        File.WriteAllText(Path.Combine(dir, "agent-a.meta.json"), content);
        Assert.Null(await SessionImport.ReadAgentMetadataSidecarAsync(transcript, CancellationToken.None));
    }

    [Fact]
    public async Task MissingSidecar_IsAbsent_ValidSidecar_IsRead()
    {
        var dir = SessTestUtil.TempDir();
        var transcript = Path.Combine(dir, "agent-a.jsonl");
        Assert.Null(await SessionImport.ReadAgentMetadataSidecarAsync(transcript, CancellationToken.None));
        File.WriteAllText(Path.Combine(dir, "agent-a.meta.json"), "{\"agentType\":\"x\"}");
        var meta = await SessionImport.ReadAgentMetadataSidecarAsync(transcript, CancellationToken.None);
        Assert.Equal("x", (string?)meta!["agentType"]);
    }
}
