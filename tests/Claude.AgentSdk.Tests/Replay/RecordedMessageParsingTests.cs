using System.Text.Json;
using Claude.AgentSdk.Internal;
using Xunit;

namespace Claude.AgentSdk.Tests.Replay;

/// <summary>
/// Every message a real CLI (2.1.280) emitted in the recorded fixtures must parse into
/// the right typed message with its fields intact. Catches drift between the SDK's
/// parser and actual CLI output shapes that hand-written JSON in other tests can't.
/// </summary>
public class RecordedMessageParsingTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in Fixture.AllNames()) data.Add(name);
        return data;
    }

    private static bool IsControl(JsonElement m) =>
        m.GetProperty("type").GetString() is "control_request" or "control_response" or "control_cancel_request";

    [Fact]
    public void FixtureSetIsPresent()
    {
        // Guards against the fixtures silently not being copied to the output directory.
        Assert.True(Fixture.AllNames().Count() >= 15, $"expected recorded fixtures in {Fixture.Directory}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryRecordedCliMessage_ParsesToTheExpectedType(string fixture)
    {
        foreach (var raw in Fixture.Load(fixture).Incoming.Where(m => !IsControl(m)))
        {
            var type = raw.GetProperty("type").GetString();
            var parsed = MessageParser.ParseOrNull(raw);
            Assert.True(parsed is not null, $"{fixture}: CLI message of type '{type}' was dropped by the parser");

            switch (type)
            {
                case "assistant":
                {
                    var a = Assert.IsType<AssistantMessage>(parsed);
                    var msg = raw.GetProperty("message");
                    Assert.Equal(msg.GetProperty("model").GetString(), a.Model);
                    Assert.Equal(msg.GetProperty("id").GetString(), a.MessageId);
                    Assert.Equal(raw.GetProperty("session_id").GetString(), a.SessionId);
                    Assert.Equal(raw.GetProperty("uuid").GetString(), a.Uuid);
                    Assert.NotNull(a.Usage);
                    // Every block type the CLI emitted is one the SDK models.
                    Assert.Equal(msg.GetProperty("content").GetArrayLength(), a.Content.Count);
                    foreach (var (block, rawBlock) in a.Content.Zip(msg.GetProperty("content").EnumerateArray()))
                    {
                        switch (block)
                        {
                            case TextBlock t:
                                Assert.Equal(rawBlock.GetProperty("text").GetString(), t.Text);
                                break;
                            case ThinkingBlock th:
                                Assert.Equal(rawBlock.GetProperty("signature").GetString(), th.Signature);
                                break;
                            case ToolUseBlock tu:
                                Assert.Equal(rawBlock.GetProperty("id").GetString(), tu.Id);
                                Assert.Equal(rawBlock.GetProperty("name").GetString(), tu.Name);
                                Assert.Equal(rawBlock.GetProperty("input").GetRawText(), tu.Input.GetRawText());
                                break;
                            default:
                                Assert.Fail($"{fixture}: unexpected block {block.GetType().Name}");
                                break;
                        }
                    }
                    break;
                }
                case "user":
                {
                    var u = Assert.IsType<UserMessage>(parsed);
                    Assert.Equal(raw.GetProperty("message").GetProperty("content").GetRawText(), u.Content.GetRawText());
                    Assert.Equal(raw.GetProperty("uuid").GetString(), u.Uuid);
                    var parent = raw.GetProperty("parent_tool_use_id");
                    Assert.Equal(parent.ValueKind == JsonValueKind.Null ? null : parent.GetString(), u.ParentToolUseId);
                    break;
                }
                case "result":
                {
                    var r = Assert.IsType<ResultMessage>(parsed);
                    Assert.Equal(raw.GetProperty("subtype").GetString(), r.Subtype);
                    Assert.Equal(raw.GetProperty("is_error").GetBoolean(), r.IsError);
                    Assert.Equal(raw.GetProperty("num_turns").GetInt32(), r.NumTurns);
                    Assert.Equal(raw.GetProperty("duration_ms").GetInt32(), r.DurationMs);
                    Assert.Equal(raw.GetProperty("session_id").GetString(), r.SessionId);
                    Assert.Equal(raw.GetProperty("total_cost_usd").GetDecimal(), r.TotalCostUsd);
                    Assert.Equal(raw.GetProperty("uuid").GetString(), r.Uuid);
                    Assert.NotNull(r.Usage);
                    var stop = raw.GetProperty("stop_reason");
                    Assert.Equal(stop.ValueKind == JsonValueKind.Null ? null : stop.GetString(), r.StopReason);
                    if (raw.TryGetProperty("result", out var text))
                        Assert.Equal(text.GetString(), r.Result);
                    break;
                }
                case "stream_event":
                {
                    var s = Assert.IsType<StreamEvent>(parsed);
                    Assert.Equal(raw.GetProperty("event").GetRawText(), s.Event.GetRawText());
                    Assert.Equal(raw.GetProperty("uuid").GetString(), s.Uuid);
                    break;
                }
                case "rate_limit_event":
                {
                    var e = Assert.IsType<RateLimitEvent>(parsed);
                    var info = raw.GetProperty("rate_limit_info");
                    Assert.Equal(info.GetProperty("status").GetString(), e.RateLimitInfo.Status.ToJsonString());
                    Assert.Equal(info.GetProperty("resetsAt").GetInt64(), e.RateLimitInfo.ResetsAt);
                    Assert.Equal(info.GetProperty("rateLimitType").GetString(), e.RateLimitInfo.RateLimitType?.ToJsonString());
                    break;
                }
                case "system":
                {
                    var s = Assert.IsAssignableFrom<SystemMessage>(parsed);
                    Assert.Equal(raw.GetProperty("subtype").GetString(), s.Subtype);
                    Assert.Equal(raw.GetRawText(), s.Data.GetRawText());
                    switch (s.Subtype)
                    {
                        case "task_started":
                            var ts = Assert.IsType<TaskStartedMessage>(s);
                            Assert.Equal(raw.GetProperty("task_id").GetString(), ts.TaskId);
                            Assert.Equal(raw.GetProperty("tool_use_id").GetString(), ts.ToolUseId);
                            Assert.Equal(raw.GetProperty("task_type").GetString(), ts.TaskType);
                            break;
                        case "task_notification":
                            var tn = Assert.IsType<TaskNotificationMessage>(s);
                            Assert.Equal(raw.GetProperty("status").GetString(), tn.Status.ToString().ToLowerInvariant());
                            Assert.Equal(raw.GetProperty("summary").GetString(), tn.Summary);
                            Assert.Equal(raw.GetProperty("output_file").GetString(), tn.OutputFile);
                            break;
                    }
                    break;
                }
                default:
                    Assert.Fail($"{fixture}: fixture contains a CLI message type this test doesn't know: '{type}'");
                    break;
            }
        }
    }

    [Fact]
    public void TaskUpdated_ParsesAsTaskUpdatedMessage()
    {
        var raw = Fixture.Load("subagent").Incoming.Single(m =>
            m.GetProperty("type").GetString() == "system" && m.GetProperty("subtype").GetString() == "task_updated");

        var msg = Assert.IsType<TaskUpdatedMessage>(MessageParser.ParseOrNull(raw));
        Assert.Equal(raw.GetProperty("task_id").GetString(), msg.TaskId);
        Assert.Equal("completed", msg.Status);
        Assert.True(TaskStatus.IsTerminal(msg.Status));
    }

    [Fact]
    public void UserMessage_GetContentBlocks_ParsesRealCliToolResults()
    {
        var checkedResults = 0;
        foreach (var name in Fixture.AllNames())
        {
            foreach (var raw in Fixture.Load(name).Incoming.Where(m => m.GetProperty("type").GetString() == "user"))
            {
                var user = Assert.IsType<UserMessage>(MessageParser.ParseOrNull(raw));
                if (user.Content.ValueKind != JsonValueKind.Array) continue;
                var blocks = user.GetContentBlocks()!;
                foreach (var rawBlock in user.Content.EnumerateArray()
                             .Where(b => b.GetProperty("type").GetString() == "tool_result"))
                {
                    Assert.Contains(blocks.OfType<ToolResultBlock>(),
                        b => b.ToolUseId == rawBlock.GetProperty("tool_use_id").GetString());
                    checkedResults++;
                }
            }
        }
        Assert.True(checkedResults > 5);
    }
}
