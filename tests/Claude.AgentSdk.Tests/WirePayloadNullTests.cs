// SDK -> CLI payloads must not carry null members the CLI would reject (Python
// sends TypedDicts that only contain the keys that were set).

using System.Text.Json;
using Claude.AgentSdk.Internal;
using Claude.AgentSdk.Transport;
using Xunit;

namespace Claude.AgentSdk.Tests;

public sealed class WirePayloadNullTests
{
    private static string ValueOf(List<string> cmd, string flag) => cmd[cmd.IndexOf(flag) + 1];

    private static List<string> Command(ClaudeAgentOptions options) =>
        new SubprocessTransport("test", options with { CliPath = "dummy-claude" }).BuildCommand();

    [Fact]
    public void TypedHookOutput_DefaultSerializationOmitsUnsetMembers()
    {
        var output = new PreToolUseHookSpecificOutput { PermissionDecision = "deny", PermissionDecisionReason = "no writes" };

        const string expected = """{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"no writes"}""";
        Assert.Equal(expected, JsonSerializer.SerializeToElement(output).GetRawText());
        Assert.Equal(expected, output.ToJsonElement().GetRawText());
    }

    [Fact]
    public async Task HookCallback_NullMembersInHookSpecificOutputAreDropped()
    {
        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<HookEvent, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher("Write", [(_, _, _, _) => Task.FromResult(new HookOutput
                {
                    // Hand-built JSON with explicit nulls, as older SDK versions produced.
                    HookSpecificOutput = JsonDocument.Parse("""
                        {"hookEventName":"PreToolUse","permissionDecision":"deny","updatedInput":null,"additionalContext":null}
                        """).RootElement,
                })])]
            }
        };
        var transport = new FakeTransport();
        await using var handler = new QueryHandler(transport, options);
        await handler.StartAsync();
        await handler.InitializeAsync();

        transport.Send(new
        {
            type = "control_request",
            request_id = "hook_1",
            request = new { subtype = "hook_callback", callback_id = "hook_0", input = new { tool_name = "Write" } }
        });
        await transport.WaitForAsync(t => t.ResponsesFor("hook_1").Count > 0);

        var hso = transport.ResponsesFor("hook_1").Single()
            .GetProperty("response").GetProperty("response").GetProperty("hookSpecificOutput");
        Assert.Equal("""{"hookEventName":"PreToolUse","permissionDecision":"deny"}""", hso.GetRawText());
    }

    [Fact]
    public void Sandbox_SettingsOmitUnsetMembers()
    {
        var cmd = Command(new ClaudeAgentOptions
        {
            Sandbox = new SandboxSettings
            {
                Enabled = true,
                AutoAllowBashIfSandboxed = true,
                Network = new SandboxNetworkConfig { AllowLocalBinding = true }
            }
        });

        Assert.Equal(
            """{"sandbox":{"enabled":true,"autoAllowBashIfSandboxed":true,"network":{"allowLocalBinding":true}}}""",
            ValueOf(cmd, "--settings"));
    }

    [Fact]
    public void Sandbox_MergesIntoJsonSettings()
    {
        var cmd = Command(new ClaudeAgentOptions
        {
            Settings = """{"model":"x","n":1}""",
            Sandbox = new SandboxSettings { Enabled = true }
        });

        Assert.Equal("""{"model":"x","n":1,"sandbox":{"enabled":true}}""", ValueOf(cmd, "--settings"));
    }

    [Fact]
    public void McpConfig_OmitsUnsetMembers()
    {
        var cmd = Command(new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, object>
            {
                ["stdio"] = new McpStdioServerConfig { Command = "srv" },
                ["http"] = new McpHttpServerConfig { Url = "https://x" },
            }
        });

        Assert.Equal(
            """{"mcpServers":{"stdio":{"type":"stdio","command":"srv"},"http":{"type":"http","url":"https://x"}}}""",
            ValueOf(cmd, "--mcp-config"));
    }

    [Theory]
    [InlineData("connected", McpServerConnectionStatus.Connected)]
    [InlineData("needs-auth", McpServerConnectionStatus.NeedsAuth)]
    [InlineData("disabled", McpServerConnectionStatus.Disabled)]
    public void McpServerStatus_ParsesCliStrings(string wire, McpServerConnectionStatus expected)
    {
        var json = $$"""{"mcpServers":[{"name":"a","status":"{{wire}}"}]}""";
        var parsed = JsonSerializer.Deserialize(json, SdkJsonContext.Default.McpStatusResponse)!;
        Assert.Equal(expected, parsed.McpServers[0].Status);
        // The reflection serializer honors the same mapping.
        Assert.Equal(expected, JsonSerializer.Deserialize<McpStatusResponse>(json)!.McpServers[0].Status);
    }
}
