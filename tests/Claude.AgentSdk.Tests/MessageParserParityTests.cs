// Port of the v0.2.158 cases in claude-agent-sdk-python/tests/test_message_parser.py
// and the ResultError replacement in tests/test_query.py / tests/test_errors.py.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Transport;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class MessageParserParityTests
{
    private static Message? Parse(string json) => MessageParser.ParseOrNull(JsonDocument.Parse(json).RootElement);

    private const string ResultBase =
        "\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":1000,\"duration_api_ms\":500," +
        "\"is_error\":false,\"num_turns\":2,\"session_id\":\"session_123\"";

    #region user

    // origin is surfaced on user messages, for both content shapes, and passed
    // through with keys this SDK version doesn't model.
    [Theory]
    [InlineData("\"hi\"")]
    [InlineData("[{\"type\":\"text\",\"text\":\"hi\"}]")]
    public void User_Origin(string content)
    {
        var msg = Assert.IsType<UserMessage>(Parse(
            $$$"""{"type":"user","message":{"content":{{{content}}}},"origin":{"kind":"peer","from":"peer-addr","name":"other-session","verifiedPeerPid":4242,"someFutureField":true}}"""));

        Assert.NotNull(msg.Origin);
        Assert.Equal("peer", msg.Origin!.Kind);
        Assert.Equal("peer-addr", msg.Origin.From);
        Assert.Equal("other-session", msg.Origin.Name);
        Assert.Equal(4242, msg.Origin.VerifiedPeerPid);
        Assert.True(msg.Origin.AdditionalProperties!["someFutureField"].GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"origin\":null")]
    [InlineData(",\"origin\":\"human\"")]
    [InlineData(",\"origin\":{}")]
    public void User_OriginAbsentOrMalformed_IsNull(string extra)
    {
        var msg = Assert.IsType<UserMessage>(Parse($$"""{"type":"user","message":{"content":"hi"}{{extra}}}"""));
        Assert.Null(msg.Origin);
    }

    [Fact]
    public void User_ToolUseResult()
    {
        var msg = Assert.IsType<UserMessage>(Parse("""
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"The file has been updated."}]},
             "parent_tool_use_id":null,"uuid":"2ace3375","tool_use_result":{"filePath":"/path/to/file.py","structuredPatch":[{"oldStart":33}]}}
            """));

        Assert.Equal("/path/to/file.py", msg.ToolUseResult!.Value.GetProperty("filePath").GetString());
        Assert.Equal(33, msg.ToolUseResult.Value.GetProperty("structuredPatch")[0].GetProperty("oldStart").GetInt32());
        Assert.Equal("2ace3375", msg.Uuid);
        var block = Assert.IsType<ToolResultBlock>(Assert.Single(msg.GetContentBlocks()!));
        Assert.Equal("toolu_1", block.ToolUseId);
    }

    [Fact]
    public void User_StringContentWithToolUseResult()
    {
        var msg = Assert.IsType<UserMessage>(Parse(
            """{"type":"user","message":{"content":"Simple string content"},"tool_use_result":{"userModified":true}}"""));
        Assert.Equal("Simple string content", msg.GetTextContent());
        Assert.True(msg.ToolUseResult!.Value.GetProperty("userModified").GetBoolean());
    }

    // Python's user branch only materializes text/tool_use/tool_result; an
    // image block must not make GetContentBlocks throw.
    [Fact]
    public void User_GetContentBlocks_SkipsUnmodelledBlocks()
    {
        var msg = Assert.IsType<UserMessage>(Parse(
            """{"type":"user","message":{"content":[{"type":"image","source":{}},{"type":"text","text":"t"},{"type":"tool_result","tool_use_id":"x","is_error":null}]}}"""));
        var blocks = msg.GetContentBlocks()!;
        Assert.Equal(2, blocks.Count);
        Assert.IsType<TextBlock>(blocks[0]);
        Assert.Null(Assert.IsType<ToolResultBlock>(blocks[1]).IsError);
    }

    [Fact]
    public void User_NonObjectContentBlock_Throws()
    {
        Assert.Throws<MessageParseException>(() => Parse("""{"type":"user","message":{"content":["oops"]}}"""));
    }

    #endregion

    #region assistant

    // The error field is at the top level of the data, not inside message.
    [Fact]
    public void Assistant_ErrorIsReadFromTopLevel()
    {
        var msg = Assert.IsType<AssistantMessage>(Parse("""
            {"type":"assistant","message":{"content":[{"type":"text","text":"Invalid API key"}],"model":"<synthetic>"},
             "session_id":"test-session","error":"authentication_failed"}
            """));
        Assert.Equal(AssistantMessageError.AuthenticationFailed, msg.Error);
        Assert.IsType<TextBlock>(Assert.Single(msg.Content));
    }

    [Fact]
    public void Assistant_UnknownErrorMapsToUnknown()
    {
        var msg = Assert.IsType<AssistantMessage>(Parse(
            """{"type":"assistant","message":{"content":[],"model":"m"},"error":"something_new"}"""));
        Assert.Equal(AssistantMessageError.Unknown, msg.Error);
    }

    [Fact]
    public void Assistant_StringContent_Throws()
    {
        Assert.Throws<MessageParseException>(() =>
            Parse("""{"type":"assistant","message":{"content":"hi","model":"m"}}"""));
    }

    #endregion

    #region system

    [Fact]
    public void TaskUpdated_IsTyped()
    {
        var msg = Assert.IsType<TaskUpdatedMessage>(Parse(
            """{"type":"system","subtype":"task_updated","task_id":"t1","patch":{"status":"killed","end_time":1},"session_id":"s","uuid":"u"}"""));
        Assert.Equal("t1", msg.TaskId);
        Assert.Equal("killed", msg.Status);
        Assert.True(TaskStatus.IsTerminal(msg.Status));
        Assert.Equal(1, msg.Patch.GetProperty("end_time").GetInt32());
        Assert.Equal("s", msg.SessionId);
        Assert.IsAssignableFrom<SystemMessage>(msg);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"patch\":\"nope\"")]
    [InlineData(",\"patch\":[1]")]
    [InlineData(",\"patch\":{\"end_time\":5}")]
    public void TaskUpdated_ParsesDefensively(string patch)
    {
        var msg = Assert.IsType<TaskUpdatedMessage>(Parse($$"""{"type":"system","subtype":"task_updated"{{patch}}}"""));
        Assert.Equal("", msg.TaskId);
        Assert.Null(msg.Status);
        Assert.Equal(JsonValueKind.Object, msg.Patch.ValueKind);
        Assert.Null(msg.SessionId);
    }

    #endregion

    #region result

    [Fact]
    public void Result_TerminalReasonAndOrigin()
    {
        var msg = Assert.IsType<ResultMessage>(Parse($$$"""{{{{ResultBase}}},"terminal_reason":"aborted_tools","origin":{"kind":"task-notification","subkind":"scheduled-trigger"}}"""));
        Assert.Equal("aborted_tools", msg.TerminalReason);
        Assert.Equal(MessageOriginKind.TaskNotification, msg.Origin!.Kind);
        Assert.Equal(TaskNotificationOriginSubkind.ScheduledTrigger, msg.Origin.Subkind);
        Assert.False(msg.Origin.IsHuman);
    }

    [Fact]
    public void Result_OptionalFieldsAbsentAreNull()
    {
        var msg = Assert.IsType<ResultMessage>(Parse($$"""{{{ResultBase}}}"""));
        Assert.Null(msg.TerminalReason);
        Assert.Null(msg.Origin);
        Assert.Null(msg.ModelUsage);
        Assert.Null(msg.PermissionDenials);
    }

    [Fact]
    public void Result_ModelUsageAndPermissionDenials()
    {
        var msg = Assert.IsType<ResultMessage>(Parse($$$"""
            {{{{ResultBase}}},"modelUsage":{"claude-sonnet-4-5-20250929":{"inputTokens":3,"outputTokens":24,"cacheReadInputTokens":20012,
             "costUSD":0.0106,"contextWindow":200000,"maxOutputTokens":64000,"canonicalModel":"claude-sonnet-4-5","provider":"firstParty"}},
             "permission_denials":[],"uuid":"d379c496"}
            """));

        var usage = msg.ModelUsage!["claude-sonnet-4-5-20250929"];
        Assert.Equal(0.0106, usage.CostUSD);
        Assert.Equal(20012, usage.CacheReadInputTokens);
        Assert.Equal("claude-sonnet-4-5", usage.CanonicalModel);
        Assert.Equal("firstParty", usage.Provider);
        Assert.Equal(0, msg.PermissionDenials!.Value.GetArrayLength());
        Assert.Equal("d379c496", msg.Uuid);
    }

    #endregion

    #region other message types

    [Fact]
    public void ConversationReset_IsTyped()
    {
        var msg = Assert.IsType<ConversationResetMessage>(Parse(
            """{"type":"conversation_reset","new_conversation_id":"d2f4a573","uuid":"msg-1","session_id":"66694129"}"""));
        Assert.Equal("d2f4a573", msg.NewConversationId);
        Assert.Equal("msg-1", msg.Uuid);
        Assert.Equal("66694129", msg.SessionId);
        Assert.IsAssignableFrom<Message>(msg);
    }

    [Fact]
    public void ConversationReset_MissingField_Throws()
    {
        var ex = Assert.Throws<MessageParseException>(() =>
            Parse("""{"type":"conversation_reset","uuid":"u","session_id":"s"}"""));
        Assert.Contains("new_conversation_id", ex.Message);
    }

    [Fact]
    public void RateLimitEvent_UnknownStatusDoesNotFailTheStream()
    {
        var msg = Assert.IsType<RateLimitEvent>(Parse(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"brand_new","overageStatus":"also_new"},"uuid":"u","session_id":"s"}"""));
        Assert.Equal(RateLimitStatus.Unknown, msg.RateLimitInfo.Status);
        Assert.Equal(RateLimitStatus.Unknown, msg.RateLimitInfo.OverageStatus);
        Assert.Equal("brand_new", msg.RateLimitInfo.Raw!.Value.GetProperty("status").GetString());
    }

    #endregion

    #region ResultException replaces the trailing ProcessException (test_query.py)

    /// <summary>Wraps <see cref="FakeTransport"/>; the CLI "exits non-zero" once the fake stream completes.</summary>
    private sealed class ExitingTransport(FakeTransport inner, int exitCode) : ITransport
    {
        public bool IsReady => true;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAsync(string data, CancellationToken cancellationToken = default) => inner.WriteAsync(data, cancellationToken);
        public Task EndInputAsync(CancellationToken cancellationToken = default) => inner.EndInputAsync(cancellationToken);
        public Task CloseAsync() => inner.CloseAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var m in inner.ReadMessagesAsync(cancellationToken))
                yield return m;
            throw new ProcessException("Command failed with exit code 1", exitCode, "Check stderr output for details");
        }
    }

    private static async Task<Exception> RunToFailureAsync(object resultFrame)
    {
        var fake = new FakeTransport();
        var transport = new ExitingTransport(fake, 1);
        var messages = new List<Message>();
        var consume = Task.Run(async () =>
        {
            await foreach (var m in Claude.QueryAsync("hi", new ClaudeAgentOptions(), transport))
                messages.Add(m);
        });
        await fake.WaitForAsync(t => t.Written.Any(w => w.GetProperty("type").GetString() == "user"));
        fake.Send(resultFrame);
        fake.Complete();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => consume.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<ResultMessage>(Assert.Single(messages));
        return ex;
    }

    [Fact]
    public async Task ErrorResult_ThenExit_RaisesTypedResultException()
    {
        var ex = await RunToFailureAsync(new
        {
            type = "result",
            subtype = "error_max_turns",
            duration_ms = 1,
            duration_api_ms = 1,
            is_error = true,
            num_turns = 3,
            session_id = "sess",
            errors = new[] { "Reached maximum number of turns (3)" },
            terminal_reason = "max_turns"
        });

        var rex = Assert.IsType<ResultException>(ex);
        Assert.StartsWith("Claude Code returned an error result: Reached maximum number of turns (3)", rex.Message);
        Assert.Contains("(exit code: 1)", rex.Message);
        Assert.DoesNotContain("Check stderr", rex.Message);
        Assert.Equal("error_max_turns", rex.Subtype);
        Assert.Equal("max_turns", rex.TerminalReason);
        Assert.Equal("sess", rex.SessionId);
        Assert.Equal(1, rex.ExitCode);
        Assert.Equal(["Reached maximum number of turns (3)"], rex.Errors);
        Assert.IsType<ProcessException>(rex.InnerException);
        Assert.Equal("error_max_turns", rex.RawResult!.Value.GetProperty("subtype").GetString());
    }

    // An API failure arrives as subtype "success" + is_error with the prose in
    // `result`; the text must not read "...error result: success".
    [Fact]
    public async Task ApiErrorResult_UsesResultText()
    {
        var ex = await RunToFailureAsync(new
        {
            type = "result",
            subtype = "success",
            duration_ms = 1,
            duration_api_ms = 1,
            is_error = true,
            num_turns = 1,
            session_id = "sess",
            result = "API Error: 529 overloaded",
            api_error_status = 529,
            errors = Array.Empty<string>()
        });

        var rex = Assert.IsType<ResultException>(ex);
        Assert.StartsWith("Claude Code returned an error result: API Error: 529 overloaded", rex.Message);
        Assert.Equal(529, rex.ApiErrorStatus);
        Assert.Equal("API Error: 529 overloaded", rex.Result);
    }

    [Fact]
    public async Task SuccessfulResult_ThenExit_KeepsProcessException()
    {
        var ex = await RunToFailureAsync(new
        {
            type = "result",
            subtype = "success",
            duration_ms = 1,
            duration_api_ms = 1,
            is_error = false,
            num_turns = 1,
            session_id = "sess"
        });
        Assert.IsType<ProcessException>(ex);
    }

    [Theory]
    [InlineData("""{"errors":["a"," ","b"],"result":"r","subtype":"error_x"}""", "a; b")]
    [InlineData("""{"errors":[],"result":"  API Error: x  ","subtype":"success"}""", "API Error: x")]
    [InlineData("""{"errors":[],"result":"","subtype":"error_during_execution"}""", "error_during_execution")]
    [InlineData("""{"subtype":"success","api_error_status":500}""", "API error (HTTP 500)")]
    [InlineData("""{"subtype":"success"}""", "unknown error")]
    public void ErrorResultText_PrefersErrorsThenResultThenSubtypeThenStatus(string json, string expected)
    {
        Assert.Equal(expected, ResultException.ErrorResultText(JsonDocument.Parse(json).RootElement));
    }

    #endregion
}
