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
| `load_timeout_ms` | `LoadTimeoutMs` (default 60000) | `SessionResume.MaterializeResumeSessionAsync(options, loadTimeout)` from `QueryAsync` and `ClaudeSDKClient.ConnectAsync` | added (≤0 → immediate timeout, as in Python) |
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
| unknown assistant content block → skipped | `RawContentBlock` | diff: TS parity (see below) |
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
| unknown top-level type → skipped | `UnknownMessage` (full frame on `Message.Raw`); only `keep_alive` is skipped | diff: follows the TypeScript SDK, see [TypeScript SDK 0.3.283](#typescript-sdk-03283-messages-hooks-permission-types) |
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

1. ~~**Sessions**: non-positive `load_timeout_ms`~~ — resolved: `MaterializeResumeSessionAsync`
   accepts `TimeSpan.Zero` (and negative spans) as an immediate timeout and `LoadTimeoutMs` is
   passed through unclamped.
2. **Sessions**: Python's local-disk `fork_session()` has no .NET counterpart (only
   `SessionMutations.ForkSessionViaStoreAsync`).
3. ~~**MCP** notification ack / unknown-server text~~ — resolved: `SdkMcpBridge.HandleAsync`
   returns no reply for notifications and `QueryHandler` acks them with
   `{"jsonrpc":"2.0","result":{}}`; unknown servers answer `Server '<name>' not found`.
4. **MCP**: `McpServerRegistry` derives from `Dictionary<string, object>`, so map values stay
   `object`. A typed `McpServerConfig` base for `McpStdio/SSE/Http/SdkServerConfig` would allow a
   fully typed server map.
5. **Tests**: `McpPythonParityTests.CancelledNotification_CancelsTheInFlightCall` failed once in
   several full-suite runs (it passes in isolation and on re-runs); looks timing-dependent.
6. **AOT**: the library is `IsAotCompatible` (zero trim/AOT warnings; NativeAOT smoke test in
   `tests/Claude.AgentSdk.AotSmoke`). Wire JSON goes through the internal `SdkJsonContext` /
   `SdkJson`; the older `ClaudeJsonContext` is unused by the SDK (messages are parsed by hand) and
   still does not list `ConversationResetMessage` / `RateLimitEvent`.
   `McpSdkServerBuilder.Tool(string, Delegate, ...)` requires reflection; use the explicit-schema or
   `JsonTypeInfo<TArgs>` overloads in trimmed/AOT apps.

## TypeScript SDK 0.3.283: messages, hooks, permission types

Reference: `@anthropic-ai/claude-agent-sdk` **0.3.283** (`sdk.d.ts`). This section tracks where
.NET goes beyond Python to match the TypeScript SDK. Columns: **TS** = TypeScript type, **Py** =
Python 0.2.160 behaviour, **.NET** = this SDK. Everything is parsed by hand from `JsonElement`
(no reflection), new records are registered in `SdkJsonContext`, and the library stays free of
IL2xxx/IL3xxx warnings. Parsers for the TS-only shapes are lenient: missing or mistyped fields
become defaults, never a stream-breaking exception.

### Raw access and unknown types

| TS | Py | .NET | Status |
|---|---|---|---|
| every `SDKMessage` is the untouched object | no raw frame (system `data` only) | `Message.Raw` (full frame, `[JsonIgnore]`) on every parsed message; `SystemMessage.Raw == Data` | added |
| unknown top-level `type` yielded as-is | dropped (`None`) | `UnknownMessage { Type }` yielded by `ReceiveMessagesAsync`; `keep_alive` still skipped | diff (TS behaviour) |
| unknown content blocks passed through | dropped | assistant: `RawContentBlock(Type, Raw)`; user `GetContentBlocks()` still skips (Python parity) | diff (TS behaviour) |

### Top-level messages

| TS | Py | .NET | Status |
|---|---|---|---|
| `SDKToolProgressMessage` (`tool_progress`, incl. `subagent_retry`) | dropped | `ToolProgressMessage`, `SubagentRetryInfo` | added |
| `SDKToolUseSummaryMessage` | dropped | `ToolUseSummaryMessage` | added |
| `SDKAuthStatusMessage` | dropped | `AuthStatusMessage` | added |
| `SDKPromptSuggestionMessage` | dropped | `PromptSuggestionMessage` | added |
| `SDKActiveGoalMessage` (`value` or null) | dropped | `ActiveGoalMessage`, `ActiveGoal` | added |
| `SDKAssistantMessage` extras: `request_id, user_message_uuid(s), resume_reason, resumed_from_incomplete_thinking, supersedes, aborted, subagent_type, task_description, timestamp, context_usage, usage_report`, inner `stop_sequence` | raw only | `AssistantMessage.RequestId … StopSequence`, `ContextUsage: SdkContextUsage`, `UsageReport: SdkUsageReport` | added |
| `SDKAssistantMessageError` (13 values) | raw string | `AssistantMessageError` + 7 values (appended after `Unknown`, numeric values stable); `AssistantMessage.ErrorRaw` keeps the wire string; `AssistantMessageErrors.Parse/ToWireString` | fixed (unknowns were collapsed) |
| `SDKUserMessage` / `SDKUserMessageReplay` extras: `session_id, isSynthetic, isReplay, priority, timestamp, shouldQuery, client_composed, file_attachments, pasted_content, inline_pastes, subagent_type, task_description` | partial | `UserMessage.SessionId … TaskDescription` (`IsReplay` distinguishes replays) | added |
| `SDKResultMessage` extras: `queued_turn_count, result_index, fast_mode_state, fast_mode_disabled_reason, startup_failure_reason, user_message_uuid(s), resume_reason, local_command, ttft_ms, ttft_stream_ms, time_to_request_ms` | raw only | `ResultMessage` properties of the same names | added |
| `SDKPermissionDenial[]` | raw list | `ResultMessage.GetPermissionDenials()` → `PermissionDenial` (raw `PermissionDenials` kept) | added |
| timing internals (`request_sent_wall_ms`, `first_*`, `warm_spare_claimed`, `time_origin_ms`, …) | raw | via `Message.Raw` | diff (not typed; internal telemetry) |
| `ModelUsage.thinkingTokens/costBasis` | dropped | `ModelUsage.ThinkingTokens/CostBasis` | added |
| `SDKPartialAssistantMessage` extras (`ttft_ms, user_message_uuid(s), resume_reason`) | raw | `StreamEvent` properties | added |
| `SDKRateLimitInfo` extras + `seven_day_overage_included` | raw string | `RateLimitType.SevenDayOverageIncluded`; `RateLimitInfo.RateLimitTypeRaw`, `IsUsingOverage, OverageInUse, SurpassedThreshold, LimitScope, ErrorCode, CanUserPurchaseCredits, HasChargeableSavedPaymentMethod` | added |
| `SDKConversationResetMessage.trigger/user_message_uuid/timestamp` | dropped | `ConversationResetMessage` properties | added |
| `McpServerStatus.source`, tool `_meta` | dropped | `McpServerStatus.Source`, `McpStatusToolInfo.Meta` | added |

### `system` subtypes (all subclass `SystemMessage`; `Data` unchanged)

| TS | Py | .NET | Status |
|---|---|---|---|
| `SDKSystemMessage` (`init`) | `SystemMessage` | `SystemInitMessage` (+ `InitMcpServer`, `InitPlugin`, `InitPluginError`) | added |
| `compact_boundary`, `status`, `api_retry`, `control_request_progress` | `SystemMessage` | `CompactBoundaryMessage` (`CompactMetadata`), `StatusMessage`, `ApiRetryMessage` (`Error` + `ErrorRaw`, `NoResponse`), `ControlRequestProgressMessage` | added |
| `model_refusal_fallback`, `model_refusal_no_fallback`, `local_command_output`, `plugin_install` | `SystemMessage` | `ModelRefusalFallbackMessage`, `ModelRefusalNoFallbackMessage`, `LocalCommandOutputMessage`, `PluginInstallMessage` | added |
| `background_tasks_changed`, `thinking_tokens`, `session_state_changed`, `worker_shutting_down`, `commands_changed` | `SystemMessage` | `BackgroundTasksChangedMessage`, `ThinkingTokensMessage`, `SessionStateChangedMessage`, `WorkerShuttingDownMessage`, `CommandsChangedMessage` (`SlashCommand[]`) | added |
| `notification`, `files_persisted`, `memory_recall`, `elicitation_complete`, `permission_denied`, `informational` | `SystemMessage` | `NotificationMessage`, `FilesPersistedMessage`, `MemoryRecallMessage`, `ElicitationCompleteMessage`, `PermissionDeniedMessage`, `InformationalMessage` | added |
| `hook_started` / `hook_response` fields `hook_id, hook_name, output, stdout, stderr, exit_code, outcome` | `HookEventMessage` (name only) | `HookEventMessage.HookId … Outcome` | added |
| `hook_progress` | `SystemMessage` | `HookProgressMessage : HookEventMessage` | diff (typed; consumers filtering `HookEventMessage` now also see progress) |
| `task_started/progress/notification/updated` extras (`subagent_type, is_backgrounded, spawn_depth, workflow_name, prompt, skip_transcript, ambient, summary, reason, resource_links`, typed patch) | raw | `TaskStartedMessage` / `TaskProgressMessage` / `TaskNotificationMessage` (+ `StatusRaw`, `McpResourceLink`) / `TaskUpdatedMessage.TypedPatch` | added |
| `mirror_error` key `{projectKey, sessionId}` | `{project_key, session_id}` | Python shape | diff (kept at Python parity; SDK-synthesized) |

### Info types (`Types.Info.cs`)

| TS | .NET | Status |
|---|---|---|
| `SlashCommand`, `AgentInfo`, `ModelInfo`, `AccountInfo` | records of the same names, each with lenient `static Parse(JsonElement)` | added |
| `SDKControlInitializeResponse` | `InitializeResponse` + `InitializeResponse.Parse(JsonElement)` (keeps `Raw`) | added |
| `SDKContextUsage` (per assistant message) | `SdkContextUsage` (+ category / tool / file / agent / skill records) | added |
| `SDKUsageReport` | `SdkUsageReport` (`SdkUsageSession`, `SdkUsageRateLimits`, `SdkUsageRateLimit`, `SdkUsageExtraUsage`) | added |
| `TerminalReason`, `FastModeState`, `FastModeDisabledReason`, `ApiKeySource`, `SDKStartupFailureReason`, `SDKStatus` | string constants: `TerminalReasons`, `FastModeStates`, `FastModeDisabledReasons`, `ApiKeySources`, `StartupFailureReasons`, `SdkStatuses` (properties stay `string` so new values pass through) | added |

### Hooks

| TS | Py | .NET | Status |
|---|---|---|---|
| `HOOK_EVENTS` (33) | 10 | `HookEvent` has all 33 (original 10 keep their numeric values); wire name via `ToJsonString()` / `HookEventNames.ToWireName` | added |
| string-keyed hooks (any event name) | arbitrary dict keys | `HookEventNames.Parse(string)` returns a named member or registers a custom value sent under the exact name; `HooksBuilder.On(string eventName, …)` | added |
| initialize `hooks` keys | wire names | `QueryHandler.InitializeAsync` now uses `ToJsonString()` (was `Enum.ToString()`) | fixed |
| `BaseHookInput.prompt_id/effort/agent_id/agent_type` | partial | `BaseHookInput.PromptId/Effort/AgentId/AgentType` (+ `Raw`); tool-hook `AgentId/AgentType` moved to the base | added |
| per-event input extras (`mcp_server`, `duration_ms`, `source`, `session_title`, `last_assistant_message`, `background_tasks`, `session_crons`, typed `permission_suggestions`) | partial | `McpServer` (`McpServerProvenance`), `DurationMs`, `Source`, `SessionTitle`, `LastAssistantMessage`, `BackgroundTasks`, `SessionCrons`, `PermissionRequestHookInput.Suggestions` | added |
| 23 new `*HookInput` types (`PostToolBatch` … `MessageDisplay`) | none | records of the same names (`ModelSwitchHookInput` / `TeamTaskHookInput` share fields) | added |
| `HookInput` union | none | `HookInput.Parse(JsonElement)` / `ParseAs<T>` — reflection-free dispatcher; unknown events → `UnknownHookInput` | added |
| `SyncHookJSONOutput.terminalSequence`, `decision: 'approve'\|'block'` | none / `'block'` | `HookOutput.TerminalSequence` (sent as `terminalSequence`), `HookDecision.Approve/Block`, `HookPermissionDecision` constants | added |
| output extras: PostToolUse `classifierContext`; UserPromptSubmit `sessionTitle, suppressOriginalPrompt`; SessionStart `initialUserMessage, sessionTitle, watchPaths, reloadSkills` | none | properties on the existing records | added |
| new hook-specific outputs: Stop, SubagentStop, UserPromptExpansion, Setup, PreModelSwitch, PostModelSwitch, PostToolBatch, PermissionDenied, Elicitation, ElicitationResult, CwdChanged, FileChanged, WorktreeCreate, MessageDisplay | none | `*HookSpecificOutput` records with `ToJsonElement()` (source-generated) | added |
| `PermissionRequestHookSpecificOutput.decision` union | dict | `PermissionRequestDecision` (`Allow(...)` / `Deny(...)`, `Parse`), `PermissionRequestHookSpecificOutput.From(decision)` / `TypedDecision` | added |

### Permission types

| TS | Py | .NET | Status |
|---|---|---|---|
| `PermissionUpdateDestination` `cliArg` | missing | `PermissionUpdateDestination.CliArg` (appended; `ToJsonString`, `FromControlProtocol`, `FromDictionary`) | added |
| `PermissionResult.toolUseID/decisionClassification`, `CanUseTool` options (`mcpServer, defaultToNo, suppressAlwaysAllowRule, requestId, matchedAskRule`) | missing | tracked with the control-protocol work (`QueryHandler.HandleCanUseToolAsync`) | follow-up |

