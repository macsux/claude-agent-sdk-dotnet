# Python SDK parity inventory

Reference: `claude-agent-sdk-python` **0.2.158** (bundled CLI 2.1.280),
`src/claude_agent_sdk`. Status legend:

- **ok**: present and behaves like Python.
- **added**: was missing in .NET; added in this pass.
- **fixed**: present but divergent; aligned in this pass.
- **typed**: was `object`; replaced by a typed union with implicit conversions.
- **diff**: intentional .NET difference (reason given).
- **follow-up**: out of scope for this pass; see [Follow-ups](#follow-ups).

## ClaudeAgentOptions (`types.py` `ClaudeAgentOptions`)

| Python field | .NET member | Maps to | Status |
|---|---|---|---|
| `tools: list[str] \| ToolsPreset` | `Tools` + `ToolsPreset` | `--tools a,b` / `--tools ""` / `--tools default` | ok (two members instead of a union) |
| `allowed_tools` | `AllowedTools` | `--allowedTools` (+ `Skill`/`Skill(name)` from `skills`) | ok |
| `system_prompt: str \| SystemPromptPreset \| SystemPromptCustom \| SystemPromptFile` | `SystemPrompt: SystemPromptConfig?` (`SystemPromptText`, `SystemPromptPreset`, `SystemPromptCustom`, `SystemPromptFile`; `string` converts implicitly) | `--system-prompt` / `--append-system-prompt` / `--system-prompt-file`; `None` → `--system-prompt ""` | typed; `SystemPromptCustom` added |
| `SystemPromptPreset.exclude_dynamic_sections` | `SystemPromptPreset.ExcludeDynamicSections` | initialize `excludeDynamicSections` | ok |
| `SystemPromptPreset.snapshot` / `SystemPromptCustom.snapshot` | `SystemPromptPreset.Snapshot` / `SystemPromptCustom.Snapshot` | initialize `systemPromptSnapshot` (preset/custom only) | added |
| `mcp_servers: dict \| str \| Path` | `McpServers: McpServersConfig?` (`ServerMap` / `ConfigPath`; `Dictionary<string, object>` and `string` convert implicitly) | `--mcp-config {"mcpServers":…}` (SDK servers stripped to `{type,name}`) or path/JSON verbatim; empty → omitted | typed; empty-map omission fixed |
| `strict_mcp_config` | `StrictMcpConfig` | `--strict-mcp-config` | ok |
| `permission_mode` | `PermissionMode` (`Default, AcceptEdits, Plan, BypassPermissions, DontAsk, Auto`) | `--permission-mode` | fixed (`DontAsk`/`Auto` used to throw when building argv) |
| `continue_conversation` | `ContinueConversation` | `--continue` | ok |
| `resume` | `Resume` | `--resume=<v>` (Windows metachar guard) | ok |
| `session_id` | `SessionId` | `--session-id=<v>` (Windows metachar guard) | ok |
| `max_turns` | `MaxTurns` | `--max-turns` (0 omitted, Python truthiness) | fixed |
| `max_budget_usd` | `MaxBudgetUsd` (`decimal`) | `--max-budget-usd` (invariant culture) | fixed (culture) |
| `disallowed_tools` | `DisallowedTools` | `--disallowedTools` | ok |
| `model` / `fallback_model` | `Model` / `FallbackModel` | `--model` / `--fallback-model` (empty omitted) | ok |
| `betas` | `Betas` | `--betas` | ok (`SdkBeta` literal not modelled; string list) |
| `permission_prompt_tool_name` | `PermissionPromptToolName` | `--permission-prompt-tool`; `stdio` when `can_use_tool` set | fixed (mutual-exclusion check now in `QueryAsync` too) |
| `cwd` / `cli_path` | `Cwd` / `CliPath` | process cwd + `PWD` / executable | ok |
| `settings` | `Settings` | `--settings` (merged with sandbox) | ok |
| `add_dirs` | `AddDirs` | `--add-dir` each | ok |
| `env` | `Env` | process env (after `CLAUDE_CODE_ENTRYPOINT`, before `CLAUDE_AGENT_SDK_VERSION`) | ok (entrypoint `sdk-dotnet`) |
| `extra_args` | `ExtraArgs` | `--flag` / `--flag v` / `--flag=v` for dash-leading values | ok |
| `max_buffer_size` | `MaxBufferSize` | stdout/stderr framing limit | ok |
| `debug_stderr` (deprecated) | `DebugStderr` (`[Obsolete]`, unused) | not read | added |
| `stderr` | `StderrCallback` | stderr piped only when set | ok |
| `can_use_tool` | `CanUseTool` | control `can_use_tool` | ok; shadowing warning added (`CanUseToolShadowedWarning` text written to stderr once per message) |
| `hooks` | `Hooks` | initialize `hooks` | ok |
| `user` | `User` | Python `user=` on spawn | diff: .NET throws `NotSupportedException` (no setuid in `Process`) |
| `include_partial_messages` | `IncludePartialMessages` | `--include-partial-messages` | ok |
| `include_hook_events` | `IncludeHookEvents` | `--include-hook-events` | ok |
| `forward_subagent_text` | `ForwardSubagentText` | initialize `forwardSubagentText: true` (only when on) | added |
| `verbatim_prompts` | `VerbatimPrompts` | every outgoing user message stamped `client_composed: true`; warns on CLI < 2.1.248 | added |
| `fork_session` | `ForkSession` | `--fork-session` | ok |
| `resume_session_at` | `ResumeSessionAt` | `--resume-session-at=<uuid>` (equals form, Windows metachar guard, empty omitted) | added |
| `resume_drops_turn` | `ResumeDropsTurn` | `--resume-drops-turn=<uuid>` (equals form, Windows metachar guard, **empty forwarded**) | added |
| `agents` | `Agents` | initialize `agents` (None-valued keys dropped) | ok |
| `setting_sources` | `SettingSources` | `--setting-sources=a,b` (defaults to `user,project` when `skills` set) | ok |
| `skills: list[str] \| "all" \| None` | `Skills: SkillsConfig?` (`All` / `Named`; `"all"`, `List<string>`, `string[]` convert implicitly; any other string throws) | `--allowedTools` rules + initialize `skills` (lists only) | typed |
| `sandbox` | `Sandbox` | merged into `--settings` | ok |
| `plugins` | `Plugins` | `--plugin-dir`; non-`local` type raises | fixed (non-local used to be silently skipped) |
| `max_thinking_tokens` (deprecated) | `MaxThinkingTokens` (`[Obsolete]`) | `--max-thinking-tokens` | ok |
| `thinking` | `Thinking` (`ThinkingConfigAdaptive/Enabled/Disabled`) | `--thinking` / `--max-thinking-tokens` / `--thinking-display` | ok |
| `effort` | `Effort` | `--effort` | ok |
| `output_format` | `OutputFormat` | `--json-schema` | ok |
| `enable_file_checkpointing` | `EnableFileCheckpointing` | env `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=true` | ok |
| `session_store` / `session_store_flush` | `SessionStore` / `SessionStoreFlush` | `--session-mirror` + mirror batcher | ok |
| `load_timeout_ms` | `LoadTimeoutMs` (default 60000) | `SessionResume.MaterializeResumeSessionAsync(options, loadTimeout)` from `QueryAsync` and `ClaudeSDKClient.ConnectAsync` | added (≤0 → 1 tick, see follow-ups) |
| `task_budget` | `TaskBudget` | `--task-budget` | ok |
| — | `--input-format stream-json`, `--output-format stream-json --verbose` | always | ok |

## AgentDefinition

| Python field | .NET | Status |
|---|---|---|
| `description`, `prompt`, `tools`, `disallowedTools`, `model`, `skills`, `memory`, `initialPrompt`, `maxTurns`, `background`, `permissionMode` | same (positional record) | ok |
| `effort: EffortLevel \| int` | `Effort: AgentEffort?` (`Level` / `Tokens`; `EffortLevel` and `int` convert implicitly) | typed |
| `mcpServers: list[str \| dict]` | `McpServers: IReadOnlyList<AgentMcpServer>?` (`Reference` / `Inline`; `string` and `Dictionary<string, object>` convert implicitly) | typed |

## Messages and content blocks (`_internal/message_parser.py` → `Internal/MessageParser.cs`)

| Python | .NET | Status |
|---|---|---|
| `UserMessage.content/uuid/parent_tool_use_id` | same (`Content` stays raw `JsonElement`; `GetContentBlocks()`) | ok |
| `UserMessage.tool_use_result` | `ToolUseResult` | added |
| `UserMessage.origin` / `MessageOrigin` (`kind, server, from, name, fromSession, senderTaskId, body, verifiedPeerPid, subkind` + pass-through keys) | `Origin` / `MessageOrigin` (+ `AdditionalProperties`) | added |
| user content blocks: only text / tool_use / tool_result, non-dict block → `MessageParseError` | `GetContentBlocks()` skips other types (used to throw on e.g. `image`) | fixed |
| `AssistantMessage` fields | same | ok |
| `AssistantMessage.error` read from the **top-level** frame | `Error` | fixed (was read from `message.error`) |
| assistant non-list content → error | `MessageParseException` | ok |
| `TextBlock`, `ThinkingBlock`, `ToolUseBlock`, `ToolResultBlock`, `ServerToolUseBlock`, `ServerToolResultBlock` (`advisor_tool_result`) | same | ok (`ToolResultBlock.is_error: null` no longer throws) |
| `SystemMessage`, `TaskStartedMessage`, `TaskProgressMessage`, `TaskNotificationMessage`, `MirrorErrorMessage` | same | ok |
| `TaskUpdatedMessage` (defensive parse of `task_updated`) | `TaskUpdatedMessage` | fixed (type existed but `task_updated` parsed as plain `SystemMessage`) |
| `HookEventMessage` (`hook_started`/`hook_response`) | same | ok |
| `ResultMessage.model_usage` (`modelUsage`) / `ModelUsage.canonicalModel/provider` | `ModelUsage` dictionary / `ModelUsage.CanonicalModel/Provider` | added |
| `ResultMessage.permission_denials` | `PermissionDenials` (raw) | added |
| `ResultMessage.terminal_reason` | `TerminalReason` | added |
| `ResultMessage.origin` | `Origin` | added |
| other `ResultMessage` fields | same | ok |
| `StreamEvent` | same | ok |
| `RateLimitEvent` / `RateLimitInfo` (raw status passed through) | same; unknown status → `RateLimitStatus.Unknown` | fixed (unknown status used to throw) |
| `ConversationResetMessage` (`conversation_reset`) | `ConversationResetMessage : Message` | fixed (type existed, was not a `Message` and never parsed) |
| unknown top-level type → skipped | `ParseOrNull` returns null | ok |
| `TERMINAL_TASK_STATUSES`, `TaskUpdatedStatus`, `TaskNotificationStatus`, `TaskUsage`, `MessageOriginKind`, `TaskNotificationOriginSubkind`, `ServerToolName`, `DeferredToolUse` | same | ok |

## ClaudeSDKClient (`client.py`)

| Python | .NET | Status |
|---|---|---|
| `connect(prompt: str \| AsyncIterable \| None)` | `ConnectAsync(string?)` / `ConnectAsync(IAsyncEnumerable<…>)` | fixed: no longer rejects a string prompt with `CanUseTool` (Python dropped that check); runs `_configure_can_use_tool` |
| `query(prompt: str \| AsyncIterable, session_id)` | `QueryAsync(string, …)` / `QueryAsync(IAsyncEnumerable<…>, …)` | added (stream overload); both stamp `client_composed` under `VerbatimPrompts` |
| `receive_messages`, `receive_response`, `interrupt`, `set_model`, `rewind_files`, `reconnect_mcp_server`, `toggle_mcp_server`, `stop_task`, `get_context_usage`, `get_server_info`, `disconnect`, `async with` | same (`…Async`, `IAsyncDisposable`) | ok |
| `set_permission_mode(mode: PermissionMode)` | `SetPermissionModeAsync(string)` + `SetPermissionModeAsync(PermissionMode)` | added typed overload |
| `get_mcp_status() -> McpStatusResponse` | `GetMcpStatusAsync()` → `JsonElement`; `ListMcpServersAsync()` → `McpStatusResponse` | diff (kept for compatibility) |
| load_timeout_ms feeds resume materialization | `SessionStoreSupport.MaterializeAsync` | added |

## `query()` (`query.py`, `_internal/client.py`)

| Python behavior | .NET | Status |
|---|---|---|
| `_configure_can_use_tool` (mutual exclusion, shadowing warning, `permission_prompt_tool_name="stdio"`) | `CanUseToolConfiguration.Configure` in `Claude.QueryAsync` and `ClaudeSDKClient` | added (`QueryAsync` had no check) |
| string prompt written to stdin after initialize, stamped | `Claude.QueryAsync(string)` → `StreamInputAsync` | fixed (stamping) |
| `materialize_resume_session(options)` with `load_timeout_ms` | `SessionStoreSupport.MaterializeAsync` | added |
| initialize timeout from `CLAUDE_CODE_STREAM_CLOSE_TIMEOUT` (≥60s) | `SessionStoreSupport.InitializeTimeout` | ok |

## Control protocol (`_internal/query.py` → `Internal/QueryHandler.cs`)

| Python | .NET | Status |
|---|---|---|
| outgoing `initialize` (`hooks`, `agents`, `excludeDynamicSections`, `systemPromptSnapshot`, `skills`, `forwardSubagentText`) | `InitializeAsync` | fixed (`systemPromptSnapshot`, `forwardSubagentText` added) |
| outgoing `interrupt`, `set_permission_mode`, `set_model`, `rewind_files`, `mcp_reconnect`, `mcp_toggle`, `stop_task`, `mcp_status`, `get_context_usage` | same | ok |
| incoming `can_use_tool`, `hook_callback`, `mcp_message`; `control_cancel_request` | same | ok |
| `stamp_user_message` | `QueryHandler.StampUserMessage` | added |
| `ResultError` replaces the trailing `ProcessError` after an `is_error` result (`_error_result_text`: errors → result → non-success subtype → `API error (HTTP n)`) | `ResultException.FromResultFrame` / `ErrorResultText` | fixed (was a plain `ProcessException` with `errors`/subtype text and stderr placeholder) |
| task-lifecycle ledger (#1088), transcript mirror, bidirectional stdin hold | same | ok |

## Errors (`_errors.py`)

| Python | .NET | Status |
|---|---|---|
| `ClaudeSDKError`, `CLIConnectionError`, `CLINotFoundError`, `ProcessError`, `CLIJSONDecodeError`, `MessageParseError` | `ClaudeSDKException`, `CliConnectionException`, `CliNotFoundException`, `ProcessException`, `JsonDecodeException`, `MessageParseException` | ok |
| `ResultError(subtype, errors, result, api_error_status, terminal_reason, session_id, data)` | `ResultException` (+ `Result`, `ApiErrorStatus`, `SessionId`, `RawResult`) | fixed (fields added; now actually raised) |
| `CanUseToolShadowedWarning` | type exists; the message is written to stderr | diff (.NET has no warnings channel) |

## Hooks and permissions

| Python | .NET | Status |
|---|---|---|
| 10 `HookEvent`s and their `*HookInput` TypedDicts | `HookEvent` enum + `*HookInput` records | ok |
| `PreToolUseHookSpecificOutput`, `PostToolUseHookSpecificOutput` | same | ok |
| `PostToolUseFailure`, `UserPromptSubmit`, `SessionStart`, `Notification`, `SubagentStart`, `PermissionRequest` hook-specific outputs | records of the same names | added |
| `HookMatcher`, `HookContext`, `HookJSONOutput` (`async_`/`continue_`) | `HookMatcher`, `HookContext`, `HookOutput` (`Async`/`Continue`) | ok |
| `PermissionMode`, `PermissionUpdate` (+`to_dict`/`from_dict`), `PermissionRuleValue`, `PermissionResultAllow/Deny`, `ToolPermissionContext` | same | ok |
| `SandboxSettings`, `SandboxNetworkConfig`, `SandboxIgnoreViolations` | same | ok |

## Transport (`_internal/transport/subprocess_cli.py`)

| Python | .NET | Status |
|---|---|---|
| `VERBATIM_PROMPTS_MINIMUM_CLAUDE_CODE_VERSION = "2.1.248"` warning in `_check_claude_version` | `VerbatimPromptsMinimumClaudeCodeVersion`, `GetVerbatimPromptsVersionWarning` (stderr) | added |
| `_reject_windows_cmd_metacharacters` for `resume`, `session_id`, `resume_session_at`, `resume_drops_turn` | `RejectWindowsCmdMetacharacters` | fixed (new options covered) |
| `_reject_non_list_skills`, `_validate_skill_name`, `_apply_skills_defaults` | `SkillsConfig` conversion, `ValidateSkillName`, `ApplySkillsDefaults` | ok |
| `--allowedTools` emitted once, from the skills-aware list, before `--max-turns` | same | fixed (was rewritten in place after the fact) |

## Package exports (`__init__.py`)

Everything above plus: `query` → `Claude.QueryAsync`; `Transport` → `ITransport`;
`__version__` → assembly version (sent as `CLAUDE_AGENT_SDK_VERSION`);
`ThinkingConfig*`, `TaskBudget`, `ContextUsage*`, `Mcp*Status*` types: ok.
Out of scope for this pass (listed for completeness):

| Python export | .NET | Owner |
|---|---|---|
| `tool`, `create_sdk_mcp_server`, `SdkMcpTool`, `ToolAnnotations`, `McpSdkServerConfig` | `Mcp/*` (`McpServers.Sdk`, `McpSdkServerBuilder`, …) | MCP |
| `list_sessions`, `get_session_info`, `get_session_messages`, `list_subagents`, `get_subagent_messages`, `*_from_store`, `rename/tag/delete/fork_session(_via_store)`, `ForkSessionResult`, `fold_session_summary`, `project_key_for_directory`, `import_session_to_store`, `InMemorySessionStore`, `SessionStore` types | `ClaudeSessions`, `Sessions/*`, `Types.Sessions.cs` | Sessions |

## Follow-ups

1. **Sessions** (`Sessions/SessionResume.cs`): `MaterializeResumeSessionAsync(options, TimeSpan)`
   throws for a non-positive timeout, while Python treats `load_timeout_ms <= 0` as an immediate
   timeout. The option plumbing currently maps `<= 0` to one tick; accepting `TimeSpan.Zero` there
   would let it pass the value through unchanged.
2. **Sessions**: Python's local-disk `fork_session()` has no .NET counterpart (only
   `SessionMutations.ForkSessionViaStoreAsync`).
3. **MCP** (`Internal/QueryHandler.HandleMcpMessageAsync` / `SdkMcpBridge`): Python answers a
   JSON-RPC *notification* carried in `mcp_message` with `{"jsonrpc":"2.0","result":{}}` and uses
   the error text `Server '<name>' not found`; left untouched here because the MCP bridge is being
   reworked concurrently.
4. **MCP**: `McpServerRegistry` derives from `Dictionary<string, object>`, so map values stay
   `object`. A typed `McpServerConfig` base for `McpStdio/SSE/Http/SdkServerConfig` would allow a
   fully typed server map.
5. **Tests**: `McpPythonParityTests.CancelledNotification_CancelsTheInFlightCall` failed once in
   several full-suite runs (it passes in isolation and on re-runs); looks timing-dependent.
6. **AOT**: `ClaudeJsonContext` does not list `ConversationResetMessage` / `RateLimitEvent` as
   polymorphic `Message` subtypes; serializing those through the `Message` base is unsupported.
   Out of scope until the AOT pass.
