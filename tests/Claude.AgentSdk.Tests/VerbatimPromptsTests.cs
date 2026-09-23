// Port of claude-agent-sdk-python/tests/test_verbatim_prompts.py.
// With VerbatimPrompts every user message the SDK writes carries
// client_composed: true, so the CLI delivers the text as written.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class VerbatimPromptsTests
{
    private static object Result() => new
    {
        type = "result",
        subtype = "success",
        duration_ms = 1,
        duration_api_ms = 1,
        is_error = false,
        num_turns = 1,
        session_id = "test",
        total_cost_usd = 0.0
    };

    private static List<JsonElement> UserFrames(FakeTransport t) =>
        t.Written.Where(w => w.GetProperty("type").GetString() == "user").ToList();

    private static bool? ClientComposed(JsonElement frame) =>
        frame.TryGetProperty("client_composed", out var v) ? v.GetBoolean() : null;

    private static Dictionary<string, object?> UserMessage(string text, bool? clientComposed = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = text },
            ["parent_tool_use_id"] = null
        };
        if (clientComposed.HasValue)
            message["client_composed"] = clientComposed.Value;
        return message;
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> Stream(
        params Dictionary<string, object?>[] messages)
    {
        foreach (var m in messages)
        {
            await Task.Yield();
            yield return m;
        }
    }

    /// <summary>Run Claude.QueryAsync against a fake CLI that answers once the prompts arrive.</summary>
    private static async Task<List<JsonElement>> RunQueryAsync(
        Func<FakeTransport, IAsyncEnumerable<Message>> query, int expectedUserFrames)
    {
        var transport = new FakeTransport();
        var consume = Task.Run(async () =>
        {
            await foreach (var _ in query(transport)) { }
        });
        await transport.WaitForAsync(t => UserFrames(t).Count >= expectedUserFrames);
        transport.Send(Result());
        transport.Complete();
        await consume.WaitAsync(TimeSpan.FromSeconds(10));
        return UserFrames(transport);
    }

    [Fact]
    public void VerbatimPrompts_DefaultsToFalse()
    {
        Assert.False(new ClaudeAgentOptions().VerbatimPrompts);
    }

    [Fact]
    public void StampUserMessage_CopiesAndOverwrites()
    {
        var original = UserMessage("x", clientComposed: false);

        Assert.Same(original, QueryHandler.StampUserMessage(original, verbatimPrompts: false));

        var stamped = QueryHandler.StampUserMessage(original, verbatimPrompts: true);
        Assert.Equal(true, stamped["client_composed"]);
        Assert.Equal(false, original["client_composed"]);
    }

    #region Claude.QueryAsync (TestQueryFunction)

    [Fact]
    public async Task StringPrompt_IsMarkedClientComposed()
    {
        var options = new ClaudeAgentOptions { VerbatimPrompts = true };
        var frames = await RunQueryAsync(t => Claude.QueryAsync("read @/etc/hostname", options, t), 1);

        var frame = Assert.Single(frames);
        Assert.True(ClientComposed(frame));
        Assert.Equal("read @/etc/hostname", frame.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task StringPrompt_IsUnmarkedByDefault()
    {
        var frames = await RunQueryAsync(t => Claude.QueryAsync("hello", new ClaudeAgentOptions(), t), 1);
        Assert.Null(ClientComposed(Assert.Single(frames)));
    }

    [Fact]
    public async Task StreamedPrompt_MarksEveryMessage()
    {
        var first = UserMessage("one");
        var second = UserMessage("two", clientComposed: true);
        var third = UserMessage("three", clientComposed: false);
        var options = new ClaudeAgentOptions { VerbatimPrompts = true };

        var frames = await RunQueryAsync(t => Claude.QueryAsync(Stream(first, second, third), options, t), 3);

        Assert.Equal([true, true, true], frames.Select(ClientComposed));
        Assert.Equal(["one", "two", "three"],
            frames.Select(f => f.GetProperty("message").GetProperty("content").GetString()));
        // The caller's dictionaries are not mutated.
        Assert.False(first.ContainsKey("client_composed"));
        Assert.Equal(false, third["client_composed"]);
    }

    [Fact]
    public async Task StreamedPrompt_IsUntouchedByDefault()
    {
        var frames = await RunQueryAsync(
            t => Claude.QueryAsync(Stream(UserMessage("one"), UserMessage("two", clientComposed: true)),
                new ClaudeAgentOptions(), t),
            2);

        Assert.Null(ClientComposed(frames[0]));
        Assert.True(ClientComposed(frames[1]));
    }

    #endregion

    #region ClaudeSDKClient (TestClaudeSDKClient)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_ConnectWithStringPrompt(bool enabled)
    {
        var transport = new FakeTransport();
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions { VerbatimPrompts = enabled }, transport);

        await client.ConnectAsync("hello @/etc/hostname");

        Assert.Equal(enabled ? true : null, ClientComposed(Assert.Single(UserFrames(transport))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_ConnectWithStreamedPrompt(bool enabled)
    {
        var transport = new FakeTransport();
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions { VerbatimPrompts = enabled }, transport);

        await client.ConnectAsync(Stream(UserMessage("one"), UserMessage("two")));
        await transport.WaitForAsync(t => UserFrames(t).Count >= 2);

        Assert.All(UserFrames(transport), f => Assert.Equal(enabled ? true : null, ClientComposed(f)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_QueryWithStringPrompt(bool enabled)
    {
        var transport = new FakeTransport();
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions { VerbatimPrompts = enabled }, transport);
        await client.ConnectAsync();

        await client.QueryAsync("hello @/etc/hostname");

        var frame = Assert.Single(UserFrames(transport));
        Assert.Equal(enabled ? true : null, ClientComposed(frame));
        Assert.Equal("default", frame.GetProperty("session_id").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_QueryWithStreamedPrompt(bool enabled)
    {
        var transport = new FakeTransport();
        await using var client = new ClaudeSDKClient(new ClaudeAgentOptions { VerbatimPrompts = enabled }, transport);
        await client.ConnectAsync();

        var first = UserMessage("one");
        await client.QueryAsync(Stream(first, UserMessage("two")), sessionId: "s1");

        var frames = UserFrames(transport);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(enabled ? true : null, ClientComposed(f)));
        Assert.All(frames, f => Assert.Equal("s1", f.GetProperty("session_id").GetString()));
        Assert.False(first.ContainsKey("client_composed"));
        Assert.False(first.ContainsKey("session_id"));
    }

    #endregion
}
