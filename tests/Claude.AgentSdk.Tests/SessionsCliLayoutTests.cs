// ClaudeSessions over a FileSessionStore rooted at a CLI-style projects directory:
// transcripts the CLI wrote directly (no .summaries sidecars).

using System.Text.Json.Nodes;
using Claude.AgentSdk.Sessions;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class SessionsCliLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sdk-cli-layout").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteCliTranscript(string sessionId, string lastTimestamp)
    {
        var projectDir = Path.Combine(_root, ParityUtil.ProjectKey);
        Directory.CreateDirectory(projectDir);
        var user = ParityUtil.User("hello from the cli", Guid.NewGuid().ToString(), null, sessionId);
        var assistant = ParityUtil.Assistant("hi", Guid.NewGuid().ToString(), user["uuid"]!.GetValue<string>(), sessionId);
        assistant["timestamp"] = lastTimestamp;
        var path = Path.Combine(projectDir, sessionId + ".jsonl");
        File.WriteAllLines(path, [user.ToJsonString(), assistant.ToJsonString()]);
        return path;
    }

    [Fact]
    public async Task ListSessions_FindsCliWrittenTranscriptsWithoutSidecars()
    {
        var sid = Guid.NewGuid().ToString();
        WriteCliTranscript(sid, "2024-01-01T00:00:05.000Z");
        var store = new FileSessionStore(_root);

        var sessions = await ClaudeSessions.ListSessionsAsync(store, ParityUtil.Dir);

        var info = Assert.Single(sessions);
        Assert.Equal(sid, info.SessionId);
        Assert.Equal("hello from the cli", info.FirstPrompt);
        Assert.True(info.LastModified > 0);
    }

    [Fact]
    public async Task GetSessionInfo_LastModifiedIsTheLastEntryTimestamp()
    {
        var sid = Guid.NewGuid().ToString();
        WriteCliTranscript(sid, "2024-01-01T00:00:05.000Z");
        var store = new FileSessionStore(_root);

        var info = await ClaudeSessions.GetSessionInfoAsync(store, sid, ParityUtil.Dir);

        Assert.NotNull(info);
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:05Z").ToUnixTimeMilliseconds(), info!.LastModified);
    }
}
