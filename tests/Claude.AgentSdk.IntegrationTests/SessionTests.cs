using System.Text.Json;
using Claude.AgentSdk.IntegrationTests.Infrastructure;
using Claude.AgentSdk.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Claude.AgentSdk.IntegrationTests;

/// <summary>
/// Session persistence: local transcripts (resume / fork / continue), SessionStore
/// mirroring and store-backed resume, and the ClaudeSessions read API.
/// Each test plants a random code word in one CLI process and asks for it from another,
/// so recall can only come from the persisted session.
/// </summary>
[Trait("Category", "Integration")]
public class SessionTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    // Phrased as an ordinary request: haiku sometimes refuses "remember this code word"
    // prompts as a suspected test/jailbreak.
    private const string AskForCode = "What is the codename of my project? Answer with just the codename.";

    private static string Plant(string nonce) =>
        $"My project's codename is {nonce}. Please keep that in mind. Acknowledge with just OK.";

    private async Task<ResultMessage> PlantAsync(string nonce, ClaudeAgentOptions options, string? suffix = "seed",
        [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        var messages = await CollectAsync(Claude.QueryAsync(Plant(nonce), options, Transport(options, test, suffix), Ct));
        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        return result;
    }

    private string TranscriptPath(string sessionId) =>
        Path.Combine(SessionPaths.GetProjectsDir(), SessionPaths.ProjectKeyForDirectory(Cwd), sessionId + ".jsonl");

    [IntegrationFact]
    public async Task Resume_LocalSession_RecallsPlantedNonce()
    {
        var nonce = NewNonce("CODE");
        var seed = await PlantAsync(nonce, Options());
        Assert.True(File.Exists(TranscriptPath(seed.SessionId)), "CLI should persist the transcript locally");

        var options = Options() with { Resume = seed.SessionId };
        var messages = await CollectAsync(Claude.QueryAsync(AskForCode, options, Transport(options), Ct));

        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(seed.SessionId, result.SessionId);
        Assert.Contains(nonce, result.Result);
    }

    [IntegrationFact]
    public async Task ForkSession_RecallsNonce_UnderNewSessionId()
    {
        var nonce = NewNonce("CODE");
        var seed = await PlantAsync(nonce, Options());

        var options = Options() with { Resume = seed.SessionId, ForkSession = true };
        var messages = await CollectAsync(Claude.QueryAsync(AskForCode, options, Transport(options), Ct));

        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.NotEqual(seed.SessionId, result.SessionId);
        Assert.Contains(nonce, result.Result);
        // Both transcripts exist: the original is left untouched by the fork.
        Assert.True(File.Exists(TranscriptPath(seed.SessionId)));
        Assert.True(File.Exists(TranscriptPath(result.SessionId)));
    }

    [IntegrationFact]
    public async Task ContinueConversation_PicksUpMostRecentSessionInCwd()
    {
        var nonce = NewNonce("CODE");
        var seed = await PlantAsync(nonce, Options());

        var options = Options() with { ContinueConversation = true };
        var messages = await CollectAsync(Claude.QueryAsync(AskForCode, options, Transport(options), Ct));

        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.Contains(nonce, result.Result);
    }

    [IntegrationFact(Skip = "SDK bug: ClaudeSessions.ListSessionsAsync(new FileSessionStore(SessionPaths.GetProjectsDir()), cwd) " +
                            "returns [] for sessions the CLI wrote. ListSessionsAsync prefers ListSessionSummariesAsync whenever the " +
                            "store implements it, and FileSessionStore only reads .summaries/ sidecars, which the CLI never writes " +
                            "(no fallback to scanning *.jsonl). The ClaudeSessions docs recommend exactly this store for the on-disk layout.")]
    public async Task ClaudeSessions_ListFindsJustCreatedSessionOnDisk()
    {
        var nonce = NewNonce("CODE");
        var seed = await PlantAsync(nonce, Options(), suffix: null);

        var store = new FileSessionStore(SessionPaths.GetProjectsDir());
        var sessions = await ClaudeSessions.ListSessionsAsync(store, Cwd, cancellationToken: Ct);

        var info = Assert.Single(sessions);
        Assert.Equal(seed.SessionId, info.SessionId);
        Assert.Equal(Plant(nonce), info.FirstPrompt);
    }

    [IntegrationFact]
    public async Task ClaudeSessions_GetInfoAndMessagesOfJustCreatedSessionOnDisk()
    {
        var nonce = NewNonce("CODE");
        var seed = await PlantAsync(nonce, Options(), suffix: null);

        // The on-disk layout is a FileSessionStore rooted at the CLI's projects dir.
        var store = new FileSessionStore(SessionPaths.GetProjectsDir());

        var single = await ClaudeSessions.GetSessionInfoAsync(store, seed.SessionId, Cwd, Ct);
        Assert.NotNull(single);
        Assert.Equal(seed.SessionId, single!.SessionId);
        Assert.Equal(Plant(nonce), single.FirstPrompt);
        Assert.Equal(Cwd, single.Cwd);
        Log($"LastModified={single.LastModified}"); // see ClaudeSessions_GetSessionInfo_ReportsLastModified

        var messages = await ClaudeSessions.GetSessionMessagesAsync(store, seed.SessionId, Cwd, Ct);
        Assert.True(messages.Count >= 2, $"expected user + assistant messages, got {messages.Count}");
        var first = messages[0];
        Assert.Equal("user", first.Type);
        Assert.Equal(seed.SessionId, first.SessionId);
        Assert.Contains(nonce, first.MessageData.GetRawText());
        Assert.Contains(messages, m => m.Type == "assistant");
        Assert.All(messages, m => Assert.True(Guid.TryParse(m.Uuid, out _)));
    }

    [IntegrationFact(Skip = "SDK bug: ClaudeSessions.GetSessionInfoAsync returns LastModified = 0: it folds the entries with " +
                            "no mtime (SessionSummary.FoldSessionSummary(null, key, entries)). Python's " +
                            "get_session_info_from_store uses the last entry's timestamp (falling back to now).")]
    public async Task ClaudeSessions_GetSessionInfo_ReportsLastModified()
    {
        var seed = await PlantAsync(NewNonce("CODE"), Options(), suffix: null);
        var store = new FileSessionStore(SessionPaths.GetProjectsDir());

        var info = await ClaudeSessions.GetSessionInfoAsync(store, seed.SessionId, Cwd, Ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(info!.LastModified, now - 600_000, now + 60_000);
    }

    [IntegrationFact]
    public async Task SessionStore_MirrorsTranscript_AndResumeFromStoreRecallsNonce()
    {
        var nonce = NewNonce("CODE");
        var store = new InMemorySessionStore();
        var projectKey = SessionPaths.ProjectKeyForDirectory(Cwd);

        // Run 1: mirror into the store (no custom transport: the SDK must wire the batcher).
        var seedOptions = Options() with { SessionStore = store };
        var seedMessages = await CollectAsync(Claude.QueryAsync(Plant(nonce), seedOptions, cancellationToken: Ct));
        var seed = SingleResult(seedMessages);
        Assert.DoesNotContain(seedMessages, m => m is MirrorErrorMessage);

        var key = new SessionKey { ProjectKey = projectKey, SessionId = seed.SessionId };
        var entries = store.GetEntries(key);
        Log($"mirrored {entries.Count} entries: {string.Join(",", entries.Select(e => e.Type))}");
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Type == "user");
        Assert.Contains(entries, e => e.Type == "assistant");
        Assert.Contains(nonce, JsonSerializer.Serialize(entries));

        // The store is queryable through the public session API.
        var listed = await ClaudeSessions.ListSessionsAsync(store, Cwd, cancellationToken: Ct);
        Assert.Contains(listed, s => s.SessionId == seed.SessionId);
        var storedMessages = await ClaudeSessions.GetSessionMessagesAsync(store, seed.SessionId, Cwd, Ct);
        Assert.Contains(storedMessages, m => m.Type == "user" && m.MessageData.GetRawText().Contains(nonce));

        // Remove the CLI's local copy: a successful recall now proves the resume was
        // materialized from the store, not read from ~/.claude/projects.
        File.Delete(TranscriptPath(seed.SessionId));

        // Run 2: resume from the store in a fresh process.
        var resumeOptions = Options() with { SessionStore = store, Resume = seed.SessionId };
        var messages = await CollectAsync(Claude.QueryAsync(AskForCode, resumeOptions, cancellationToken: Ct));
        var result = SingleResult(messages);
        Assert.Equal("success", result.Subtype);
        Assert.Contains(nonce, result.Result);

        // The resumed turn was mirrored as well.
        Assert.True(store.GetEntries(new SessionKey { ProjectKey = projectKey, SessionId = result.SessionId }).Count > entries.Count
                    || result.SessionId != seed.SessionId);
    }
}
