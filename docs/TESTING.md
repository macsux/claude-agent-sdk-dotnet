# Testing

The SDK has two test categories.

| | Unit (`tests/Claude.AgentSdk.Tests`) | Integration (`tests/Claude.AgentSdk.IntegrationTests`) |
|---|---|---|
| Talks to | fakes and recorded fixtures | the real `claude` CLI and the Anthropic API |
| Cost | free | real money (about $0.30 for a full run on haiku) |
| Speed | about 10 s | about 1 min (tests run 4 at a time) |
| Deterministic | yes | no (model output varies) |
| Runs by default | yes | no: every test is **skipped** unless you opt in |

## Unit tests

```sh
dotnet test tests/Claude.AgentSdk.Tests
```

A plain `dotnet test` of the solution runs the unit tests and skips every integration test, so it
never spends money.

### Replay tests (recorded real-CLI traffic)

`tests/Claude.AgentSdk.Tests/Replay/` replays JSONL recordings of real CLI sessions from
`tests/Claude.AgentSdk.Tests/Fixtures/` through the real SDK code: `Claude.QueryAsync` /
`ClaudeSDKClient` → `QueryHandler` → `MessageParser`, plus the hook, `can_use_tool` and SDK-MCP
handlers. `ReplayTransport` runs in lock-step with the SDK:

- `in` lines (CLI stdout) are fed to the SDK in recorded order.
- `out` lines (what the SDK wrote to stdin during the recording) are expectations. Replay pauses
  until the live SDK writes a matching frame. A control request must have the same subtype, and a
  control response must answer the same CLI request id with the same success or error outcome. If
  no matching frame arrives within 10 s, the test fails with `ReplayMismatchException`.
- Control requests that the SDK starts (such as `initialize`, `interrupt` and `mcp_status`) get
  fresh ids on every run, so replay maps each recorded id to the live one.
- An `exit` line throws the same `ProcessException` that a non-zero CLI exit throws.

`RecordedMessageParsingTests` also parses every recorded CLI message and checks that its typed
fields match the raw JSON.

Fixture format, one JSON object per line:

```jsonl
{"fixture":"Hook.PreToolUse_Deny_PreventsFileWrite","model":"claude-haiku-4-5-20251001","recorded_at":"2026-09-23","note":"..."}
{"dir":"out","msg":{"type":"control_request","request_id":"req_1_…","request":{"subtype":"initialize",…}}}
{"dir":"in","msg":{"type":"system","subtype":"init",…}}
{"dir":"out","end_input":true}
{"dir":"exit","exit_code":1,"error":"Command failed"}
```

## Integration tests

```sh
CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS=1 dotnet test tests/Claude.AgentSdk.IntegrationTests \
  --logger "console;verbosity=detailed"
# only the integration category from the whole solution:
CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS=1 dotnet test --filter Category=Integration
```

Requirements: an installed, authenticated Claude Code CLI. The suite finds the CLI through
`CLAUDE_CLI_PATH`, then `PATH`, then `~/.local/bin/claude`. If no CLI is found, every test is
skipped with a message saying so.

Some tests (`TsParityTests.Elicitation_*`) start a small stdio MCP server with `python3`; without
it they log and return.

The tests assert protocol-level facts rather than exact model wording, for example:

- a denied `Write` leaves no file on disk;
- a hook or permission callback received the real tool name, input and `tool_use_id`;
- an SDK MCP tool received the exact arguments;
- a resumed session recalls a random codename planted in an earlier process;
- the SessionStore mirrored entries.

Prompts are written so that tool use is almost certain. Tests whose precondition is "the model
calls tool X" use `ModelCompliance.RetryOnceAsync`. It retries once, and only when the model did
not make the call. SDK assertion failures are never retried.

Each test runs in isolation:

- It gets a fresh temp working directory, which is deleted afterwards together with the CLI's
  `~/.claude/projects/<cwd-key>/` transcripts.
- `SettingSources = []` and `StrictMcpConfig = true` keep your own settings, CLAUDE.md, hooks, MCP
  servers and plugins out of the run.
- Built-in tools are off unless the test enables them.
- Variables that a parent Claude Code session exports are blanked for the CLI under test, so
  running the suite from inside Claude Code behaves like running it from a terminal.

### Cost and latency controls

All of these live in `Infrastructure/IntegrationTestBase.cs`:

- model `claude-haiku-4-5-20251001` by default
- `MaxTurns = 3` and `MaxBudgetUsd = 0.25` per test
- a 120 s per-test timeout
- a suite-wide spend cap: once the total reaches it, the remaining tests fail fast

Each test prints its cost and the running total to the test output. The per-test table and the
total are written to `bin/<config>/net10.0/integration-costs.log` and to stderr when the test
process exits. A full run costs about $0.30 on haiku and takes about 1.5 minutes.

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS` | unset | Set to `1` or `true` to run the integration tests; otherwise they are skipped. |
| `CLAUDE_AGENT_SDK_IT_MODEL` | `claude-haiku-4-5-20251001` | Model for all integration tests. |
| `CLAUDE_AGENT_SDK_IT_TIMEOUT_SECONDS` | `120` | Per-test timeout. |
| `CLAUDE_AGENT_SDK_IT_MAX_TOTAL_USD` | `5` | Spend cap for the whole run. |
| `CLAUDE_AGENT_SDK_RECORD_FIXTURES` | unset | Directory to write JSONL recordings to (see below). |
| `CLAUDE_CLI_PATH` | – | Explicit CLI binary. |

### Known SDK bugs

Tests that exposed SDK bugs are marked `Skip = "SDK bug: …"`, and the skip reason describes the
bug. To list them, search for `SDK bug:`. Remove the `Skip` once the bug is fixed.

## Re-recording fixtures

1. Run the integration tests with recording enabled:

   ```sh
   CLAUDE_AGENT_SDK_RUN_INTEGRATION_TESTS=1 \
   CLAUDE_AGENT_SDK_RECORD_FIXTURES=/tmp/sdk-fixtures \
   dotnet test tests/Claude.AgentSdk.IntegrationTests --filter "FullyQualifiedName~McpTests"
   ```

   Each test that passes its client or query through `Transport(options)` writes
   `<Class>.<Test>.jsonl`. Tests that resume from a SessionStore are not recorded, because a custom
   transport bypasses the SDK's store materialization.

2. `RecordingTransport` scrubs every line before writing it:

   - home, working-directory and temp paths become `{HOME}`, `{CWD}` and `{TMP}`, including their
     sanitized `-Users-…` project-key forms;
   - e-mail addresses become `user@example.com`;
   - account, organization, subscription, `apiKeySource` and messaging-socket values become
     `REDACTED`;
   - Anthropic API request ids become `req_redacted`;
   - command and skill catalogs are truncated;
   - agents that are neither built in nor SDK-defined are dropped.

3. **Review every file before committing.** Search at least for your user name, home path,
   e-mail, organization name and any `sk-ant` string:

   ```sh
   grep -i -E "$(whoami)|/Users/|/home/|@|sk-ant|organization" /tmp/sdk-fixtures/*.jsonl
   ```

4. Copy the files you want into `tests/Claude.AgentSdk.Tests/Fixtures/` under a short snake_case
   name. Replay tests load fixtures by that name. If you replace a fixture, update the values that
   its replay test reads from it (for example the MCP arguments or the codename).
