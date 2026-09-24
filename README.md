# Claude Agent SDK for .NET

A modern .NET library for interacting with the Claude Code CLI, providing both a simple one-shot `QueryAsync()` API and a full bidirectional client with control-protocol support.

[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)]()
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-blue)]()
[![License](https://img.shields.io/badge/license-MIT-green)]()

**Disclaimer:** This is an independent, unofficial port and is not affiliated with or endorsed by Anthropic, PBC.

## Features

- Simple `Claude.QueryAsync()` API for one-shot requests, plus `QueryTextAsync` and typed `QueryAsync<T>`
- `ClaudeSDKClient` for multi-turn, bidirectional conversations
- Control protocol support (interrupts, modes, dynamic model switching)
- Hook system (PreToolUse, PostToolUse, UserPromptSubmit)
- Tool permission callbacks with allow/deny control
- In-process MCP server support (tools, prompts, resources), including `[McpTool]` attribute classes
- `ILogger` integration and a `ScriptedTransport` for unit-testing agent code without the CLI
- Cross-platform: Windows, Linux, macOS
- Trim- and NativeAOT-compatible (source-generated JSON throughout)
- Tested against the real Claude Code CLI (opt-in integration suite) and recorded CLI traffic

> **Parity:** Tracks Python `claude-agent-sdk` v0.2.158. See [docs/PARITY.md](docs/PARITY.md).

## Prerequisites

- .NET 10.0
- Claude Code CLI >= 2.0.0:
  ```bash
  npm install -g @anthropic-ai/claude-code
  ```

### CLI Path Resolution

The SDK discovers the Claude Code CLI in this order:

1. `ClaudeAgentOptions.CliPath` (explicit path)
2. `CLAUDE_CLI_PATH` environment variable
3. `PATH` search for `claude` (or `claude.cmd` on Windows)

## Quick Start

### One-Shot Query

```csharp
using Claude.AgentSdk;

await foreach (var msg in Claude.QueryAsync("What is 2+2?"))
{
    if (msg is AssistantMessage am)
        foreach (var block in am.Content)
            if (block is TextBlock tb)
                Console.Write(tb.Text);
}
```

### Multi-Turn Conversation

```csharp
using Claude.AgentSdk;

await using var client = new ClaudeSDKClient();
await client.ConnectAsync();

await client.QueryAsync("Write a Python hello world");

await foreach (var msg in client.ReceiveResponseAsync())
{
    if (msg is AssistantMessage am)
        foreach (var block in am.Content)
            if (block is TextBlock tb)
                Console.Write(tb.Text);
}
```

### With Options

```csharp
var options = Claude.Options()
    .SystemPrompt("You are a helpful coding assistant.")
    .MaxTurns(5)
    .Model("claude-sonnet-4-20250514")
    .AcceptEdits()
    .Build();

await foreach (var msg in Claude.QueryAsync("Explain async/await", options))
{
    // handle messages
}
```

### Tool Permission Callback

```csharp
var options = Claude.Options()
    .CanUseTool(async (toolName, input, context, ct) =>
    {
        if (toolName == "Bash" && input.GetProperty("command").GetString()?.Contains("rm") == true)
            return new PermissionResultDeny("Destructive commands not allowed");
        return new PermissionResultAllow();
    })
    .Build();
```

### Hooks

```csharp
var options = Claude.Options()
    .AllowTools("Bash")
    .Hooks(h => h
        .PreToolUse("Bash", (input, toolUseId, ctx, ct) =>
        {
            Console.WriteLine($"[Hook] Bash: {input}");
            return Task.FromResult(new HookOutput { Continue = true });
        }))
    .Build();
```

### One-Call Helpers

```csharp
// Final answer as text (throws ResultException on max-turns/budget/API errors)
string answer = await Claude.QueryTextAsync("What is 17 * 3?");

// Typed structured output; the JSON schema comes from source-generated metadata
Forecast f = await Claude.QueryAsync("Forecast for Lisbon tomorrow", MyJson.Default.Forecast);

[JsonSerializable(typeof(Forecast))]
partial class MyJson : JsonSerializerContext;
```

`Claude.QueryAsync<Forecast>(prompt)` (no `JsonTypeInfo`) also works, using reflection (not AOT-safe).

### Logging

Set `ClaudeAgentOptions.Logger` (or `.Logger(...)` on the builder) to any `ILogger`. The SDK logs CLI
discovery/spawn (flag names only), exit codes, control-protocol traffic (subtypes and ids), callback
failures and warnings. Prompts, argument values, environment and message contents are never logged.

### MCP Tools (In-Process)

```csharp
using Claude.AgentSdk;
using Claude.AgentSdk.Mcp;

var options = Claude.Options()
    .McpServers(m => m.AddSdk("calculator", s => s
        .Tool("add", (double a, double b) => a + b, "Add two numbers")))
    .AllowAllTools()
    .Build();
```

Or mark methods with `[McpTool]` and register a whole class. Passing a `JsonSerializerContext` keeps it
trim/NativeAOT-safe (one arguments record per tool); without one, any parameter list works via reflection.

```csharp
public sealed class InventoryTools
{
    [McpTool("stock_level", Description = "Units in stock for a SKU", ReadOnly = true)]
    public string StockLevel(StockArgs args) => ...;
}

var servers = McpServers.Sdk("inventory", b => b.ToolsFrom(new InventoryTools(), MyJson.Default));
```

### Testing Your Agent Code

`Claude.AgentSdk.Testing.ScriptedTransport` plays the CLI's side of a conversation without the CLI:
deterministic, instant, free. Permission prompts, hook callbacks and MCP tool calls pause the script
until your code answers, so your callbacks run exactly as they would live.

```csharp
var cli = new ScriptedTransport().Turn(t => t
    .PermissionRequest("Write", """{"file_path":"/etc/hosts","content":"x"}""")
    .Result("I can't write there."));

await Claude.QueryTextAsync("update /etc/hosts", myOptions, cli);
Assert.Equal("deny", cli.PermissionResponses[0].GetProperty("behavior").GetString());
```

Custom transports (e.g. a CLI over SSH or in a container) can reuse `StreamJsonReader` for the
bounded, forward-compatible stream-json parsing that `SubprocessTransport` uses.

### Trimming and NativeAOT

The library is trim- and NativeAOT-compatible (`IsAotCompatible`). The delegate-based
`Tool(name, Delegate, ...)` shown above infers schemas by reflection and is annotated
`[RequiresUnreferencedCode]`; in trimmed/AOT apps register tools with an explicit JSON schema or
with source-generated `JsonTypeInfo<TArgs>`:

```csharp
var schema = JsonDocument.Parse("""
    {"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}
    """).RootElement;

var options = Claude.Options()
    .McpServers(m => m.AddSdk("calculator", s => s
        // Raw JSON arguments, validated against the schema.
        .Tool("add", schema, (args, ct) => Task.FromResult(McpToolResults.Text(
            (args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()).ToString())))
        // Typed arguments; the schema is exported from the same metadata.
        .Tool("mul", MyJsonContext.Default.MulArgs, (m, ct) => Task.FromResult(McpToolResults.Text((m.X * m.Y).ToString())))))
    .Build();

record MulArgs(int X, int Y);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MulArgs))]
partial class MyJsonContext : JsonSerializerContext;
```

Values placed in `Dictionary<string, object?>` payloads (prompt streams, MCP `_meta`) should be
primitives, collections, `JsonElement` or `JsonNode`; other objects need reflection-based
serialization, which trimmed/AOT apps disable. Typed hook outputs have `ToJsonElement()`.
`tests/Claude.AgentSdk.AotSmoke` publishes a NativeAOT app that exercises this surface.

### Custom Agents

```csharp
var options = Claude.Options()
    .Agents(a => a
        .Add("reviewer", "Reviews code", "You are a code reviewer.", "Read", "Grep")
        .Add("writer", "Writes code", "You are a clean coder.", tools: ["Read", "Write"]))
    .Build();
```

### Sandbox Configuration

```csharp
var options = Claude.Options()
    .Sandbox(s => s
        .Enable()
        .AutoAllowBash()
        .ExcludeCommands("rm", "sudo")
        .Network(n => n.AllowLocalBinding()))
    .Build();
```

## Configuration

`ClaudeAgentOptions` mirrors the Python SDK's options:

| Property | Type | Description |
|----------|------|-------------|
| `SystemPrompt` | `SystemPromptConfig?` | A string (implicit), `SystemPromptPreset`, `SystemPromptCustom` or `SystemPromptFile` |
| `MaxTurns` | `int?` | Maximum conversation turns |
| `MaxBudgetUsd` | `decimal?` | Spending limit in USD |
| `Model` | `string?` | Model to use |
| `FallbackModel` | `string?` | Fallback model |
| `PermissionMode` | `PermissionMode?` | Default, AcceptEdits, Plan, BypassPermissions, DontAsk, Auto |
| `McpServers` | `McpServersConfig?` | A `Dictionary<string, object>` of servers (implicit) or a config path / JSON string |
| `Skills` | `SkillsConfig?` | `"all"` or a list of skill names (both implicit) |
| `ResumeSessionAt` / `ResumeDropsTurn` | `string?` | Truncating resume (`--resume-session-at` / `--resume-drops-turn`) |
| `ForwardSubagentText` | `bool` | Forward subagent text/thinking blocks |
| `VerbatimPrompts` | `bool` | Mark prompts `client_composed` (no `@path` expansion / slash commands) |
| `LoadTimeoutMs` | `int` | SessionStore load timeout during resume (default 60000) |
| `CanUseTool` | `CanUseToolCallback?` | Tool permission callback |
| `Hooks` | `IReadOnlyDictionary<...>?` | Event hooks |
| `AllowedTools` | `IReadOnlyList<string>` | Whitelist tools |
| `DisallowedTools` | `IReadOnlyList<string>` | Blacklist tools |
| `Cwd` | `string?` | Working directory |
| `CliPath` | `string?` | Explicit CLI path |

## Message Types

- `AssistantMessage` - Claude's response with `Content` blocks
- `UserMessage` - User input
- `SystemMessage` - System notifications
- `ResultMessage` - Query completion with cost/duration info

### Content Blocks

- `TextBlock` - Text content
- `ThinkingBlock` - Extended thinking (with signature)
- `ToolUseBlock` - Tool invocation
- `ToolResultBlock` - Tool output

## Examples

See the `examples/` directory:

| Example | Description |
|---------|-------------|
| `BasicQuery` | Simple one-shot query |
| `StreamingMode` | Interactive bidirectional client |
| `SystemPrompt` | Custom system prompts |
| `McpCalculator` | In-process MCP tools |
| `McpPrompts` | MCP prompt templates |
| `Hooks` | Pre/post tool use hooks |
| `ToolPermissionCallback` | Permission control |
| `Agents` | Agent configurations |
| `MaxBudget` | Spending limits |
| `Convenience` | `QueryTextAsync`, typed `QueryAsync<T>`, `[McpTool]` classes, `ILogger` |
| `TestingYourAgent` | Unit-testing agent code with `ScriptedTransport` |

## Status & Parity

- **Current version:** 0.1.0
- **Status:** Preview (API and behavior may change)
- **Parity:** Designed to match the Python Claude Agent SDK API, behavior, and ergonomics
- **Tests:** unit tests (including replays of recorded real-CLI traffic), a live-CLI integration
  suite, and a NativeAOT smoke app. See [docs/TESTING.md](docs/TESTING.md).
- **Parity inventory:** [docs/PARITY.md](docs/PARITY.md)

### Running Integration Tests

Integration tests talk to the real Claude Code CLI (latency, non-determinism, real cost — about
$0.25 per full run on Haiku) and are skipped unless enabled:

- `CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS=1 dotnet test tests/Claude.AgentSdk.IntegrationTests`

**Canonical rule:** The Python `claude-agent-sdk` is the canonical reference. This .NET port tracks its behavior and API.

## Installation

### NuGet Package (Coming Soon)

```bash
dotnet add package Claude.AgentSdk
```

### From Source

```bash
git clone https://github.com/anthropics/claude-agent-sdk-dotnet.git
cd claude-agent-sdk-dotnet
dotnet build
```

## Related Projects

| Project | Language | Description |
|---------|----------|-------------|
| [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python) | Python | Official Python SDK (canonical reference) |
| [claude-agent-sdk-cpp](https://github.com/0xeb/claude-agent-sdk-cpp) | C++ | C++ port with full feature parity |

## License

Licensed under the MIT License. See `LICENSE` for details.

This is a .NET port of [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python) by Anthropic, PBC.

## Support

- Issues: Use the GitHub issue tracker
- Examples: See `examples/`
- Tests: See `tests/`
