# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-7A Complete. Full Design Review Complete.** Build green (0 errors), 276 tests passing (up from 264), Full Review grade A-. Core systems operational:
- Agent registry, supervisor, cross-agent calling with recursion guard + depth limits + timeout
- WebSocket (with reconnect replay + sequence IDs), TUI (with steering), Telegram channel adapters
- File and in-memory session stores (configurable via platform config)
- Session suspend/resume, paginated history, bounded message queuing with backpressure
- OAuth + API key auth
- Provider abstraction: OpenAI, Anthropic, Copilot
- WebUI dashboard with thinking/tool display, reconnection, activity feed
- DIP fix: GatewayWebSocketHandler now uses IGatewayWebSocketChannelAdapter interface
- OpenAPI spec export
- Comprehensive integration tests (39 new tests in Sprint 7A)

**Carried Findings (Sprint 7B):**
- `Path.HasExtension` auth bypass in `GatewayAuthMiddleware`
- StreamAsync background task leak in providers
- SessionHistoryResponse should move to Abstractions.Models
- Monitor GatewaySession SRP — extract replay buffer if it grows further

**Phase 7 Focus:** Resilience (reconnection, pagination, queueing), channel consolidation, test hardening, observability.

---

## 2026-04-06T03:00:00Z — Sprint 7A Design Review (Lead)

**Timestamp:** 2026-04-06T03:00:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Architectural review of all Sprint 7A implementations

**Context:**
Sprint 7A delivered 8 features across 11 commits (Bender ×4, Farnsworth ×4, Kif ×2, Hermes ×1). Build green, 264 tests passing (39 new). Reviewed: session reconnection protocol, suspend/resume, TUI steering, message queuing, DIP fix, history pagination, max call chain depth, cross-agent timeout, configurable session store, OpenAPI export.

**Grade: A-**

| Area | Grade |
|------|-------|
| SOLID Compliance | A |
| Extension Model | A |
| API Design | A- |
| Thread Safety | A |
| Test Quality | A- |

**Key Findings:**
- P0: None.
- P1: SessionHistoryResponse should move to Abstractions. GatewaySession accumulating replay concern alongside history — monitor SRP. SequenceAndPersistPayloadAsync has unnecessary double-serialization. Reconnect replay bypasses payloadMutator (needs documenting comment).
- Carried: Path.HasExtension auth bypass, StreamAsync task leak (both from Phase 5/6, Sprint 7B).

**Decision written to:** `.squad/decisions/inbox/leela-sprint7a-review.md`

---

## 2026-04-06T0546Z — Phase 10 Design Review: Grade A- (Wave 2)

**Duration:** ~22 min  
**Status:** ✅ Complete  
**Scope:** Comprehensive design review of Phase 10 implementations

**Context:** Phase 10 delivered CLI parity + Gateway P1 fixes across 3 agents (Bender ×1, Farnsworth ×4, Hermes ×1). 6 commits, build green, tests passing.

**Reviewed Decisions:**

1. **WebSocket handler decomposition approved** — `GatewayWebSocketHandler` (orchestrator, 150 lines) → `WebSocketConnectionManager` (166 lines) + `WebSocketMessageDispatcher` (296 lines). Clean SRP split with preserved endpoint contracts.

2. **PUT AgentId validation approved** — Returns 400 on route/body mismatch, falls back to route value on empty body. Phase 9 P1 resolved.

3. **CORS verb restriction approved** — Production restricts to `GET, POST, PUT, DELETE, OPTIONS`. Development keeps permissive CORS. Phase 9 P1 resolved.

**Grade: A-**

| Area | Grade | Notes |
|------|-------|-------|
| SOLID Compliance | A | WebSocket decomposition clean SRP |
| API Design | A- | PUT validation complete, contracts preserved |
| Extension Model | A | Config loader unified CLI+gateway |
| Thread Safety | A | WebSocket handler isolation maintained |
| Test Quality | A- | 10 new deployment tests added |

**Issues Identified:**

4. **CLI architecture needs Phase 11 work** — `Program.cs` is 850+ lines of top-level statements
   - P1: Decompose into command handler classes
   - P1: Add test coverage for config get/set reflection

**Deployment test harness approved** — `WebApplicationFactory<Program>` with isolated `BOTNEXUS_HOME` temp roots. Solid config layering coverage.

**P1 Items for Phase 11:**
- [ ] Decompose `BotNexus.Cli/Program.cs` into command handler classes
- [ ] Add unit tests for CLI config get/set reflection logic
- [ ] Copilot conformance tests duplicate OpenAI (carried from Phase 9)

**Carried Forward:**
- StreamAsync task leak (deferred — frozen code)
- SessionHistoryResponse location (Abstractions.Models)
- SequenceAndPersistPayloadAsync double-serialization

**Orchestration Log:** `.squad/orchestration-log/2026-04-06T0546Z-leela.md`

---

## 2026-04-05T19:41:00Z — Phase 7 Gateway Gap Analysis (Lead)

**Timestamp:** 2026-04-05T19:41:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Architecture gap analysis against 6 refined Gateway requirements

**Context:**
Phases 1-6 are complete (build green, 225 tests, Phase 6 Grade A). Owner provided refined requirements for Phase 7. Performed comprehensive gap analysis across all gateway, channels, WebUI, CLI, and test code.

**Key Findings:**

1. **Agent Management (75% complete):** Core registry, supervisor, communicator, config sources all done. Missing: max call chain depth limit (P1), cross-agent timeout (P1).

2. **Isolation Strategies (25% complete):** `IIsolationStrategy` interface solid. InProcess works. Sandbox/Container/Remote are stubs (by design — Phase 2 items). Only P1 gap: typed isolation options model.

3. **Channel Adapters (60% complete):** WebSocket channel is full-featured (streaming, steering, follow-up, thinking, tools). TUI has streaming + thinking/tool display. Telegram is stub. Gaps: TUI needs steering support (P1), need explicit message queuing abstraction (P1).

4. **Session Management (70% complete):** Two session stores (memory, file), cleanup service, thread-safe history, routing all done. **P0 gap:** No session reconnection with event replay — clients lose stream on disconnect.

5. **API Surface (65% complete):** REST controllers for agents/chat/sessions/config done. WS handlers for chat and activity done. Auth middleware done. **P0 gap:** No WS reconnection replay. P1 gaps: missing session suspend/resume, history pagination, agent update endpoints, OpenAPI spec, authorization enforcement.

6. **Platform Configuration (70% complete):** PlatformConfig + loader + watcher + CLI validation all done. Gaps: no JSON Schema (P1), no `botnexus init` command (P1), no CLI agent management (P1), no session store config selection (P1).

**Sprint Plan Delivered:**
- **Sprint 7A (P0+critical P1):** Session reconnection, suspend/resume, history pagination, depth limit, timeout, OpenAPI, TUI steering, message queuing. 3-5 days.
- **Sprint 7B (remaining P1):** Agent update API, auth enforcement, isolation options, JSON Schema, CLI init/agents, WebUI enhancements. 3-5 days.
- **Sprint 7C (P2 polish):** Rate limiting, correlation IDs, lifecycle events, health checks, SQLite store, WebUI module split. 3-5 days.

**Carried-forward Phase 5/6 issues:** DIP violation in WebSocketHandler, Path.HasExtension auth bypass, StreamAsync background task leak.

**Decision written to:** `.squad/decisions/inbox/leela-phase7-plan.md`

## 2026-04-03T16:25:00Z — Provider Response Normalization Layer (Lead)

**Timestamp:** 2026-04-03T16:25:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen  
**Scope:** Design and implement provider response normalization layer

**Context:**
After discovering the Copilot proxy splits Claude responses across multiple choices (content in `choices[0]`, tool_calls in `choices[1]`), we patched it with a merge-choices loop. This works but is fragile. Pi's architecture (badlogic/pi-mono) is better: each provider owns its own response parsing and emits a canonical `AssistantMessage` type. The agent loop never sees raw JSON.

**Architecture Goal:**
Establish clear normalization contract where:
1. `LlmResponse` is the single canonical response format
2. Each provider normalizes its raw API response internally
3. Agent loop is completely isolated from provider quirks
4. Provider-specific edge cases (multi-choice, argument formats, field naming) handled inside providers

**Implementation:**

1. **Enhanced LlmResponse documentation** (`Core/Models/LlmResponse.cs`)
   - Documented as "canonical normalized response from any LLM provider"
   - Listed provider normalization responsibilities:
     * Parse provider-specific formats (JSON, SDK objects)
     * Merge multi-choice responses if applicable
     * Normalize tool call argument formats (JSON string vs object)
     * Map finish reasons to FinishReason enum
     * Normalize token count field names
     * Handle missing/null fields gracefully
   - Added XML docs to FinishReason enum values
   - Added parameter documentation

2. **Enhanced ILlmProvider interface** (`Core/Abstractions/ILlmProvider.cs`)
   - Documented normalization contract at interface level
   - `ChatAsync` MUST return normalized LlmResponse
   - Clarified providers handle their own quirks internally
   - Agent loop should never see raw provider responses

3. **Documented CopilotProvider normalization** (`Providers.Copilot/CopilotProvider.cs`)
   - **Multi-choice merging:** When proxying Claude, Copilot splits content and tool_calls across choices[] - we merge by taking first non-empty content, first tool_calls array
   - **Dual argument formats:** OpenAI returns arguments as JSON string, Claude returns as JSON object - ParseToolCalls handles both via ValueKind detection (commit dd0343a fix)
   - **Finish reason mapping:** Copilot strings ("stop", "tool_calls") → FinishReason enum
   - **Token count mapping:** "prompt_tokens"/"completion_tokens" → InputTokens/OutputTokens
   - Added `// NORMALIZATION:` comments at each normalization point
   - Referenced historical fix (commit dd0343a) in docs

4. **Documented OpenAiProvider normalization** (`Providers.OpenAI/OpenAiProvider.cs`)
   - **Content extraction:** OpenAI SDK returns Content[] array, we take first item's Text
   - **Tool arguments:** Always JSON string format, deserialize to Dictionary<string, object?>
   - **Finish reason mapping:** ChatFinishReason SDK enum → FinishReason enum
   - **Token count mapping:** InputTokenCount/OutputTokenCount (direct mapping)
   - Added `// NORMALIZATION:` comments
   - Documented that SDK makes normalization straightforward vs raw JSON

5. **Documented AnthropicProvider normalization** (`Providers.Anthropic/AnthropicProvider.cs`)
   - **Content extraction:** content[] array, extract first block's text field
   - **Stop reason mapping:** snake_case strings ("end_turn", "max_tokens") → enum
   - **Token count mapping:** "input_tokens"/"output_tokens" → InputTokens/OutputTokens
   - **TODO:** Tool call support not yet implemented (marked as P1 in architecture review)
   - Added `// NORMALIZATION:` comments

**Verification:**
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 540 passing (23 E2E, 11 deployment, 396 unit, 110 integration)
- ✅ Backward compatible: No behavioral changes, only documentation
- ✅ Pre-commit hook validated: Build + tests
- ✅ AgentLoop confirmed clean: No provider-specific logic, works only with LlmResponse

**Key Design Principles:**
1. **Separation of concerns:** Provider quirks stay in providers, agent logic stays clean
2. **Explicit contract:** Documentation makes normalization responsibilities crystal clear
3. **Future-proof:** New providers know exactly what they must implement
4. **Follows patterns:** Inspired by Pi's AssistantMessage normalization architecture

**Commits:**
- `d31923b`: feat(providers): Implement provider response normalization layer

**Outcome:**
Normalization layer is now formally documented and enforced via contract. Each provider owns its response parsing, the agent loop is isolated from provider details. This pattern is now the standard for all current and future providers. The existing code already followed this pattern - we formalized it with comprehensive documentation.

**Decision Document:** `.squad/decisions/inbox/leela-provider-normalization.md`

---

## 2026-04-03T22:30:00Z — Multi-Turn Tool Call Bug Fix (Lead)

**Timestamp:** 2026-04-03T22:30:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (CRITICAL PRIORITY)  
**Scope:** Fix tool call parsing failure in CopilotProvider breaking multi-turn agent loops

**Problem Identified:**
Jon reported that Nova agent (using Claude via Copilot proxy) would exit the agent loop after tool calls without processing results or responding. Logs showed:
```
CopilotProvider: tools=8, messages=3          ← 8 tools sent ✅
finish_reason=tool_calls/"ToolCalls", tool_calls=0  ← API says "tool_calls" but ZERO parsed!
breaking loop at iteration 0, FinishReason="ToolCalls", ToolCalls=0  ← Loop exits
```

**Root Cause Analysis:**
`ParseToolCalls()` in CopilotProvider.cs (line 510) only handled OpenAI's tool call format where `function.arguments` is a JSON **string**:
```csharp
var argumentsJson = argumentsElement.GetString();  // Returns null if arguments is JSON object!
```

When using Claude models through the Copilot proxy, the API returns `arguments` as a JSON **object**, not a string. `GetString()` returned null, resulting in empty arguments dictionaries. The tool call structure was valid but unparsed, causing:
1. `toolCalls` list to be empty (even though API returned tool_calls)
2. Agent loop to exit with `FinishReason=ToolCalls` but `ToolCalls.Count=0`
3. No tool execution, no follow-up turn, no final answer

**Solution Implemented:**

1. **Added format detection** — Check `argumentsElement.ValueKind`:
   - If `String`: OpenAI format → use `GetString()` then deserialize
   - If `Object`: Claude/Copilot format → use `GetRawText()` and deserialize
   - Else: log warning and use empty dict

2. **Added diagnostic logging:**
   - INFO: Raw `tool_calls` JSON from API (before parsing)
   - WARN: Tool calls missing `function` property
   - WARN: Unexpected `arguments.ValueKind`
   - DEBUG: Each parsed tool call with argument count

3. **Changed method signature** — `ParseToolCalls` from `static` to instance method to access `Logger`

**Testing:**
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 396 unit tests passing
- ✅ E2E: Filesystem tool call → parsed → executed → final response ✅
- ✅ E2E: Cron tool call → parsed → executed → final response ✅
- ✅ Logs confirm: `tool_calls=1` (not 0), arguments properly deserialized

**Multi-Turn Flow Verified:**
```
User: "List files in Q:\repos\botnexus"
→ LLM: finish_reason=tool_calls, tool_calls=1 (filesystem)
→ Agent: executing 1 tool calls (iteration 0) ✅
→ Tool: returns directory listing
→ LLM: receives tool results, generates final response
→ User: sees markdown table of files ✅
```

**Key Learning:**
Provider abstraction must handle format variations when proxying different model families. The Copilot API acts as a proxy that normalizes requests but preserves some Claude-specific response formats. Testing with actual API responses (not mocks) is critical.

**Commits:**
- `dd0343a`: fix(copilot): Handle both JSON string and object formats for tool call arguments

**Outcome:**
Multi-turn agent loops now work correctly with Claude models via Copilot proxy. Nova can execute tools, process results, and respond. No more premature loop exits. The fix is backward-compatible with OpenAI format.

---

## 2026-04-03T22:00:00Z — Build Failure Prevention Retrospective (Lead)

**Timestamp:** 2026-04-03T22:00:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen  
**Ceremony:** Retrospective (triggered by recurring build failures)  

**Problem Identified:**
Pattern of recurring build failures over 48 hours. Multiple "fix: resolve X warnings" commits indicate agents were committing without full solution validation. Build breakages discovered post-commit instead of being prevented.

**Timeline of Recent Failures:**
- `3240874`: "fix: resolve nullable model warnings" — CS8603/CS8604/CS8625 across projects
- `274a29e`: "fix: Remove hardcoded gpt-4o defaults" — configuration issues  
- `aa06e4d`: "fix: remove hardcoded Temperature/MaxTokens defaults" — nullable warnings
- `1e02abd`: "fix(gateway): correct invalid route pattern" — runtime bug
- `a99808a`: "Fix cron tool array schema" — API validation error (HTTP 400)
- `d8b8aa7`: "fix: resolve all build warnings across solution" — comprehensive cleanup
- `5f4b0bc`: "Fix pack.ps1 parallel publish race condition" — build script failure

**Root Cause Analysis:**
1. **No solution-wide build validation before commit** — Agents building individual projects instead of `BotNexus.slnx`
2. **No automated pre-commit gate** — Nothing prevents broken commits reaching main
3. **Cascading changes across 27 projects** — Nullable/interface changes ripple through entire solution
4. **Parallel agent work without coordination** — Multiple agents modifying overlapping files

**Prevention Measures Implemented:**

1. ✅ **Pre-commit hook** (`.git/hooks/pre-commit`)
   - Runs `dotnet build BotNexus.slnx` before every commit
   - Runs unit tests before every commit
   - Fails fast with clear error messages
   - Lightweight (<30s on clean builds)

2. ✅ **Team decision added to `.squad/decisions.md`**
   - **Rule 1:** Every agent MUST build full solution before committing (`dotnet build BotNexus.slnx --nologo --tl:off`)
   - **Rule 2:** Every agent MUST run tests before committing (`dotnet test BotNexus.slnx --nologo --tl:off --no-build`)
   - **Rule 3:** Pre-commit hook enforces automatically (bypass with `--no-verify` only for docs)
   - **Rule 4:** Zero tolerance for build warnings (treat as errors)

3. ✅ **Full retrospective document** (`.squad/decisions/inbox/leela-retro-build-failures.md`)
   - Complete timeline, root cause analysis, lessons learned
   - Action items for next iteration (CI/CD pipeline, build badges)

**Current Build Status:**
- ✅ 0 errors, 0 warnings
- ✅ 540 tests passing (396 unit + 110 integration + 23 E2E + 11 deployment)
- ✅ Pre-commit hook active
- ✅ Team decisions documented and enforced

**Outcome:**
Moving from reactive (fixing broken builds) to proactive (preventing broken commits). Pre-commit hook + team discipline should prevent 90% of future breakages. Recommended next steps: CI/CD pipeline for ultimate safety net.

**Commits:** (pending)
- `chore(git): Add pre-commit hook for build validation`
- `docs(squad): Build failure retrospective and prevention rules`

**Leela's Assessment:** Jon was frustrated by repeated build failures — rightfully so. This retrospective addresses the root cause. We now have automated guardrails (pre-commit hook) + documented rules (decisions.md). The build is stable. The rules are clear. Let's keep main green. 🟢

---

## 2026-04-03T19:30:00Z — Agentic Streaming Architecture (Lead)

**Timestamp:** 2026-04-03T19:30:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen  
**Scope:** Real-time streaming and progress events for agentic behavior

**Problem Identified:**
Jon reported that agents feel like request-response chatbots, not true agentic systems. Unlike Copilot CLI or nanobot where users see real-time progress ("Let me check that...", tool usage indicators, streaming responses), BotNexus agents were running silently and only showing the final result.

**Root Cause Analysis:**
1. **No intermediate streaming during LLM generation** — AgentLoop used `ChatAsync()` (non-streaming), collecting the complete response before returning
2. **No progress events during tool execution** — Tool calls happened silently; users saw nothing until completion
3. **Response only at end** — `AgentLoop.ProcessAsync()` returned a single string; no way to emit deltas during processing
4. **Streaming infrastructure existed but unused** — All providers implement `ChatStreamAsync()`, WebSocketChannel implements `SendDeltaAsync()`, IActivityStream ready for events — but AgentLoop never called them

**Architecture Gap Identified:**
The agent pipeline was structured as a blocking synchronous flow:
```
User → Gateway → AgentRunner → AgentLoop.ProcessAsync() [WAITS] 
  → provider.ChatAsync() [COMPLETE RESPONSE]
  → return string
  → AgentRunner.SendAsync() [FINAL MESSAGE ONLY]
```

**Solution Implemented:**
Added callback-based streaming mechanism to enable real-time updates:

1. **Streaming callback parameter** — `AgentLoop.ProcessAsync()` now accepts optional `Func<string, Task>? onDelta` callback
2. **AgentRunner wires callback** — When channel supports streaming (`IChannel.SupportsStreaming`), creates callback that invokes `channel.SendDeltaAsync()`
3. **Conditional streaming mode** — AgentLoop uses `ChatStreamAsync()` when callback is provided AND tools are not in use (tool calls require non-streaming mode)
4. **Tool progress events** — During tool execution, publishes `ActivityEventType.AgentProcessing` events to IActivityStream with messages like "🔧 Using tool: filesystem"
5. **IActivityStream wired through** — Added to AgentRunnerFactory constructor and passed to AgentLoop for progress broadcasting
6. **Backward compatible** — Streaming is opt-in; when no callback provided, uses original `ChatAsync()` behavior

**Technical Implementation:**
- Modified `AgentLoop.cs`: Added `IActivityStream` field, streaming callback parameter, conditional streaming logic
- Modified `AgentRunner.cs`: Creates delta callback when `_responseChannel.SupportsStreaming == true`
- Modified `AgentRunnerFactory.cs`: Accepts `IActivityStream?` parameter, passes to AgentLoop constructor
- Streaming disabled when tools present (`tools == null || tools.Count == 0` check) because most providers don't support tool calls in streaming mode yet

**New Flow:**
```
User → Gateway → AgentRunner → AgentLoop.ProcessAsync(onDelta: callback)
  → [Iteration 1] provider.ChatStreamAsync() → "Let me" [DELTA] → "check that" [DELTA] → "..." [DELTA]
  → [Tool Call] IActivityStream.PublishAsync("🔧 Using tool: filesystem") [PROGRESS]
  → [Iteration 2] provider.ChatAsync() → tool results processed
  → [Iteration 3] provider.ChatStreamAsync() → "Here's what" [DELTA] → "I found" [DELTA]
  → Final response accumulated
```

**What Users Now See:**
1. **Real-time text streaming** — LLM responses appear token-by-token as they're generated
2. **Tool execution indicators** — "🔧 Using tool: X" messages during tool calls
3. **Multi-iteration visibility** — Progress across multiple tool loop iterations
4. **True agentic feel** — System feels alive and working, not frozen

**Testing:**
- All 396 unit tests pass ✅
- Build succeeds with 0 errors ✅
- Backward compatibility maintained (streaming opt-in)

**Commit:** `a4c5ac5` — feat(agent): Add real-time streaming and tool progress events

**Next Steps:**
1. Test integration tests to ensure E2E scenarios work
2. Consider adding streaming support for tool calls in future (requires provider updates)
3. Document streaming behavior in API docs

---

## 2026-04-03T20:23:07Z — Agentic Streaming Sprint (Post-Sprint Sync)

**Status:** ✅ Complete  
**Team:** Leela (Lead) + Bender (Runtime) + Fry (Web)  
**Outcome:** Full streaming pipeline end-to-end from Provider → AgentLoop → Gateway → WebSocket → WebUI  

**Achievements:**
- Core streaming architecture validated across all layers
- Tool progress messages flowing in real-time via onDelta callbacks
- WebSocket clients see tool execution indicators inline with response deltas
- WebUI renders thinking state + tool progress + response in parallel
- All 396 unit tests passing, 0 build errors
- Backward compatible (streaming opt-in)

**Integration Points:**
1. **Leela's work** — ChatStreamAsync + onDelta callback + IActivityStream
2. **Bender's work** — Tool progress forwarding via WebSocket channel
3. **Fry's work** — WebUI message handlers + visual indicators (🔧 tool, 💭 thinking)

**Validation:** 
- End-to-end test: User sends message → sees streaming deltas → sees tool progress → sees final response
- All middleware layers (Agent → Runner → Channel → Gateway → WebSocket) handling streaming correctly
- No regressions in non-streaming paths

**Orchestration Logs:** 
- `.squad/orchestration-log/2026-04-03T20-23-07Z-leela.md`
- `.squad/orchestration-log/2026-04-03T20-23-07Z-bender.md`
- `.squad/orchestration-log/2026-04-03T20-23-07Z-fry.md`

**Session Log:** `.squad/log/2026-04-03T20-23-07Z-streaming-sprint.md`

---

## 2026-04-03T17:45:00Z — System Messages Sprint (Team Sync)

**Delivered by:** Leela (Lead)  
**Collaborating:** Farnsworth (Platform), Bender (Runtime), Fry (Web)  

**Config & Auth:** Hardened write safety (surgical JsonNode updates), auto-reauth on 401/403, secure token storage in ~/.botnexus/tokens/ (not config.json)  
**Cross-Layer:** Device auth flow now secured end-to-end from runtime (Bender) → platform (Farnsworth) → web (Fry)  

**Status:** ✅ Sprint complete. All auth touchpoints secured.

### 2026-04-03 — Critical Auth Issues: Token Loss Root Cause + Auto-Reauth

**Timestamp:** 2026-04-03T17:30:00Z  
**Status:** ✅ Complete  
**Scope:** OAuth token resilience and config safety  

**Issue Reports:**
1. **Token disappearing from config** — Second occurrence of Jon's Copilot token going missing
2. **Silent auth failures** — No auto-reauth when token expires or is missing

**Investigation Findings:**
- **Root Cause Identified:** Token was NOT being lost from config.json. OAuth tokens are stored separately in `~/.botnexus/tokens/copilot.json` by `FileOAuthTokenStore`. The config.json only contains provider settings (API base, timeout, etc.), NOT tokens.
- **Actual Timeline (from logs):**
  - 2026-04-03 09:06:57 — Token naturally expired (expiry: 2026-04-03 14:03:43Z)
  - System correctly cleared expired token and initiated device auth
  - Device code presented: `7C8C-E529` (timeout: 15 min)
  - User didn't complete auth in time → silent failure
  - Subsequent requests failed with no automatic retry
- **Secondary Issue:** When 401/403 occurred during chat, only Copilot access token was cleared, not the GitHub OAuth token. This created a loop where the same expired GitHub token was reused.

**Fixes Implemented:**
1. **Auto-reauth on 401/403** — `CopilotProvider` now:
   - Clears ALL tokens (GitHub OAuth + Copilot access) on auth failure
   - Automatically retries with fresh auth after token exchange failure
   - Provides clear error messages instead of silent failures
   
2. **Surgical Config Updates** — `ConfigFileManager.SaveConfig` refactored to:
   - Use `JsonNode` for partial updates instead of full object serialization
   - Deep merge strategy preserves fields not in C# model
   - Prevents accidental data loss when config contains provider-specific fields

**Technical Details:**
- `InvalidateAndClearTokensAsync()` method clears both `_copilotAccessToken` and `_cachedToken`, then calls `_tokenStore.ClearTokenAsync()`
- Token exchange failure now triggers automatic re-authentication with single retry
- ConfigFileManager uses `JsonNode.Parse()` and `MergeJsonObjects()` for safe updates

**Test Results:** All 6 Copilot provider tests passing, including auth expiry and retry scenarios.

**Commit:** `50f27fe` — feat(auth): Add auto-reauth and surgical config updates

---

### 2026-04-02 — Agent Loop Tool Execution Investigation (IN PROGRESS)

**Issue:** Agent loop appears to stall after tool calls. User report: Nova agent says "I'll look around" (implying tool use) but conversation hangs — no follow-up response with tool results.

**Investigation Progress:**
1. **Code Review:** `AgentLoop.ProcessAsync()` loop logic verified sound:
   - For loop (0..maxToolIterations) correctly continues after tool execution
   - Line 157: Breaks only when `FinishReason != ToolCalls OR ToolCalls.Count == 0`
   - Lines 163-172: Tool execution and history updates work correctly
   - Loop should continue to next iteration after tools execute
   
2. **Test Validation:** Ran `AgentLoopTests` — ALL 10 tests PASS ✅
   - `ProcessAsync_ExecutesToolCalls_AddsToolResultToSession` confirms loop works correctly
   - Mock provider returns `FinishReason.ToolCalls` → loop continues → second call returns `FinishReason.Stop`
   - **Conclusion:** AgentLoop code is correct. Problem is NOT in the loop logic.

3. **Log Analysis:** Examined logs from reported Nova interactions (20:30:42, 20:31:22):
   - Each shows only ONE "Calling provider" log per user message
   - **Expected:** Two provider calls (iteration 0 with tools, iteration 1 with results)
   - **Actual:** Single provider call → immediate response sent
   - No evidence of tool execution in logs (tool logging was LogDebug, not visible)

4. **Diagnostic Logging Added (commit 08851e6):**
   - AgentLoop: Log when breaking loop (iteration, FinishReason, tool count)
   - AgentLoop: Changed tool execution log from Debug → Information level
   - CopilotProvider: Log raw API response (finish_reason, content length, tool calls)

**Root Cause Hypothesis:** One of the following:
1. **Copilot API not returning `finish_reason: "tool_calls"`** — Provider returns "stop" even when tools are suggested  
2. **Tools not being offered to LLM** — Tool definitions not in request payload  
3. **LLM not choosing to use tools** — Valid response, not a bug (agent just responds with text)

**Next Steps:**
- Reproduce bug with new logging to capture actual API responses
- Verify tool definitions are included in Copilot API request payload
- Test if Copilot provider correctly handles tool_calls in response
- If logging reveals Copilot API issue, may need to debug provider or check model/config

**Status:** Investigation complete. Root cause narrowed to 3 hypotheses. Awaiting live test with diagnostic logging to identify exact cause. No code fix applied yet — only diagnostics added.

---

### 2026-04-03 — Skills Platform Sprint (Lead)

**Timestamp:** 2026-04-03T07:50:00Z  
**Status:** ✅ Complete  
**Scope:** Skills platform design and implementation  

**Deliverables:**
- **SkillsLoader** — Dynamic skill discovery from `extensions/skills/`
  - Global and per-agent filtering via config + frontmatter
  - YAML frontmatter metadata parsing
  - Wildcard DisabledSkills patterns (e.g., `disabled-*`, `*-beta`)
- **Context Integration** — Skills injected at runtime via context builder
- **REST API Endpoints**
  - GET /api/skills
  - GET /api/skills/{skillId}
  - POST /api/agents/{agentId}/skills

**Team Coordination:**
- **Fry:** Model dropdown UI depends on SkillsLoader API
- **Kif:** Documentation (640-line skills guide, API reference, config docs) — commit f241ca3
- **Hermes:** 24 new tests (loading, filtering, frontmatter, wildcards) — 396 total passing

---

### 2026-04-03 — Sprint 4 Completion — Model Selector UI + Config Hardening

**Spawn Date:** 2026-04-03T03:22:49Z  
**Status:** Success (4 agents, 7 work items, 0 blockers)

**Summary:**
- **Leela (Lead):** Orchestrated parallel agent work. Validated agent dependencies. No overlaps detected. All agents committed their work as expected.
- **Fry (Web Dev):** Delivered model selector dropdown UI + tool call visibility toggle. Models loaded from /api/providers. Tool messages hidden by default with toggle.
- **Farnsworth (Platform Dev):** Made Temperature, MaxTokens, ContextWindowTokens nullable across config stack. All 3 providers (Copilot, OpenAI, Anthropic) now use own defaults when not explicitly configured. Unblocks future model-specific tuning.
- **Build status:** ✅ All tests passing. Zero errors. Conventional commits used.

**Decisions Archived:**
- User directive: Always route work to agents (no coordinator domain work)
- User directive: Maximize parallel agent spawning (multi-agent is default)
- Decision: Nullable generation settings for provider defaults (architectural)
- Decision: Workspace templates follow OpenClaw pattern (foundational)

**Next Phase:** Model selector integration testing with live providers. Tool visibility in production WebUI.

---

### 2026-04-01 — Initial Architecture Review & Implementation Plan (Rev 2)

**Build & Test Baseline:**
- Solution builds cleanly on .NET 10.0 with only 2 minor warnings (CA2024 async stream, CS8425 EnumeratorCancellation)
- 124 tests pass (121 unit, 3 integration): `dotnet test BotNexus.slnx`
- Build command: `dotnet build BotNexus.slnx`
- NuGet restore required first: `dotnet restore BotNexus.slnx`

**Architecture:**
- Clean contract-first design: Core defines 13 interfaces, implementations in outer modules
- Dependencies flow inward — no circular references detected
- Two entry points: Gateway (full bot platform, port 18790) and Api (OpenAI-compatible REST proxy)
- Gateway is the orchestrator: hosts channels, message bus, agent loop, cron, heartbeat, WebUI
- Message flow: Channel → MessageBus → Gateway loop → AgentRunner → CommandRouter or AgentLoop → Channel response

**Key File Paths:**
- Solution: `BotNexus.slnx` (17 src + 2 test projects)
- Core contracts: `src/BotNexus.Core/Abstractions/` (13 interfaces)
- Core config: `src/BotNexus.Core/Configuration/BotNexusConfig.cs` (root config, section "BotNexus")
- DI entry: `src/BotNexus.Core/Extensions/ServiceCollectionExtensions.cs` (AddBotNexusCore)
- Gateway bootstrap: `src/BotNexus.Gateway/Program.cs` + `BotNexusServiceExtensions.cs`
- Agent loop: `src/BotNexus.Agent/AgentLoop.cs` (max 40 tool iterations)
- Session persistence: `src/BotNexus.Session/SessionManager.cs` (JSONL files)
- WebUI: `src/BotNexus.WebUI/wwwroot/` (vanilla JS SPA, no framework)

**Patterns:**
- All projects target net10.0, ImplicitUsings=enable, Nullable=enable
- Test stack: xUnit + FluentAssertions + Moq + coverlet
- Provider pattern with LlmProviderBase abstract class providing retry/backoff
- Channel abstraction via BaseChannel template method pattern
- MCP (Model Context Protocol) support with stdio and HTTP transports
- Tool system uses ToolBase abstract class with argument helpers
- Configuration is hierarchical POCOs bound from "BotNexus" section in appsettings.json

**Concerns Identified & Roadmap:**
- Anthropic provider lacks tool calling support (OpenAI has it, Anthropic does not)
- Anthropic provider has no DI extension method (OpenAI has AddOpenAiProvider)
- MessageBusExtensions.Publish() uses sync-over-async (.GetAwaiter().GetResult()) — deadlock risk
- No assembly loading or plugin discovery mechanism exists yet
- **DECISION:** Dynamic assembly loading is now foundation. Copilot is P0 with OAuth. 24-item roadmap across 4 releases. See decisions.md for full plan.

**Team Directives Merged:**
1. Dynamic assembly loading — extensions folder-based, configuration-driven, no default loading
2. Conventional commits — all agents use feat/fix/refactor/docs/test/chore format, granular per-item commits
3. Copilot provider P0 — OAuth device code flow, OpenAI-compatible API, only provider Jon uses

**Your Responsibilities (Leela):**
- Lead/Architect oversight of entire roadmap
- Architecture decisions during Phase 1-3 execution
- Plan Q2 features (item 23, Phase 4)
- Monitor team progress and adjust as needed
- Channel implementations (Discord/Slack/Telegram) not registered in Gateway DI — registration code is missing
- Slack channel uses webhook mode but no webhook endpoint exists in Gateway
- No authentication or authorization on any endpoint
- WebUI has no build tooling (vanilla JS, no bundling)
- ProviderRegistry exists but is never registered in DI or used

### 2026-04-01 — Dynamic Extension Architecture Plan

**Key Architectural Decisions:**
- Jon's directive elevates plugin/extension architecture from P2 to THE foundational P0 item. Everything else builds on dynamic assembly loading.
- Config model must shift from typed properties (e.g., `ProvidersConfig.OpenAI`) to dictionary-based (`Dictionary<string, ProviderConfig>`) so extensions are config-driven, not compile-time-driven.
- Folder convention: `extensions/{type}/{name}/` (e.g., `extensions/channels/discord/`). Config keys match folder names.
- Two-tier registration: extensions can implement `IExtensionRegistrar` for full DI control, or fall back to convention-based discovery (loader scans for IChannel/ILlmProvider/ITool implementations).
- WebSocket channel stays hard-coded in Gateway — it's core infrastructure, not an extension.
- Built-in tools (exec, web search, MCP) stay in the Agent project. Only external tools are extensions.
- `AssemblyLoadContext` per extension for isolation and future hot-reload capability.
- ProviderRegistry (currently dead code) gets integrated as the resolver for per-agent provider selection.
- Original 13 review items reshuffled: P0 channel/provider DI items merged into dynamic loading story; P2 plugin architecture promoted to P0.

**Plan Output:** `.squad/decisions/inbox/leela-implementation-plan.md` — 22 work items across 4 phases, mapped to 6 team members with dependencies and sizing.

### 2026-04-01 — Implementation Plan Rev 2: Copilot P0, OAuth, Conventional Commits

**Trigger:** Three new directives from Jon arrived after the initial plan:
1. Copilot provider is P0 — the only provider Jon uses. OAuth auth, not API key.
2. Conventional commits required — granular commits as work completes.
3. Dynamic assembly loading (already incorporated in Rev 1).

**Copilot Provider Architecture Decisions:**
- Copilot uses OpenAI-compatible HTTP format (same chat completions API, streaming, tool calling) against `https://api.githubcopilot.com`.
- Auth is GitHub OAuth device code flow — no API key. Provider implements `IOAuthProvider` to acquire/cache/refresh tokens at runtime.
- New `IOAuthProvider` and `IOAuthTokenStore` interfaces added to Core abstractions. Providers implementing `IOAuthProvider` skip API key validation in the loader and registry.
- `ProviderConfig` gains an `Auth` discriminator (`"apikey"` | `"oauth"`) so the config model can express both auth modes.
- Shared OpenAI-compatible HTTP client logic (request DTOs, SSE streaming) should be extracted to `Providers.Base` to avoid duplication between OpenAI and Copilot providers.
- Default token store uses encrypted file storage under `~/.botnexus/tokens/`. Interface allows future OS keychain implementations.

**Provider Priority Reordering:**
- Copilot: P0 (only provider Jon uses, must work first)
- OpenAI: P1 (mostly working, foundational for testing)
- Anthropic: P2 (tool calling is nice-to-have, deprioritized)

**Plan Changes:**
- Added 2 new work items: `oauth-core-abstractions` (Phase 1, P0, S) and `copilot-provider` (Phase 2, P0, L).
- Demoted `anthropic-tool-calling` from P1 to P2.
- Sprint 2 execution order leads with Copilot provider (Farnsworth: `provider-dynamic-loading` → `copilot-provider`).
- Added Part 6: Process Guidelines with conventional commits specification.
- Updated dependency graph, team member tables, and decision log.
- Plan is now 24 work items across 4 phases.

**Decision Output:** `.squad/decisions/inbox/leela-copilot-provider.md`

## Sprint 4 Summary — 2026-04-01T18:22Z

✅ **COMPLETE** — Documentation & Architecture (2 items)

### Your Deliverables (Leela) — Sprint 4

1. ✅ **architecture-documentation** (7b65671) — Comprehensive system architecture overview
2. ✅ **extension-dev-guide** (bc929a4) — Step-by-step extension developer guide

### Key Achievements

**architecture-documentation:**
- System architecture overview with module boundaries and layer isolation
- Message flow diagrams: Channel → Bus → Gateway → Agent → Tool → Response
- Extension model documentation: folder structure, IExtensionRegistrar pattern, dynamic loading
- Provider/channel/tool abstractions with concrete implementation examples
- Configuration model: hierarchical POCO binding, per-agent overrides, home directory
- Security model: API key auth, extension signing, webhook signature validation
- Observability model: correlation IDs, health checks, metrics emission
- Deployment scenarios: local development, containerized, cloud
- Decision rationale for key architectural choices with RFC links

**extension-dev-guide:**
- Step-by-step extension development workflow for channels, providers, tools
- IExtensionRegistrar pattern implementation guide with code examples
- Configuration binding and dependency injection integration
- Testing strategy with mock implementations for reproducible validation
- Local development loop: project setup, build, deploy to extensions/{type}/{name}/, test
- Packaging and deployment guidelines for production extensions
- Example extension reference implementation (complete Discord channel or GitHub tool)
- Common pitfalls and debugging tips for extension developers

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All 192 tests passing (158 unit + 19 integration + 15 E2E)
- ✅ Code coverage: 98% extension loader, 90%+ core libraries
- ✅ Documentation builds cleanly, all links validated
- ✅ Code examples validated against codebase

### Integration Points
- Works with Farnsworth's extension loader for implementation guidance
- Aligns with Bender's security patterns for extension validation
- Supports Fry's WebUI extensions panel for operational visibility
- Enables Hermes' E2E test scenarios with documented patterns

### Team Status
**ALL 4 SPRINTS COMPLETE:** 24/26 items delivered. Leela: Architecture lead + 6 items across all sprints (review, planning, architecture, extension guide). Production-ready platform ready for external developer community.

### 2026-04-02 — Full Consistency Audit: Docs, Code, Comments

**Trigger:** Jon flagged that `docs/architecture.md` still referenced the pre-`~/.botnexus/config.json` world. Systemic problem — each agent updated their own deliverables but nobody cross-checked other files for stale references.

**Discrepancies Found & Fixed (22 total):**

**architecture.md (8 fixes):**
1. Line 139: Config box said `appsettings.json → BotNexusConfig` — fixed to `~/.botnexus/config.json`
2. Lines 326-358: Extension config example had phantom `LoadPath` property, flat `Channels` (missing `Instances` wrapper), wrong property names (`Token`→`BotToken`, `AppId` removed), `Providers`/`Channels`/`Tools` arrays that don't exist in `ExtensionLoadingConfig`
3. Line 458: Comment said "Bind from appsettings.json" — clarified config.json overrides appsettings.json
4. Line 794: Session default path was `./sessions` — corrected to `~/.botnexus/workspace/sessions`
5. Line 902: API key rotation referenced `appsettings.json` — fixed to `config.json`
6. Lines 984-1015: Installation Layout showed `config/` subfolder with `appsettings.json` and phantom `cache/web_fetch/` — replaced with actual structure from `BotNexusHome.Initialize()` (`config.json` at root, `workspace/sessions/`, no cache)
7. Lines 1019-1023: Config Resolution omitted `config.json` in loading chain — added it between appsettings.{env}.json and env vars
8. Lines 1029-1030: First-Run said "Generate default appsettings.json" — corrected to `config.json`

**configuration.md (3 fixes):**
9. Line 57: Precedence example said `appsettings.json` — fixed to `config.json`
10. Lines 700-708: Precedence order was wrong (code defaults listed after env vars, config.json missing entirely) — rewritten with correct 5-layer order
11. Lines 622-633: Extension registration example used `RegisterServices` method (doesn't exist, actual method is `Register`), used `AddScoped` (actual lifetime is Singleton), took `ProviderConfig` parameter (actual is `IConfiguration`)

**extension-development.md (10 fixes):**
12. Line 35: "enabled in appsettings.json" — added config.json reference
13. Line 222: "bound from appsettings.json" — generalized to "configuration"
14. Lines 235-248: Channel config example missing `Instances` wrapper
15. Line 296: "Enable in appsettings.json" — added config.json reference
16. Line 981: "receive configuration from appsettings.json" — added config.json reference
17. Lines 1004: `ExtensionsPath: "./extensions"` — corrected to `~/.botnexus/extensions`
18. Lines 1010-1030: Config Shape had flat `Channels.discord` (→`Channels.Instances.discord`) and flat `Tools.github` (→`Tools.Extensions.github`)
19. Lines 1167-1176: `FileOAuthTokenStore` example hardcoded `Environment.GetFolderPath` — corrected to use `BotNexusHome.ResolveHomePath()`
20. Lines 1432, 1476, 1536: Three troubleshooting references to "appsettings.json" — added config.json references
21. Lines 1446-1447: Log examples showed `./extensions` — corrected to `~/.botnexus/extensions`
22. Lines 110-117: Extension.targets Publish description said `{PublishDir}` — corrected to `{BOTNEXUS_HOME}`

**Code (1 fix):**
- `BotNexusConfig.cs` XML doc: "bound from appsettings.json" → "bound from the BotNexus section (appsettings.json + ~/.botnexus/config.json)"

**README.md (1 fix):**
- Replaced 1-sentence stub with comprehensive project description (features, quick start, architecture table, config overview, project structure, docs links)

**Items Verified Clean:**
- All 7 extension .csproj files: correct `ExtensionType`, `ExtensionName`, and `Extension.targets` import ✅
- `appsettings.json` (Gateway): defaults match code (ExtensionsPath=`~/.botnexus/extensions`, Workspace=`~/.botnexus/workspace`) ✅
- `appsettings.json` (Api): defaults match code ✅
- No TODO/FIXME/HACK comments in src/ ✅
- `Extension.targets`: build/publish paths consistent with BotNexusHome ✅

**Lesson:** Multi-agent doc/code drift is a systemic risk. When any agent changes a config path, data model, or default value, ALL docs and comments referencing the old value must be updated in the same PR. The consistency audit should be a ceremony — not a one-off fix.

## 2026-04-02 — Team Updates

- **Nibbler Onboarded:** New team member added as Consistency Reviewer. Owns post-sprint consistency audits.
- **New Ceremony:** "Consistency Review" ceremony established, runs after sprint completion or architectural changes. First run (Leela's audit, 2026-04-02) found 22 issues across 5 files.
- **Decision Merged:** "Cross-Document Consistency Checks as a Team Ceremony" (2026-04-01T18:54Z Jon directive) now in decisions.md. All agents should treat consistency as quality gate.

### 2026-04-02 — Agent Workspace, Context Builder & Memory Architecture Design

**Trigger:** Jon requested OpenClaw-style agent workspaces with personality/identity/memory files, a Nanobot-style context builder, and a two-layer memory model.

**Codebase Analysis (what exists today):**
- `AgentLoop` takes a flat `string? systemPrompt` in constructor — no file-based context, no dynamic assembly
- `ContextBuilder` only handles history trimming (token budget via chars ≈ tokens × 4) — no system prompt assembly
- `IMemoryStore` exists in Core with key-value read/write/append/delete/list — plain .txt files under `{basePath}/{agentName}/memory/{key}.txt`
- `AgentConfig` has `SystemPrompt`, `SystemPromptFile`, `EnableMemory`, `Workspace` — no workspace file management
- `BotNexusHome` creates `extensions/`, `tokens/`, `sessions/`, `logs/` — no `agents/` directory
- `ToolRegistry` accepts `ITool` implementations via `Register()` — ready for memory tools

**Key Architectural Decisions:**
1. Agent workspaces at `~/.botnexus/agents/{name}/` (not under `workspace/`) — clean separation of identity/memory from transient sessions
2. New `IContextBuilder` interface replaces flat `string? systemPrompt` on `AgentLoop` — assembles system prompt from IDENTITY.md, SOUL.md, USER.md, AGENTS.md, TOOLS.md, MEMORY.md, and daily notes
3. New `IAgentWorkspace` interface for workspace file I/O — separate from `IMemoryStore` (different access patterns)
4. Extend `IMemoryStore` with key conventions (`daily/YYYY-MM-DD` for dailies) rather than replacing the interface
5. AGENTS.md auto-generated from config + IDENTITY files at session start — prevents staleness
6. TOOLS.md auto-generated from `ToolRegistry.GetDefinitions()` — agent always knows its capabilities
7. Include HEARTBEAT.md — BotNexus already has heartbeat infrastructure, natural fit for memory consolidation
8. Keyword-based memory search first (grep-style), hybrid vector search as future enhancement
9. Preserve `SystemPrompt`/`SystemPromptFile` backward compat — simple agents don't need workspace files
10. Memory consolidation via LLM call triggered by heartbeat — configurable model and interval

**Plan Output:** `.squad/decisions/inbox/leela-workspace-memory-plan.md` — 22 work items across 5 phases, ~15-21 days estimated, mapped to team members with full dependency graph.

**Phase Summary:**
- Phase 1: Foundation (5 items) — `IContextBuilder`, `IAgentWorkspace`, config additions, `BotNexusHome` agents dir, `MemoryStore` path migration
- Phase 2: Implementation (8 items) — `AgentWorkspace`, `AgentContextBuilder`, `AgentLoop` refactor, 3 memory tools, registration, DI wiring
- Phase 3: Consolidation (3 items) — `IMemoryConsolidator`, LLM-based consolidation, heartbeat trigger
- Phase 4: Testing (5 items) — Unit tests for all new components + integration tests
- Phase 5: Documentation (1 item) — Workspace/memory docs + architecture.md updates

### 2026-04-02 — Centralized Cron Service Architecture Design

**Trigger:** Jon directive (2026-04-01T20:35Z) — cron must be a first-class independent service managing ALL scheduled work centrally, not a per-agent helper or embedded in heartbeat.

**Codebase Analysis (what exists today):**
- `CronService` is a generic scheduler: `Schedule(name, cron, action)` with `Func<CancellationToken, Task>` callbacks. No awareness of agents, channels, sessions, or job types.
- `HeartbeatService` is a separate `BackgroundService` that records health beats and triggers memory consolidation per agent on interval.
- `AgentConfig.CronJobs` exists in config as `List<CronJobConfig>` but is **never wired** to execution — dead configuration.
- `CronTool` lets agents schedule/remove jobs at runtime but payloads aren't processed.
- **Critical gap:** `IAgentRunnerFactory` does not exist. No way to create `IAgentRunner` instances on demand. The `AgentRouter` expects `IEnumerable<IAgentRunner>` from DI but nothing registers them. Factory pattern exists for `IContextBuilder` and `IAgentWorkspace` but not for runners.
- `ChannelManager.GetChannel(name)` provides case-insensitive channel lookup — ready for cron output routing.
- `IActivityStream` provides pub/sub for `ActivityEvent` — ready for cron observability.

**Key Architectural Decisions:**
1. Central `Cron.Jobs` config replaces per-agent `AgentConfig.CronJobs` — single place to manage all scheduled work
2. Three job types: `AgentCronJob` (runs agent via AgentRunner), `SystemCronJob` (no LLM, direct actions), `MaintenanceCronJob` (consolidation, cleanup, health)
3. `AgentCronJob` uses new `IAgentRunnerFactory` → full context/memory/workspace pipeline, consistent with interactive flow
4. `IAgentRunnerFactory` is a prerequisite that also fixes an existing gap (no runner creation mechanism in codebase)
5. HeartbeatService replaced entirely — consolidation becomes a cron MaintenanceJob, health beat is implicit from cron tick
6. `IHeartbeatService` kept as thin adapter during transition for backward compatibility
7. Session modes: `new` (fresh per run), `persistent` (same session across runs), `named:{key}` (explicit key)
8. Channel output routing via existing `ChannelManager.GetChannel()` — no new abstractions
9. `ISystemActionRegistry` for extensible non-agent actions — extensions can register custom system actions
10. Correlation IDs flow end-to-end: cron tick → job → agent run → channel output → activity stream
11. Cronos library retained for cron expression parsing
12. Execution history bounded to 1000 entries with LRU eviction

**Plan Output:** `.squad/decisions/inbox/leela-cron-service-plan.md` — 22 work items across 5 phases, ~17-23 days estimated, mapped to team members with full dependency graph.

**Phase Summary:**
- Phase 1 (Sprint A): Foundation — 4 items: Core interfaces, config model, agent runner factory, system action registry
- Phase 2 (Sprint B): Implementation — 5 items: CronService, AgentCronJob, SystemCronJob, MaintenanceCronJob, CronJobFactory
- Phase 3 (Sprint C): Integration — 4 items: DI wiring, heartbeat migration, CronTool update, legacy config migration
- Phase 4 (Sprint D): Observability — 4 items: API endpoints, metrics, health check, activity events
- Phase 5 (Sprint E): Testing & Docs — 5 items: Unit tests, integration tests, E2E tests, documentation, consistency review



### 2026-04-02 — Sprint 5 Complete: Agent Workspace, Memory, Deployment Lifecycle + Kif Onboarding

**Overview:** Sprint 5 delivered the core agent infrastructure (workspace + identity), memory management system (long-term + daily with consolidation), and comprehensive deployment lifecycle validation (10 real-process E2E scenarios). Team expanded with Kif as Documentation Engineer.

**Achievement:** 48/50 items done. 2 P2 items deferred (Anthropic tool-calling, plugin architecture deep-dive). Team grew from 6 to 8 agents (Nibbler + Zapp added). Kif added as 9th agent for documentation and getting-started guide.

**Workspace & Identity (Leela ws-01/02, Farnsworth ws-03/04/05):**
- Agent workspace structure: ~/.botnexus/agents/{agent-name}/ with SOUL/IDENTITY/USER/AGENTS/TOOLS/MEMORY files
- BotNexusHome.Initialize() creates workspace structure and stub files
- Multi-agent awareness via auto-generated AGENTS.md (from config + identity files)
- File-based persistent identity and personality system
- Integration tests for workspace creation, file structure, and initialization

**Context Builder & Memory Services (Bender ws-06 through ws-12, Farnsworth ws-13):**
- IContextBuilder interface replaces flat systemPrompt with file-driven context assembly
- Context loads workspace files (SOUL, IDENTITY, USER, AGENTS, TOOLS, MEMORY) at session start
- Memory tools added: memory_search (FTS), memory_save, memory_get, memory_list
- Daily memory files (~/.botnexus/agents/{name}/memory/YYYY-MM-DD.md) auto-loaded for today + yesterday
- Long-term MEMORY.md consolidation via LLM-based distillation
- Token budget trimming integrated into context builder

**Heartbeat & Memory Consolidation (Bender ws-15, Farnsworth ws-16):**
- IHeartbeatService runs daily consolidation job: distills daily files → MEMORY.md
- Controlled pruning prevents unbounded memory growth
- Health check integrated with heartbeat service

**Deployment Lifecycle Testing (Hermes ws-17 through ws-21):**
- Implemented 10 real-process E2E scenarios in tests/BotNexus.Tests.Deployment/
- GatewayProcessFixture: spawns Gateway via dotnet run with isolated temp dirs, health probes
- Scenarios cover: install, config creation, startup, agent workspace setup, message routing, multi-agent handoff, session persistence, graceful shutdown, restart with session restoration, platform update, health management, OAuth integration
- All 10 pass. Scenario registry now 86% coverage (48/56 total scenarios).
- Key discovery: Sessions persisted across restart; workspace creation is lazy (on first message); extension loading is explicit, not auto-scanning.

**Scenario Registry & Team Expansion (Leela ws-22, Zapp scenario-registry + deployment-lifecycle-tests):**
- Zapp added to team: owns E2E deployment validation, deployment lifecycle tests
- Nibbler added to team: owns consistency reviews, post-sprint audits
- Kif added to team: owns user-facing documentation, getting-started guide, style guide, GitHub Pages
- Scenario registry process formalized: Hermes maintains as living document after sprint completion
- Consistency review ceremony established: triggered after sprint or architecture changes

**Kif — Documentation Engineer Onboarding (Kif getting-started guide):**
- Created `docs/getting-started.md` — 706-line comprehensive guide covering prerequisites through OpenClaw migration
- 13 sections: Prerequisites, Installation, First Run, Initial Configuration, Adding Channels, Adding Providers, Creating Custom Tool, Running Agents, Building Custom Agents, Deployment Scenarios, Troubleshooting, OpenClaw Integration, Reference Links
- Every code example, config snippet, and API endpoint verified against live source code
- Updated README.md with prominent Getting Started link and full documentation listing
- All steps tested end-to-end for accuracy and usability
- Supports 100% scenario coverage and first-time user onboarding

**Process Updates:**
- All decisions from inbox merged into decisions.md (decisions #9, #10, #11)
- Inbox files deleted (merged, not orphaned)
- Cross-agent consistency checks now a formal ceremony with Nibbler as owner
- Documentation updated and consistency audit completed (Leela: 22 issues fixed across 5 files)

**Outstanding:**
- 2 P2 items deferred to next sprint: Anthropic tool-calling feature parity, plugin architecture deep-dive
- Hearbeat service still needs HealthCheck.AggregateAsync() implementation (minor gap)
- Plugin discovery (AssemblyLoadContext per extension) not yet fully tested with real extension deployments
- GitHub Pages setup pending (Kif P1 item for next sprint)
- Documentation style guide needed (Kif P1 item for next sprint)

## Session Completion: 2026-04-02

**Sprints Completed:** 1-6  
**Items Done:** 71 of 73 (97.3%)  
**Tests Passing:** 395  
**Scenario Coverage:** 64/64 (100%)  
**Team Size:** 12 agents  

**Major Achievements:**
- Dynamic extension loading fully operational
- Copilot OAuth integration complete and tested
- Multi-agent routing with assistant classification deployed
- Agent workspaces with durable file storage working
- Centralized memory system with consolidation running
- Centralized cron service architecture finalized (pending implementation)
- Authentication/authorization layer deployed across Gateway, WebSocket, REST
- Security hardening: ~/.botnexus/ live environment fully protected
- Observability framework (metrics, tracing, health checks) integrated
- WebUI deployed with real-time status feeds
- Full E2E scenario coverage: 64/64 scenarios passing

**Deferred (P2):** 2 Anthropic items awaiting clarification

**Decisions Merged:**
1. Cron service as independent first-class scheduler
2. Live environment protection (~/.botnexus/ isolation)

**Next Steps:** Production deployment readiness, Sprint 7 planning for P2 items.



### 2026-04-02 — CLI Tool, Config Hot Reload & Doctor Command Architecture

**Directive:** copilot-directive-2026-04-02T0008 (Jon Bullen)

**Deliverable:** .squad/decisions/inbox/leela-cli-doctor-plan.md

**Three capabilities designed:**

1. **CLI Tool (otnexus command):** New src/BotNexus.Cli/ project as a dotnet tool (installable via dotnet tool install). Uses System.CommandLine for parsing. 16 commands across 6 groups: lifecycle (start/stop/restart/status), config (validate/show/init), agent (add/list/workspace), provider (add/list), channel (add), extension (list), doctor, and logs. Two operating modes: offline (reads config.json directly) and online (queries Gateway REST API). Process management via PID file + health endpoint polling.

2. **Config Hot Reload:** 
eloadOnChange: true + ConfigReloadOrchestrator hosted service using IOptionsMonitor<BotNexusConfig>.OnChange() with 500ms debounce. Defined what CAN hot reload (agents, cron jobs, API key) vs what REQUIRES restart (Kestrel binding, extension loading, new channels/providers). ApiKeyAuthenticationMiddleware migrated from IOptions to IOptionsMonitor for live API key updates.

3. **Doctor Command:** IHealthCheckup interface in Core. 13 built-in checkups across 6 categories (configuration, security, connectivity, extensions, permissions, resources). Implementations in new BotNexus.Diagnostics project. CheckupRunner executes sequentially with timing. Supports offline (CLI) and online (Gateway /api/doctor endpoint) modes. --category filtering and --json output.

**Work Items:** 28 items across 4 phases:
- Phase 1 (Foundation): 9 items — Core interface, Diagnostics project, all 13 checkups, tests
- Phase 2 (CLI Commands): 13 items — CLI project scaffold, all commands, integration tests
- Phase 3 (Hot Reload): 5 items — reloadOnChange, orchestrator, IOptionsMonitor migration, cron reload, tests
- Phase 4 (Gateway API): 1 item — /api/status, /api/doctor, /api/shutdown endpoints

**Key Decisions:**
- CLI is separate dotnet tool, not embedded in Gateway (separation of concerns)
- IHealthCheckup in Core; implementations in Diagnostics (keeps Core dependency-free)
- Hot reload via IOptionsMonitor.OnChange() with debounce, not custom file watcher
- Kestrel binding + extension loading are restart-only (immutable after Build())
- PID file + health endpoint for process management
- Config mutations via direct JSON manipulation (CLI writes, hot reload picks up)

**Team Assignments:** Amy (Core/Diagnostics/Gateway), Bender (CLI), Fry (Tests)

### 2026-04-02 — Sprint 7 Complete: CLI Tool, Doctor Diagnostics, Config Hot Reload

**Cross-Agent Update:** Sprint 7 was a major infrastructure sprint combining three interconnected capabilities: the otnexus CLI tool, pluggable doctor diagnostics system, and config hot reload. The CLI tool added 16 commands via System.CommandLine framework for managing BotNexus. The doctor system provides 13 diagnostic checkups across 6 categories (config, security, connectivity, extensions, providers, permissions, resources) with optional auto-fix capability and two fix modes (interactive --fix, force --fix --force). Config hot reload lets the Gateway watch ~/.botnexus/config.json and automatically reload without restart using IOptionsMonitor + FileSystemWatcher. Also deployed three Gateway REST endpoints (/api/status, /api/doctor, /api/shutdown) and fixed a P0 first-run bug where extensions failed to load. Test coverage grew to 443 tests (322 unit + 98 integration + 23 E2E). Kif (Documentation Engineer) joined the team. See .squad/log/2026-04-02T00-34-sprint7-complete.md and .squad/decisions.md Sprint 7 section for full details.

---

### Agent File Restructure — squad.agent.md Trimming

**Architecture Decision:** Split squad.agent.md into operational rules (kept in agent file) and lifecycle/setup content (moved to `.squad/skills/squad-lifecycle/SKILL.md`). The agent file dropped from 1287→982 lines (−24%). 14 sections removed: Init Mode (both phases), Casting & Persistent Naming (all subsections), Adding/Removing Team Members, Plugin Marketplace, Worktree Lifecycle Management, Pre-Spawn: Worktree Setup, Multi-Agent Artifact Format, Constraint Budget Tracking, GitHub Issues Mode, PRD Mode, Human Team Members, Copilot Coding Agent Member.

**Key additions:** Lifecycle Operations routing table, pre-response self-check constraint (anti-inline-work guard), skill entry in Source of Truth Hierarchy, lightweight init check referencing skill file.

**Pattern:** On-demand loading — setup/lifecycle instructions load only when triggered, not on every session. Keeps the coordinator's context window focused on orchestration rules that matter for every interaction.

**User Preference (Jon):** Wants the agent file lean — load-on-demand for infrequent operations, always-loaded for critical operational rules.

---

### Internal Tools Auto-Registration — Review & Commit

**What:** Reviewed and committed feat that auto-registers 5 built-in tools (FilesystemTool, ShellTool, WebTool, MessageTool, CronTool) for every agent session via `AgentRunnerFactory.CreateInternalTools()`. Added `AgentConfig.DisallowedTools` opt-out property.

**Architecture Notes:**
- Filtering happens at two levels: factory filters internal+external tools before injection, AgentLoop filters memory tools via `RegisterIfAllowed()`. No double-filtering — each level handles its own tool set.
- `_channels` refactored from single `IChannel?` to `IReadOnlyList<IChannel>` to support CronTool multi-channel needs. `FirstOrDefault()` still used where single channel needed.
- ShellTool conditionally added based on `ToolsConfig.Exec.Enable` — security gate preserved.
- `DisallowedTools` uses case-insensitive `HashSet<string>` for lookup — consistent with tool name matching.
- All 466 tests pass (322 unit + 110 integration + 23 E2E + 11 deployment).

---

## 2026-04-02T23:19:04Z — Internal Tools Auto-Registration (Parallel Session with Bender)

**Context:** Leela and Bender worked in parallel. Leela implemented per-agent tool exclusion; Bender fixed parallel pack build corruption. Both committed under same hash (0f162a1).

**Work:** 
- Implemented `AgentConfig.DisallowedTools` property for selective tool suppression per agent
- Refactored `AgentRunnerFactory` to respect DisallowedTools during internal tool instantiation
- Updated `AgentLoop` execution path to filter disallowed tools before dispatch
- All existing tests remain passing

**Team Update (Bender's Parallel Work):**
- Bender fixed parallel pack corruption by switching from `--no-build parallel publish` to `--no-restore` sequential builds
- Added `/p:UseSharedCompilation=false` flag to prevent Roslyn cache conflicts
- Both changes committed together as 0f162a1

**Decisions Merged:** leela-commit-instructions.md, leela-agent-file-restructure.md (added during this session)

**Files Modified:**
- src/BotNexus.Agents/Execution/AgentConfig.cs
- src/BotNexus.Agents/Execution/AgentRunnerFactory.cs
- src/BotNexus.Agents/Execution/AgentLoop.cs

---

### 2026-04-02 — Fixed pack.ps1 Parallel Publish Race Condition

**Trigger:** Bender's `pack.ps1` implementation used "restore once + publish --no-restore in parallel", which still caused race conditions. Parallel `dotnet publish --no-restore` processes were building simultaneously and fighting over shared `obj/` directories in BotNexus.Core and other shared dependencies.

**Root Cause Analysis:**
- All 9 components (Gateway, CLI, 3 providers, 3 channels, 1 tool) depend on BotNexus.Core
- Providers share BotNexus.Providers.Base (3 projects)
- Channels share BotNexus.Channels.Base (4 projects)
- `dotnet publish --no-restore` still **builds** — only skips package restore
- Multiple parallel builds of the same project create file contention in `obj/` directories, causing intermittent failures like "PE metadata corruption" or "access denied"

**Solution:**
- Changed from "restore once + publish --no-restore in parallel" to **"build once + publish --no-build in parallel"**
- `dotnet build` the full solution ONCE — compiles all shared dependencies serially, no contention
- `dotnet publish --no-build` in parallel — only copies pre-built binaries, safe to parallelize
- Increased ThrottleLimit from 4 to 8 since publish is now just file operations (no CPU-bound builds)

**Key Learnings:**
- `--no-restore` ≠ "skip build" — it only skips package fetch. Build still happens.
- `--no-build` is the correct flag for parallel publish after a solution-wide build
- Shared project dependencies make parallel builds fundamentally unsafe without isolation
- Building the solution once is faster AND more reliable than parallel project builds with shared deps

**Testing:**
- ✅ `.\scripts\pack.ps1` completes successfully, all 9 packages created

---

## 2026-04-03 — Loop Alignment & UI Fix

**Cross-Team Update:** Fixed critical agent loop pattern and system prompt issues. Root cause analysis: agents were narrating work instead of executing because system prompt lacked explicit tool-use instructions. Removed non-standard keyword continuation detection from AgentLoop.cs and implemented nanobot-style finalization retry (proven across Anthropic, OpenAI, nanobot production systems). Added explicit "USE tools proactively" instructions to AgentContextBuilder.BuildIdentityBlock(): "You have access to tools to accomplish tasks. USE them proactively — do not just narrate what you would do." Simultaneously, Fry fixed UI rendering bugs (CSS margin cleanup on hidden tool messages + WebSocket renderer tool call context). Decision "Agent Loop Standard Pattern" created, implemented, and merged to decisions.md. Commits: Leela 8951925, Fry 74d54d6. See .squad/log/2026-04-03T05-51-33Z-loop-alignment-ui-fix.md.

- ✅ `.\scripts\dev-loop.ps1` end-to-end test passes (pack + install + gateway start)
- ✅ Build time ~30s, parallelism maintained in publish/packaging phase

**Commit:** 5f4b0bc "Fix pack.ps1 parallel publish race condition"

### 2026-04-02 — Fixed Cron Tool Array Schema for Copilot API Compliance

**Trigger:** Jon reported errors when running BotNexus. Investigation of platform logs revealed HTTP 400 errors from Copilot API: `"Invalid schema for function 'cron': In context=('properties', 'output_channels'), array schema missing items."`

**Root Cause:**
- The `cron` tool's `output_channels` parameter was defined as type "array" but lacked the required `items` property
- JSON Schema specification requires arrays to specify what type of elements they contain via an `items` field
- `ToolParameterSchema` record only supported `Type`, `Description`, `Required`, and `EnumValues` — no `Items` property
- Both CopilotProvider and OpenAiProvider's `BuildParameterSchema` methods didn't handle nested schema for array items

**Solution:**
1. Added `Items` property to `ToolParameterSchema` record (nullable, for recursive schema definition)
2. Updated `CopilotProvider.BuildParameterSchema` to include `items` field when parameter has Items defined
3. Updated `OpenAiProvider.BuildParameterSchema` to include `items` field when parameter has Items defined
4. Fixed `CronTool` definition to specify `Items: new("string", "Channel name")` for output_channels array

**Key Files Modified:**
- `src/BotNexus.Core/Models/ToolDefinition.cs` — Added Items parameter to ToolParameterSchema
- `src/BotNexus.Providers.Copilot/CopilotProvider.cs` — BuildParameterSchema now includes items for arrays
- `src/BotNexus.Providers.OpenAI/OpenAiProvider.cs` — BuildParameterSchema now includes items for arrays
- `src/BotNexus.Agent/Tools/CronTool.cs` — output_channels now specifies Items type

**Testing:**
- ✅ Solution builds cleanly: `dotnet build --no-incremental` (exit 0)
- ✅ CronTool tests pass: 2 succeeded, 0 failed
- ✅ No breaking changes to existing tool definitions

**Key Learnings:**
- Log location: `~/.botnexus/logs/botnexus-{date}.log` (Serilog with daily rolling, 14 day retention)
- Copilot API strictly enforces JSON Schema compliance, OpenAI may be more lenient
- When defining array-type tool parameters, ALWAYS specify Items to avoid API rejection
- Tool schema validation happens at runtime when provider serializes tools for LLM API
- Both OpenAI and Copilot providers use similar schema building logic (Anthropic doesn't support tools yet)

**Commit:** a99808a "Fix cron tool array schema for Copilot API compliance"

### 2026-04-03 — Workspace Template Integration from OpenClaw

**Task:** Replace placeholder workspace template stubs with rich, useful defaults inspired by OpenClaw framework.

**Research:**
- Found OpenClaw repo: openclaw/openclaw on GitHub (346k stars, main TypeScript AI assistant framework)
- Located official templates in docs/reference/templates/:
  - SOUL.md — Agent personality, values, boundaries, and behavioral guidelines
  - IDENTITY.md — Name, creature type, vibe, emoji (agent self-definition)
  - USER.md — Human profile capture (name, pronouns, timezone, context)
  - AGENTS.md — Workspace guide with startup routine, memory practices, boundaries
  - TOOLS.md — Local environment-specific notes and preferences
  - HEARTBEAT.md — Periodic instruction patterns
  - MEMORY.md concept described in AGENTS.md (curated long-term memory)

**Implementation:**
- Updated src/BotNexus.Agent/AgentWorkspace.cs with OpenClaw-inspired templates
- Replaced HTML comment stubs with structured, example-rich templates
- Added AGENTS.md and TOOLS.md to BootstrapFiles dictionary (new files)
- Enhanced HEARTBEAT.md with example tasks and guidance
- Created comprehensive MEMORY.md template with example entries and maintenance guidance
- All templates provide clear sections, example content, and explain their purpose

**Key Principles from OpenClaw:**
- **Personality over placeholders:** Templates establish an agent identity and voice
- **Examples show the way:** Each file includes sample content showing what good entries look like
- **Memory is file-based:** "Mental notes" don't survive sessions — write everything down
- **Clear boundaries:** Define what's safe to do freely vs. what needs permission
- **Curated memory:** Daily logs (raw) vs. MEMORY.md (distilled wisdom)

**Files Modified:**
- src/BotNexus.Agent/AgentWorkspace.cs — Replaced all 5 stub templates + added 2 new files (AGENTS.md, TOOLS.md)

**Testing:**
- ✅ Solution builds cleanly: dotnet build (exit 0)
- ✅ No breaking changes to workspace initialization logic
- ✅ All template files are valid markdown with proper structure

**Key Learning:**
- OpenClaw's templates focus on agent autonomy and personality development
- Templates should be opinionated enough to guide but generic enough to adapt
- Memory practices are critical: file-based persistence, daily vs. long-term, proactive maintenance
- Workspace files aren't just config — they're the agent's continuity across sessions

**Commit:** 70f4696 "Replace workspace template stubs with rich OpenClaw-inspired defaults"


---

### 2026-04-03 — CLI Agent Add: Workspace Bootstrap + ID Normalization

**What:** Fixed CLI `botnexus agent add` command to properly bootstrap agent workspaces and normalize agent IDs.

**Issues Fixed:**
1. **Workspace bootstrapping:** CLI now calls `AgentWorkspace.InitializeAsync()` after adding agent to config. Creates agent folder with SOUL.md, IDENTITY.md, USER.md, AGENTS.md, TOOLS.md, HEARTBEAT.md, MEMORY.md, and memory/daily/ subdirectory.
2. **ID normalization:** Agent IDs now normalized to lowercase with special chars replaced by dashes (e.g., "Nova Star" → ID "nova-star", folder "nova-star"). Display name preserves original casing in config.

**Technical Changes:**
- Added `NormalizeAgentId()` helper: lowercase + regex to replace non-alphanumeric with dashes, trim/collapse consecutive dashes
- Updated `agent add` command to use normalized ID for config key and workspace creation
- Updated `agent workspace` command to normalize input for folder lookup
- Added BotNexus.Agent project reference to CLI project
- Agent workspace folders now consistently use normalized lowercase IDs

**Architecture Impact:**
- Agent ID normalization happens at CLI boundary — config keys, folder names, workspace paths all use lowercase IDs
- `AgentConfig.Name` property stores display name with proper casing for UI

---

## 2026-04-05T00:00:00Z — pi-mono Agent Port Planning (Lead)

**Timestamp:** 2026-04-05T00:00:00Z  
**Status:** ✅ Plan Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Create a multi-sprint plan for porting `@mariozechner/pi-agent-core` into BotNexus

## Learnings

### Gateway Phase 4 Wave 1 Design Review (2026-04-05)
- **Reviewed:** 12 commits covering runtime hardening, config validation endpoint, multi-tenant API key auth
- **Overall grade: A-** — well-executed runtime fixes with textbook async patterns
- **Key patterns observed:**
  - `AsyncLocal<HashSet<string>>` for per-flow recursion detection — correct isolation, clean `IDisposable` cleanup
  - `TaskCompletionSource` with `RunContinuationsAsynchronously` for deduplicating concurrent creation — textbook pattern, replaces lock-held async creation
  - `ConcurrentDictionary` with `TryAdd`/`TryUpdate` optimistic loop for rate limiting — lock-free, amortized cleanup every 128th update
  - Options pattern migration from `LoadAsync().GetAwaiter().GetResult()` to synchronous `Load()` — eliminates sync-over-async deadlock risk
- **P1 findings:** (1) Config validation endpoint allows unauthenticated filesystem path probing — needs auth gating or path restriction; (2) Two recursion guard tests are `Skip`-ped — should be enabled
- **P2 findings:** (1) `ApplyPlatformConfig` manual property copy is fragile; (2) WebSocket handler accumulating rate-limiting responsibility; (3) No max call-chain depth limit; (4) Dual config source-of-truth (legacy + nested) without conflict warnings
- **Review written to:** `.squad/decisions/inbox/leela-phase4-design-review.md`

### pi-mono Agent Architecture
- **types.ts**: 10+ type definitions forming a discriminated-union event system, typed tool definitions with TypeBox schemas, extensible messages via declaration merging, AgentState as mutable state container
- **agent-loop.ts**: The core engine — outer loop (follow-ups) → inner loop (tool calls + steering) → streamAssistantResponse → executeToolCalls. Two execution modes: sequential and parallel. BeforeToolCall/AfterToolCall hooks allow skip/modify.
- **agent.ts**: Stateful wrapper with PendingMessageQueue, ActiveRun lifecycle, subscribe/unsubscribe pattern, abort/waitForIdle/reset. Single active run constraint.
- **proxy.ts**: Browser-specific proxy streaming — not applicable to C#/.NET server-side. Excluded from port scope.

### Key Architectural Decisions for the Port
- **Project name:** `BotNexus.AgentCore` — mirrors pi-agent-core naming
- **Dependency boundary:** Only `BotNexus.Providers.Base` (→ Core). Zero coupling to existing `BotNexus.Agent`
- **EventStream:** `ChannelReader<AgentEvent>` via `System.Threading.Channels` — idiomatic C# async streaming
- **Events:** Record hierarchy with abstract base + 10 subtypes, pattern-matchable
- **Messages:** Independent `AgentMessage` hierarchy, NOT extending `ChatMessage`. Convert at the LLM call boundary.
- **Tools:** New `IAgentTool` interface (richer than `ITool`) with `ToolAdapter` for wrapping existing tools
- **AbortSignal → CancellationToken**, subscribe → IDisposable pattern
- **Thread safety required** — unlike JS single-threaded model, Agent class needs synchronization

### Key File Paths
- Plan document: `.squad/decisions/inbox/leela-agent-port-plan.md`
- Target project: `src/BotNexus.AgentCore/`
- Target test project: `tests/BotNexus.AgentCore.Tests/`
- Existing agent (DO NOT MODIFY): `src/BotNexus.Agent/`
- Existing Core types to reuse: `ChatMessage`, `ChatRequest`, `ToolDefinition`, `ToolCallRequest`, `StreamingChatChunk`, `LlmResponse`, `ILlmProvider`, `ModelDefinition`

### Reuse Strategy
- **Reuse directly:** `ToolDefinition`, `ILlmProvider`, `StreamingChatChunk`, `LlmResponse`, `ModelDefinition`, `ChatRequest`
- **Wrap/convert:** `ChatMessage` ↔ `AgentMessage` at the boundary
- **New:** `AgentEvent`, `AgentState`, `IAgentTool`, `AgentToolResult`, `AgentLoopConfig`, `Agent`

---

## 2026-04-06T00:00:00Z — Port Audit Phase 2 Sprint Retrospective (Lead)

**Timestamp:** 2026-04-06T00:00:00Z  
**Status:** ✅ Complete  
**Requested by:** sytone  
**Scope:** Facilitate retrospective for Port Audit Phase 2 fix sprint (79 findings, 15 P0s)

**Outcome:**
Sprint closed successfully. 18 commits across 5 agents resolved all 15 P0s and 14 P1s. 372 tests passing, 0 build errors, 0 warnings. Architecture grade upgraded from A− to A. All 8 pre-sprint architecture decisions (AD-1 through AD-8) were followed without deviation.

**What went well:**
1. Design-first approach — 8 architecture decisions locked before coding eliminated mid-sprint debates
2. 5-way parallel execution with zero merge conflicts or duplicated work
3. Every commit independently valid (build + test green)
4. 100% P0 closure rate across both audit phases (25/25 total)

**What to improve:**
1. Test count (13 regression tests) fell short of 50+ target — need explicit test expectations per fix
2. P1 fixes happened implicitly alongside P0 commits — need explicit backlog tracking
3. Review gates should trigger after each commit batch, not just at sprint end
4. Provider conformance test suite still not built (flagged in Phase 1 retro too)

**Action items for next sprint:**
1. Build provider conformance test suite (Hermes)
2. Triage remaining 15 P1s by user-facing impact (Leela + Team)
3. Begin AgentSession design sprint with AD-1 composition constraint (Farnsworth + Bender)
4. Implement streaming error recovery — top P1 candidate (Bender)

**Artifacts:**
- Retro document: `.squad/decisions/inbox/leela-retro-port-audit-sprint-2.md`
- Updated status: `.squad/identity/now.md`

**Learnings:**
- Design review before implementation is the single highest-leverage activity for multi-agent sprints. Eight decisions in one session saved dozens of mid-sprint coordination messages.
- Parallel execution scales linearly when ownership boundaries are clean. Five agents, zero conflicts.
- Implicit P1 fixes are a velocity trap — they feel efficient but create tracking debt. Next sprint: explicit P1 backlog with per-item status.
- The "conformance test suite" action item has now appeared in two consecutive retros. If it appears a third time, it should be escalated to P0 priority.

**Decision Document:** `.squad/decisions/inbox/leela-agent-port-plan.md`
- Workspace bootstrap uses existing `AgentWorkspace.InitializeAsync()` — no duplication of bootstrap logic

**Build Status:** ✅ All changes compile cleanly. No test regressions.


---

### 2026-04-02 — Agent Loop Aligned to Industry Standard

**Task:** Two-part fix: (1) Remove Bender's non-standard continuation detection, (2) Investigate why agents narrate instead of using tools.

**Part 1 — Remove Keyword-Based Continuation Detection:**
- **Removed:** Bender's keyword detection ("I'll", "I will", "proceed", "next") that prompted agents to continue
- **Added:** Nanobot-style finalization retry — when LLM returns blank content (no tool calls, no text), retry ONCE with "You have finished the tool work. Provide your final answer now." with tools disabled
- **Standard Pattern Now:**
  - Tool calls present → execute, continue loop
  - No tool calls + text content → final answer, break
  - No tool calls + blank content → finalization retry (nanobot pattern), then break
  - Max iterations → force stop

**Part 2 — Root Cause Analysis & Fix:**
- **Problem:** Agents were saying "I'll do X" without making tool calls (narration instead of action)
- **Investigation:**
  1. Reviewed AgentContextBuilder.cs — system prompt lacked tool-use instructions
  2. Compared with nanobot's context.py — their system prompt explicitly instructs: "USE tools proactively", "do not just describe what you would do — do it"
  3. Verified Copilot provider formats tools correctly (API 	ools parameter) ✅
  4. Issue: System prompt said "Use tools deliberately" but didn't say "USE THEM NOW, don't narrate"
  
- **Fix:** Added explicit tool-use instructions to BuildIdentityBlock():
  `
  ### Tool Use Instructions
  - You have access to tools to accomplish tasks. USE them proactively — do not just narrate what you would do.
  - When you need information or need to perform an action, call the appropriate tool immediately rather than describing it or asking the user.
  - Always use tools when they can help. Do not just describe what you would do — actually do it.
  - State your intent briefly, then make the tool call(s). Do not predict or claim results before receiving them.
  `

**Research Findings:**
- **Surveyed frameworks:** nanobot, LangChain, CrewAI, OpenAI, Anthropic docs
- **Standard pattern:** ALL use "tool calls → execute; no tool calls + content → break" as baseline
- **ONLY nanobot** uses finalization retry for blank responses (proven in production)
- **ZERO frameworks** use keyword-based continuation detection
- **Best practice:** Tools must be mentioned in BOTH system prompt (instructions) AND API parameters (structural)

**Commit:** 8951925 — "Align agent loop to industry standard and add tool-use instructions"

**Impact:**
- Agents will now USE tools instead of narrating what they'll do
- Loop behavior matches industry standard (Anthropic, OpenAI, nanobot patterns)
- Finalization retry handles edge case of blank responses gracefully
- No breaking changes — backward compatible

**Build & Test:** ✅ All tests pass, solution builds cleanly

### 2026-04-02 — Token Deletion Investigation & Audit Logging

**Issue:** Jon's GitHub OAuth token was lost, forcing re-authentication. Investigated root cause and added comprehensive audit logging.

**Investigation Findings:**
1. **Timeline (2026-04-02):**
   - 22:59:00 PM: Extensions installed via install.ps1 (config.json updated)
   - 23:02:52 PM: OAuth flow triggered: "Go to https://github.com/login/device..."
   - 23:03:43 PM: New token saved to ~/.botnexus/tokens/copilot.json
   
2. **Root Cause Analysis:**
   - OAuth tokens stored separately in ~/.botnexus/tokens/, NOT in config.json
   - Token was either expired, corrupted, or missing
   - CopilotProvider clears expired tokens (lines 68-72) but **no logging existed**
   - No audit trail for token deletion, config writes, or authentication events
   
3. **Likely Scenario:** Token expired or was invalid. Provider cleared it and prompted re-auth. Zero visibility into what happened.

**Solution Implemented (Commit eb27c58):**

1. **Config Audit Logging (ConfigFileManager):**
   - Backup config.json to config.json.bak before every write
   - Log all config writes at INFO level: "Config file updated: {path}"
   - Log agent/provider/channel additions with context
   
2. **Token Audit Logging (FileOAuthTokenStore):**
   - Log token saves at WARNING level with expiration timestamp
   - Log token clears at WARNING level: "Clearing OAuth token for provider '{name}'"
   - Constructor updated to accept ILogger via DI
   
3. **Provider Audit Logging (CopilotProvider):**
   - Log expired token detection at WARNING level with expiry timestamp
   - Log token exchange failures with clear context about re-auth
   
4. **Install Script Safety (install.ps1):**
   - Backup config.json before modification
   - Enhanced logging for ExtensionsPath updates
   
**Build & Test:**
- ✅ Build succeeded (3 pre-existing warnings)
- ✅ All 322 unit tests passing
- ⚠️  Deployment tests failed due to pre-existing ASP.NET routing issues (unrelated)

**Impact:** Next time a token is cleared/expired, logs will show:
- Exact timestamp of token deletion
- Reason for deletion (expired, auth failure, etc.)
- Which config operations wrote to disk
- Backup files available for recovery

**Learning:** OAuth token lifecycle events are security-sensitive. Always log at WARNING level. Config overwrites should always backup first.

---

## 2026-04-04 — Phase 2 Sprint Design Review (Lead)

**Timestamp:** 2026-04-04  
**Status:** ✅ Complete  
**Requested by:** Copilot (Jon Bullen)  
**Scope:** Full design review of 12-commit sprint (streaming helper, thinking events, WebUI, gateway tests, agent config)

**Overall Grade: B+**

**Key Findings:**

1. **P0 — Session History Thread Safety:** `GatewaySession.History` (List<SessionEntry>) is mutated from GatewayHost, StreamingSessionHelper, and WebSocketHandler without synchronization. Concurrent messages for the same session will corrupt history. Needs session-level locking or concurrent collection.

2. **P0 — Subscription Callback Exceptions:** InProcessAgentHandle's agent event subscription callback has no try-catch. Unhandled exceptions close the channel silently — stream ends with no error to client.

3. **P0 — WebUI Memory Leaks:** Thinking toggle and tool call click listeners accumulate without cleanup. Dozens of duplicate listeners per message over a session.

4. **P1 — Missing Test Coverage:** AgentDescriptorValidator, FileAgentConfigurationSource, AgentConfigurationHostedService have zero tests. 400+ lines of untested production code.

5. **P1 — FileConfigurationWatcher Dispose Race:** `_disposed` flag set without synchronization relative to `ReloadAsync()` semaphore check.

**What Went Well:**
- StreamingSessionHelper extraction is architecturally clean — single shared helper for both GatewayHost and WebSocket
- Thinking events properly threaded end-to-end with intentional transience (not persisted)
- IAgentConfigurationSource is a well-designed, ISP-compliant extension point
- Gateway test growth (48→79) with behavioral tests, not implementation tests
- WebSocket protocol well-documented and consistent
- Modern .NET usage throughout (Lock, Channel<T>, IAsyncEnumerable, sealed records)
- No Product Isolation Rule violations

**Assignments:**
- Bender: Fix session history thread safety + subscription callback (P0-1, P0-2)
- Hermes: Add tests for config subsystem (P1-1)
- Fry: Fix WebUI memory leaks + accessibility (P0-3, P0-4, P1-7, P1-8)
- Farnsworth: Fix watcher race + validator completeness (P1-2, P1-3, P1-4)

**Decision Document:** `.squad/decisions/inbox/leela-phase2-design-review.md`

**Learnings:**
- Session models shared across async pipelines must enforce thread safety at the model level, not rely on callers to synchronize. This is a recurring pattern in gateway architectures.
- WebUI event listener management needs architectural discipline (event delegation or explicit lifecycle) when streaming creates many DOM updates per message.
- Configuration hot-reload with FileSystemWatcher requires careful synchronization between dispose and reload paths — the debounce timer creates a timing window.
- Test coverage for configuration/validation code is non-negotiable. These classes have complex conditional logic that unit tests catch cheaply.

## 2026-04-05T23:30:00Z — Phase 4 Wave 1 Design Review

**Status:** ✅ Complete  
**Overall Grade:** A-  
**Scope:** 12 commits — runtime hardening, config validation, multi-tenant auth  

**Key Findings:**

**Strengths:**
- Runtime hardening: recursion guard (AsyncLocal<HashSet>), race prevention (TaskCompletionSource), WebSocket reconnection caps (exponential backoff)
- Multi-tenant auth: O(1) identity map lookup, backward compatible (dev mode + legacy single key + platform config)
- Error messages: dotted-path notation (e.g., `gateway.apiKeys.tenant-a.apiKey`), deduplicated, sorted
- Thread safety: textbook async patterns — AsyncLocal scoping, TCS with RunContinuationsAsynchronously, ConcurrentDictionary

**P1 Issues (Requires Fix Next Sprint):**
1. Config endpoint filesystem probing: `?path=` parameter passes through Path.GetFullPath() with no restriction. Risk: information disclosure. Fix: restrict to config directory or require auth.
2. Config validation endpoint missing auth: GET /api/config/validate accessible without middleware. Combined with P1-1, unauthenticated filesystem probe. Fix: wire IGatewayAuthHandler or gate to admin-only.
3. Recursion guard tests skipped: marked [Fact(Skip = "Pending...")]. Implementation exists but unvalidated. Fix: enable tests or adjust assertions.

**P2 Issues (Backlog):**
- ApplyPlatformConfig property copy fragile (no sync on new PlatformConfig properties)
- WebSocket handler accumulating connection-limit logic (~100 lines); extract IConnectionRateLimiter
- No max call-chain depth limit (cycle guard exists, but not depth guard)
- Dual source-of-truth: root-level listenUrl vs gateway.listenUrl can conflict

**Verdict:** Ship it. Phase 4 Wave 1 well-executed. Config endpoint needs auth gating before production.

---

### 2026-04-02 — Incremental Build Performance Fix

**Problem:** dev-loop.ps1 triggered full rebuilds (~10s) every time even when only one file changed. Jon wanted incremental builds to speed up the inner dev loop.

**Root Cause:** 
- `Resolve-Version` in `scripts/common.ps1` generates version string from git state: `0.0.0-dev.{hash}.dirty`
- The `.dirty` suffix changes based on working tree state (`git status --porcelain`)
- Every build passes `/p:Version=$version` to MSBuild
- Version is stamped into assembly attributes → changing it forces recompilation of ALL projects
- During dev: make change → version = `abc123.dirty` → build → commit → version = `abc123` → next build sees different version → full rebuild

**Evidence:** 
- Build with version A then version B: 10-20s (full rebuild)
- Build twice with same version: 4-5s (incremental)
- Version instability between dev-loop runs prevented incremental builds

**Solution:** 
- Modified `dev-loop.ps1` to set `$env:BOTNEXUS_VERSION = "0.0.0-dev"` before calling pack.ps1
- `common.ps1` already checks this env var first (line 11), so all version calls return the same fixed value
- Version remains constant across builds → MSBuild uses incremental compilation
- CI/release can still override via environment variable to get git-based versions

**Performance Impact:** 
- Subsequent builds now ~50% faster (~5s vs ~10s)
- Scales with project count — larger repos see bigger gains
- Only affects local dev-loop; pack.ps1 standalone still uses git version by default

**Files Changed:** 
- `scripts/dev-loop.ps1`: Added env var initialization with explanatory comment
- `scripts/pack.ps1`: Added comment explaining version resolution strategy

**Commit:** 625fe65 "fix: enable incremental builds in dev-loop"

**Learning:** MSBuild incremental build cache is invalidated when ANY build property changes, including Version. For fast local dev loops, use stable version strings. Reserve dynamic versions (git hash, timestamps) for release builds.


### 2026-04-02 — Gateway Startup Crash: Invalid Route Pattern

**Issue:** Gateway crashing on startup with `RoutePatternException: A catch-all parameter can only appear as the last segment of the route template` at `Program.cs:168`.

**Root Cause:** Commit `2422b23` (Farnsworth's agent CRUD API) introduced invalid route patterns:
- `POST /api/sessions/{*key}/hide`
- `POST /api/sessions/{*key}/unhide`

ASP.NET Core routing does not allow catch-all parameters (`{*key}`) to have additional path segments after them. The `{*key}` must be the final segment.

**Solution:** Changed to single RESTful endpoint using HTTP PATCH with body payload:
- `PATCH /api/sessions/{*key}` with `{ "hidden": true/false }` in body
- More RESTful design (PATCH for partial updates vs separate POST endpoints)
- Complies with routing constraints

**Commit:** 1e02abd "fix(gateway): correct invalid route pattern for session hide/unhide endpoints"

**Learning:** When adding REST endpoints with catch-all route parameters, the catch-all MUST be the final segment. Additional actions should use query parameters, HTTP methods (GET/POST/PATCH/DELETE), or request body properties rather than additional path segments. Always test gateway startup after modifying routes.

### 2026-04-02 — Skills System Implementation

**Task:** Research, design, and implement a comprehensive skills system for BotNexus.

**Research Phase:**
- Studied nanobot (HKUDS) skills architecture — SKILL.md with YAML frontmatter, progressive loading
- Analyzed industry patterns for LLM agent skills — modular knowledge packages, declarative vs procedural
- Examined existing BotNexus SkillsLoader (simple text file reader) and AgentConfig.Skills property

**Design Decisions:**
1. **SKILL.md Format** — YAML frontmatter + markdown body (like nanobot, industry standard)
2. **Two-Tier Loading** — Global skills in ~/.botnexus/skills/, per-agent in ~/.botnexus/agents/{name}/skills/
3. **Agent Overrides Global** — Same skill name = agent version wins
4. **DisabledSkills with Wildcards** — Opt-out filtering with glob patterns (e.g., web-*)
5. **Context Integration** — Skills injected into system prompt, not executable tools
6. **Separation of Concerns** — Skills = knowledge, Tools = execution

**Implementation:**
- Created Skill model with Name, Description, Content, SourcePath, Scope, Version, AlwaysLoad
- Rewrote SkillsLoader to scan, parse YAML frontmatter, merge, and filter skills
- Added DisabledSkills to AgentConfig
- Integrated skills into AgentContextBuilder.BuildSystemPromptAsync()
- Added YamlDotNet dependency to BotNexus.Agent project
- Created API endpoints: GET /api/skills, GET /api/agents/{name}/skills
- Updated BotNexusHome to create skills directories on bootstrap
- Fixed test mocks in AgentContextBuilderTests for new constructor signature

**Testing:**
- ✅ Build clean, no warnings
- ✅ All 516 tests passing
- Created example global skill in ~/.botnexus/skills/example-skill/SKILL.md

**Deliverables:**
- Fully functional skills system ready for production
- Comprehensive decision document in .squad/decisions/inbox/leela-skills-architecture.md
- Backward compatible (empty directories = no-op)

**Next Steps for Team:**
1. Documentation — user guide, skill creation guide, best practices
2. WebUI — skills page, editor, enable/disable UI
3. Testing — unit tests for SkillsLoader, integration tests, E2E with example skill
4. Example Skills — build reference skills library (git workflow, code review, documentation)

**Commit:** df0c629 — "feat: implement skills system with global and per-agent skill loading"

---


## 2026-04-03T21:50:01Z — Multi-Turn Tool Calling Debug Session

**Status:** 🔍 Partial Fix — WebSocket routing fixed, multi-turn behavior analyzed  
**Requested by:** Jon Bullen  
**Scope:** Investigate and fix broken multi-turn tool calling for Nova agent

**Problem Reported:**
Jon reported that Nova agent says "Let me check if there's anything in the config or gateway..." and then STOPS — no tool call, no continuation. Multi-turn tool calling is the #1 priority.

**Investigation Findings:**

**Issue #1: WebSocket Agent Routing Bug** ✅ **FIXED**
- WebSocket connections with ?agent=nova were being routed to "assistant" agent
- Root cause: GatewayWebSocketHandler didn't read query parameters
- Fix: Extract gent query param and pass through routing chain
- Priority: message.agent_name > message.agent > query ?agent=
- Commit: `2c8bc05`

**Issue #2: Multi-Turn Status** 🔍 **NEEDS INVESTIGATION**
- Multi-turn IS working (agent iterates: 0, 1, 2, 3, 4...)
- BUT: Agent calls SAME tool repeatedly in infinite loop
- Tool results ARE being added to session history ✅
- Tool results ARE converted to ChatMessage with correct role/IDs ✅  
- Copilot provider serializes correctly with tool_call_id and name ✅
- **Hypothesis:** Either LLM not seeing results, or format issue with Copilot API proxy

**Enhanced Logging Added:**
- CopilotProvider logs tool counts and names at INFO level
- Request/response payloads at DEBUG level
- BotNexus.Providers.Copilot set to Debug in appsettings.Development.json

**Research Completed:**
- Analyzed Pi agent (badlogic/pi-mono) multi-turn implementation
- Documented their approach: streaming with tools, tool result serialization, cross-model message replay
- Key insight: Empty 	ools: [] required if messages reference past tool calls (Anthropic quirk)

**Next Steps:**
1. Capture actual HTTP request payload to Copilot API (DEBUG logs)
2. Verify tool results are in messages array with correct tool_call_id
3. Compare Nova (claude-opus-4.6) vs assistant (gpt-4o) behavior
4. Test if model-specific issue or general serialization problem
5. Check if empty tools array needed for Anthropic via Copilot proxy

**Files Changed:**
- src/BotNexus.Gateway/GatewayWebSocketHandler.cs — Query param routing
- src/BotNexus.Providers.Copilot/CopilotProvider.cs — Enhanced logging
- src/BotNexus.Gateway/appsettings.Development.json — Debug logging
- .squad/agents/leela/multi-turn-investigation.md — Detailed analysis

**Testing:**
- Created test scripts: 	est-nova.ps1, 	est-nova-simple.ps1
- Verified Nova agent now routes correctly via WebSocket
- Confirmed multi-turn loop behavior (infinite tool calls)

**Recommendation:**
HIGH PRIORITY — Capture HTTP payload to definitively confirm whether tool results are making it into subsequent requests or if there's a serialization/format issue specific to Copilot API or Claude models.

---

## 2026-04-04T12:00:00Z — Provider Abstraction Layer Architecture (Lead)

**Timestamp:** 2026-04-04T12:00:00Z  
**Status:** ✅ Complete (Decision Proposed)  
**Requested by:** Jon Bullen  
**Scope:** Design C#/.NET 10 provider abstraction layer inspired by pi-mono

**Context:**
Jon requested a full architecture decision for re-architecting the LLM provider layer, drawing from badlogic/pi-mono (TypeScript). The goal: a clean, provider-agnostic LLM abstraction that supports streaming, cross-provider handoffs, and dynamic provider registration.

**Work Done:**
1. Deep analysis of pi-mono's architecture: types.ts, api-registry.ts, models.ts, event-stream.ts, transform-messages.ts, and all provider implementations (Anthropic, OpenAI, Google, Mistral, Bedrock)
2. Mapped every pi-mono concept to idiomatic C# equivalents
3. Made concrete decisions on 17 design points (no options lists)

**Key Decisions:**
- **Content blocks:** `abstract record ContentBlock` with sealed leaf records (TextContent, ThinkingContent, ImageContent, ToolCallContent)
- **Messages:** `abstract record Message` hierarchy with sealed UserMessage, AssistantMessage, ToolResultMessage
- **Streaming:** `ICompletionStream` = `IAsyncEnumerable<StreamEvent>` + `Task<AssistantMessage>` (Channel internally, IAsyncEnumerable publicly)
- **Events:** `abstract record StreamEvent` with 12 nested sealed records for exhaustive pattern matching
- **Provider contract:** `IApiProvider` interface with `Api`, `Stream()`, `StreamSimple()` 
- **Registry:** `IApiProviderRegistry` backed by `ConcurrentDictionary` (thread-safe, DI-friendly)
- **Model:** `ModelInfo` record (not generic — C# doesn't benefit from TS branded strings)
- **Tool params:** `JsonElement` for JSON Schema (zero-copy, no schema library dependency)
- **Options:** `record StreamOptions` base, `record SimpleStreamOptions : StreamOptions` with Reasoning
- **Cross-provider:** Static `MessageTransformer.Transform()` — pure function matching pi-mono's approach
- **Cost:** `decimal` for financial precision
- **Cancellation:** `CancellationToken` replaces `AbortSignal`

**Decision Document:** `.squad/decisions/inbox/leela-providers-architecture.md`

## Learnings

- Pi-mono's `EventStream<T, R>` maps cleanly to `IAsyncEnumerable<T>` + `Task<R>` in C#. The Channel is an implementation detail, not the public API.
- Pi-mono's `Model<TApi>` generic parameter adds value in TypeScript for auto-complete on API-specific options. In C#, the same is achieved via provider-specific StreamOptions subclasses — the generic on Model itself is unnecessary overhead.
- Pi-mono's transform-messages.ts does three things: thinking-to-text conversion for cross-model, tool call ID normalization, and orphaned tool call cleanup. These are all pure data transformations — static method, not a service.
- Provider implementations in pi-mono all follow the exact same template: create stream, create output, start async IIFE, try/catch with error events. The C# equivalent is Task.Run with the same pattern. Don't over-abstract this — the template is simple enough to copy.

### 2026-04-05 — CodingAgent Port Planning

- Pi-mono's coding agent (`pi-coding-agent`) is a thin layer over `pi-agent-core`. The heavy lifting (loop, events, tools interface) is in AgentCore — the coding agent just wires tools + config + CLI. Same pattern should hold for BotNexus: `CodingAgent` is a factory/orchestrator, NOT a new agent engine.
- Pi-mono's tools (read, write, edit, bash) use the same `IAgentTool` interface as any extension tool. Built-in tools get no special treatment — they're just pre-registered. This validates our design where `CodingAgent.CreateAsync()` creates tool instances and adds them to `AgentOptions.InitialState.Tools`.
- Pi-mono's session management is file-based (JSONL in `.pi/sessions/`). This is the right pattern for a CLI tool — simple, debuggable, git-friendly. No SQLite, no server.
- Pi-mono's extension model loads from `.pi/extensions/` — npm packages or local modules. Our C# equivalent uses `AssemblyLoadContext` for DLL isolation, matching BotNexus's existing extension architecture (`extensions/{type}/{name}/`).
- The coding agent's dependency boundary is critical: ONLY `AgentCore` + `Providers.Core`. No references to the legacy `BotNexus.Agent`, `BotNexus.Core`, or `BotNexus.Gateway`. The coding agent is standalone — it IS the gateway for developer CLI usage.
- Pi-mono's safety hooks (beforeToolCall/afterToolCall) already exist in our `AgentCore` as `BeforeToolCallDelegate`/`AfterToolCallDelegate`. The coding agent just needs to provide implementations that validate paths and commands — no new hook infrastructure needed.


## Session: Phase 3 Port Audit Design Review (2026-04-05T09:49:50Z)

Participated in design review ceremony for Phase 3 architecture. All ADs approved (9–17):
- **AD-9** DefaultMessageConverter → Farnsworth
- **AD-10** --thinking CLI + /thinking command → Bender  
- **AD-11** ListDirectoryTool → Bender
- **AD-12** ContextFileDiscovery → Bender
- **AD-14** session metadata entries → Bender
- **AD-15** ModelRegistry utilities → Farnsworth
- **AD-17** /thinking slash command → Bender
- **AD-13** deferred (OpenRouter routing types, no provider yet)
- **AD-16** already present (maxRetryDelayMs)

**Orchestration logs:** .squad/orchestration-log/2026-04-05T09-49-50Z-{agent}.md

**Session log:** .squad/log/2026-04-05T09-49-50Z-port-audit-phase-3.md

**Boundaries:** AgentCore ↔ CodingAgent (DefaultMessageConverter), CodingAgent ↔ Session (MetadataEntry), Providers.Core (ModelRegistry utilities).

**Next:** Parallel execution tracks. Farnsworth + Bender begin implementation. Kif writes training docs. Nibbler runs consistency review.

## Learnings — Phase 3 Port Audit Retrospective (2026-04-06)

- **Docs written against planned APIs are unreliable.** Kif authored training docs in parallel with code work, using design review decisions as the source of truth for method signatures and behavior. 18 of 22 consistency issues traced directly to this: wrong `ExecuteAsync` signatures, non-existent parameter lists, incorrect algorithm descriptions. The plan is not the code. Docs must be written against final, committed code — or they will be wrong.

- **Parallel doc authoring only works for conceptual content.** Architecture overviews, glossary entries, and design rationale can be written in parallel with code. API examples, code snippets, and interface definitions cannot — they depend on the final implementation. Future sprints will stagger: conceptual docs in parallel, API docs after code lands.

- **Consistency review is valuable but positioned too late.** Nibbler's review caught all 22 issues, but only after the sprint was declared complete. The fix commit (`e7ff6d8`) represents rework. Moving the consistency check before sprint-complete would eliminate the fix-after-ship pattern.

- **The audit ceremony pipeline is proven.** Three phases, same structure each time: audit → design review → AD decisions → parallel sprint → consistency review → retro. The pipeline is repeatable and produces consistent results. 43 commits, 17 ADs, 415 tests across all three phases. This is the model for future feature work, not just audits.

- **YAGNI discipline prevents speculative code.** AD-13 (OpenRouter routing types) was correctly deferred. The team resisted the temptation to build types for a provider that doesn't exist. This saved a commit, avoided dead code, and kept the architecture honest.

- **Test growth tracks feature growth.** 43 new tests in Phase 3 (372 → 415). Tests were written alongside features, not backfilled. This pattern — enforced by the build-before-commit rule — keeps the test suite meaningful rather than ceremonial.


---

## 2026-04-05T11:52:58Z — Sprint 4 Design Review & Retrospective

**Status:** ✅ COMPLETE  
**Timestamp:** 2026-04-05T11:52:58Z  
**Orchestration Logs:** 
- .squad/orchestration-log/2026-04-05T11-52-58Z-leela.md (Design Review)
- .squad/orchestration-log/2026-04-05T11-52-58Z-leela-retro.md (Retrospective)

**Your Deliverables (Leela — Design Review Lead + Retrospective):**

1. **Design Review (Phase 4 Port Audit):**
   - Validated 7 P0 findings (zero false positives)
   - Created comprehensive architecture decision document
   - Documented all findings in indexed table with source references
   - Assigned ownership to all P0-P1 decisions

2. **Sprint Retrospective:**
   - Sprint 4 complete: 7 P0s + 25 P1s triaged
   - 38 total commits, zero merge conflicts
   - 16 new tests added, 100% pass rate
   - Process wins: pre-commit validation, design review flow, cross-agent communication
   - Lessons: multi-file refactors need explicit routing; JSON linting would catch standardization issues early

3. **Routing & Orchestration:**
   - Bender: 11 decisions (AgentCore + CodingAgent)
   - Farnsworth: 8 decisions (Providers)

**Decision Inbox:** All decisions merged to decisions.md and inbox deleted

**Build Status:** ✅ All P0/P1 implementations passing

**Next Phase:** E2E testing and merge validation.

## Learnings — Port Audit Remediation Retrospective (2026-07-15)

- **Design review gates catch audit false positives.** 3 of 17 audit findings were wrong — an 18% false-positive rate. All three were caught at design review before any code was written. The gate saved three agents from building unnecessary fixes. Never skip the design review even when audit confidence seems high.

- **Surface-level pattern matching causes false audit findings.** All three false findings came from the same root cause: checking method names, class names, or single code paths without following the full execution path. ShellTool's `Kill(entireProcessTree: true)` was missed because only the method name was grepped. Compaction's token-based primary path was missed because the auditor stopped at the count-based fallback. GlobTool's `"find"` registration was missed because the auditor compared class names. Future audits must require full call-chain evidence.

- **Parallel fan-out at 4 agents with zero conflicts is repeatable.** Bender (P0 safety), Farnsworth (P1 alignment), Hermes (tests), Kif (docs) ran in parallel with zero merge conflicts and zero rework. The key: scoping assigns non-overlapping file sets per agent. This is now proven across multiple sprints.

- **P0/P1 prioritization keeps safety work from drowning in alignment work.** Separating "could crash production" (P0) from "diverges from reference" (P1) prevents scope creep. The team never confused urgency with importance.

- **Deferred backlog needs active grooming.** Six items were deferred across this sprint. Without explicit prioritization, deferred items drift indefinitely. Schedule backlog grooming at sprint planning — ripgrep adapter and schema validation are highest-value next items.

- **Audit confidence ratings would reduce false positives.** Adding a High/Medium/Low confidence column forces the auditor to self-assess. Medium/Low findings get mandatory second review before becoming work items. This is a low-cost process change with high impact on audit accuracy.

---

## 2026-04-05T13:31Z — Port Audit Remediation Sprint 2 Retrospective (Lead)

**Status:** ✅ Complete
**Timestamp:** 2026-04-05T13:31Z
**Requested by:** sytone
**Ceremony:** Retrospective

**Sprint outcome:**
Design review filtered 14 audit findings down to 6 real fixes (5 already implemented, 1 intentional improvement). Farnsworth delivered 3 fixes (tool lookup, thinking signature, toolChoice). Bender delivered 3 fixes (session persist, shell cancel, .gitignore). Hermes wrote 30 new tests. 9 failed on first run due to speculative authoring — fixed in follow-up. Final: 483 tests passing, 9 conventional commits, clean build.

**Decision:** `.squad/decisions/inbox/leela-port-audit-retro.md`

## Learnings — Port Audit Sprint 2 Retrospective (2026-04-05)

- **Speculative test writing is the test equivalent of docs-before-code.** 9 of 30 tests failed because Hermes wrote them against audit findings and design review decisions, not against actual implementations. Three specific mismatches: SessionCompactor returns SystemAgentMessage (not UserMessage), InteractiveLoop leaf count wrong, SkillsLoader .gitignore path wrong. This is the same root cause as Phase 3's doc-API mismatch. Tests that assert specific behavior (types, counts, paths) must be authored AFTER implementation is final.

- **Tests must follow code, never lead it.** Sprint sequencing must enforce: Audit → Design Review → Implementation → Tests → Docs → Consistency. The test phase should start after fix commits land, not in parallel with them. Conceptual test plans can parallel; concrete assertions cannot.

- **The speculative-parallel anti-pattern has recurred twice.** Phase 3: 18/22 doc consistency issues from docs-against-plan. This sprint: 9/30 test failures from tests-against-plan. The pattern is clear and the fix is the same: sequence artifacts that assert behavior after the behavior is committed.

- **Design review filter rate is improving.** This sprint: 57% of findings filtered (8 of 14). Previous sprint: 18% false-positive rate (3 of 17). The gate is the single most valuable ceremony in the audit pipeline.

- **5-agent parallel execution with zero merge conflicts is repeatable.** Farnsworth, Bender, Hermes, Kif, and Nibbler ran in parallel. Non-overlapping file assignment continues to prevent conflicts across four consecutive sprints now.

## Learnings — Port Audit Phase 5 Retrospective (2026-07-16)

- **The speculative-parallel anti-pattern has now recurred three times.** Phase 3: docs from design decisions (18/22 issues). Phase 4: tests from audit findings (9/30 failures). Phase 5: test from design spec (ShortHash length wrong). The pattern is: any artifact that asserts specific behavior — return types, string lengths, parameter signatures — breaks when authored from plans instead of committed code. This is now a hard rule: concrete assertions must follow committed code. No exceptions.

- **Latent defects surface when refactoring changes object lifecycles.** CompactForOverflow's list aliasing was harmless for years because callers only read the list once. Bender's per-retry restructure changed the lifecycle (clear-and-rebuild per retry), exposing the shared reference. Lesson: any refactoring that changes when objects are created, read, or destroyed should trigger a review of reference semantics at the boundary.

- **Transform and compaction methods must return defensive copies.** Returning the input reference when "no work needed" is an optimization that creates aliasing traps. The cost of a list copy is negligible compared to the debugging cost of a shared-mutation bug. Add this to code review checklist.

- **Global test registries are a parallel-execution time bomb.** Two test classes registering the same provider name worked fine in serial execution for months. The collision only appeared when a timing change widened the parallel window. Test infrastructure must either scope registries per test class or use unique names. Any global mutable state in test setup is suspect.

- **Multi-agent git commits need a coordination protocol.** File locks from testhost processes and concurrent git operations caused failures requiring manual cleanup. Agents committing independently to the same repo is unsustainable beyond 2 parallel agents. Next sprint must implement either a commit queue, worktree-per-agent, or a coordinator-commits-all pattern.

- **Design specs should distinguish assumed vs verified behavior.** The ShortHash spec stated a 9-char trim step that doesn't exist in the reference implementation. Had the spec marked this as "assumed — verify against pi-mono" instead of stating it as fact, the test would have been written differently. Spec templates need an explicit confidence/verification column.

## Learnings -- Gateway Service Architecture (2026-04-06)

### Architecture Decisions

1. **Five-project decomposition**: Gateway.Abstractions (pure interfaces) -> Gateway (runtime) -> Gateway.Api (ASP.NET Core surface), plus Gateway.Sessions and Channels.Core as leaf projects.

2. **Push-based channel dispatch over message bus**: Channel adapters call IChannelDispatcher.DispatchAsync() directly instead of a shared IMessageBus. Gateway itself implements IChannelDispatcher.

3. **IAgentHandle as the isolation boundary**: Abstracts the isolation strategy. In-process wraps AgentCore.Agent; future handles can proxy to containers or remote services.

4. **AgentCore integration**: ModelRegistry.GetModel(provider, modelId) requires both params. AgentOptions is a positional record requiring all params.

### Key File Paths

- Abstractions: src/gateway/BotNexus.Gateway.Abstractions/
- Runtime: src/gateway/BotNexus.Gateway/GatewayHost.cs
- API: src/gateway/BotNexus.Gateway.Api/
- Sessions: src/gateway/BotNexus.Gateway.Sessions/
- Channels: src/channels/BotNexus.Channels.Core/
- ADR: .squad/decisions/inbox/leela-gateway-architecture.md

## Learnings -- Gateway Design Review (2025-07-24)

### Review Findings
1. **Architecture grade: A-** — Clean contracts, correct dependency flow, genuinely pluggable extension model. One bug (streaming history loss) prevents A grade.
2. **P1-1 Bug: Streaming history loss** — GatewayHost.DispatchAsync streaming branch never appends assistant response to session.History. Non-streaming path is correct.
3. **P1-2: SetDefaultAgent DI smell** — DefaultMessageRouter has a concrete SetDefaultAgent() method not on IMessageRouter. Forces dual registration in DI.
4. **P1-3: ChannelManager duplicates GatewayHost** — Both start/stop channels. GatewayHost should be authoritative; ChannelManager should be read-only or removed.
5. **P1-4: No ISessionStore in default DI** — AddBotNexusGateway() doesn't register any session store. Runtime failure with no guidance.
6. **P1-5: Test file naming mismatch** — IsolationStrategyTests tests ActivityBroadcaster, WebSocketProtocolTests tests MessageRouter, ChannelAdapterTests tests SessionsController.
7. **Test coverage gaps** — No tests for InProcessIsolationStrategy, GatewayWebSocketHandler, ChannelAdapterBase, FileSessionStore, DefaultAgentCommunicator.

### Key File Paths
- Review: .squad/decisions/inbox/leela-gateway-design-review.md

## Learnings — P1 Sprint Design Review (2026-04-05)

### Review Findings
1. **Overall grade: A-** — All six prior P1 issues correctly remediated. One P0 (WebSocket streaming history loss) prevents A grade.
2. **P0: WebSocket handler missing streaming history** — GatewayWebSocketHandler.HandleUserMessageAsync streams events to client but never captures tool events or assistant content in session.History. Same bug as P1-1, missed in this code path. Needs StreamToHistoryAsync helper extraction.
3. **P1: Channel stubs bypass ChannelAdapterBase** — TUI and Telegram adapters implement IChannelAdapter directly, missing allow-list enforcement and lifecycle logging from the base class.
4. **P1: TelegramOptions doesn't use Options pattern** — Registered as raw singleton, inconsistent with GatewayOptions IOptionsMonitor pattern.
5. **P1: ChannelManager concrete type in DI** — No interface extracted; soft DIP violation.
6. **Prior P1-1 through P1-5 all verified fixed** — Streaming history, Options pattern, ChannelManager consolidation, default session store, test naming all confirmed correct.

### Key Patterns Observed
- **Streaming history accumulation pattern** in GatewayHost (streamedContent + streamedHistory lists) is correct and should be reused in all streaming code paths.
- **IOptionsMonitor for hot-reloadable config** is the right choice for GatewayOptions.DefaultAgentId.
- **TryAddSingleton for default implementations** correctly allows consumer override.
- **Channel stubs as IChannelAdapter direct implementors** is acceptable for Phase 2 but should migrate to ChannelAdapterBase for production.

### Key File Paths
- Review: .squad/decisions/inbox/leela-design-review-p1-sprint.md
## Learnings — Phase 3 Sprint Design Review (2026-04-07)

### Review Findings
1. **Overall grade: B+** — Strong sprint delivering thread safety, isolation stubs, cross-agent calling, steering/follow-up, and platform config. One P0 (path traversal) prevents A grade.
2. **P0: Path traversal in SystemPromptFile** — FileAgentConfigurationSource.cs:110 resolves systemPromptFile relative to configDirectory without bounds checking. Could read arbitrary files via `../../../etc/passwd`.
3. **P1: No cross-agent recursion guard** — DefaultAgentCommunicator allows infinite A→B→A cycles via nested session IDs.
4. **P1: Redundant GetInstance/GetOrCreateAsync pattern** — ChatController and WebSocketHandler do two lookups where one suffices, with a TOCTOU race between them.
5. **P1: WebUI event delegation incomplete** — Session/agent lists still use per-element listeners despite delegation being added for chat messages.
6. **Thread safety implementation verified correct** — Lock usage, no async in locks, defensive copy, all production callers migrated. Zero remaining .History.Add/.History.AddRange in src/.

### Key Patterns Observed
- **Isolation strategy multicast DI pattern** (AddSingleton<IIsolationStrategy, TImpl> ×4) is clean and extensible. Stubs consistently throw NotSupportedException.
- **Cross-agent session scoping** (parent::sub::child, cross::source::target::guid) provides good audit trails but needs lifecycle cleanup.
- **PlatformConfigLoader validation-then-fail-fast** in DI setup is the correct pattern for config errors.
- **IAgentHandle.SteerAsync/FollowUpAsync** cleanly extends the existing interface without breaking changes. InProcessAgentHandle delegates directly to AgentCore methods.

### Key File Paths
- Review: .squad/decisions/inbox/leela-design-review-phase3.md

## Learnings — Phase 5 Design Review (2026-04-09)

### Review Findings
1. **Overall grade: A−** — Strongest delivery yet. All 6 requirements complete. Auth middleware, WebSocket channel pipeline, session lifecycle, workspace model, CLI, and API surface all architecturally sound. Three P1 findings prevent full A.
2. **P1: StreamAsync background task leak** — InProcessAgentHandle.StreamAsync fires PromptAsync on Task.Run; if consumer cancels IAsyncEnumerable, the background task continues. Need linked cancellation token.
3. **P1: SessionCleanupService full-scan** — ListAsync loads ALL sessions on every cleanup cycle. FileSessionStore reads every .meta.json. Won't scale. Need ListByStatusAsync or similar push-down filtering.
4. **P1: Path.HasExtension auth bypass** — GatewayAuthMiddleware.ShouldSkipAuth uses Path.HasExtension to skip auth for "static files", but this also matches API paths like /api/agents.json.
5. **P2: Agent name path traversal** — FileAgentWorkspaceManager.GetWorkspacePath doesn't validate agent name characters. Same pattern flagged for SystemPromptFile in Phase 3.
6. **SOLID score: 4.5/5** — Only deduction: GatewayWebSocketHandler takes concrete WebSocketChannelAdapter (not interface) for WebSocket-specific methods. Pragmatic but impure DIP.
7. **Cross-agent recursion guard resolved** — DefaultAgentCommunicator now uses AsyncLocal<HashSet<string>> call chain tracking with IDisposable scope cleanup. Addresses Phase 3 P1.
8. **Supervisor coalescing pattern** — DefaultAgentSupervisor uses TaskCompletionSource + _pendingCreates to prevent duplicate agent creation races. Production-grade.

### Key Patterns Observed
- **WebSocket channel pipeline integration:** WebSocketChannelAdapter extends ChannelAdapterBase, implements IStreamEventChannelAdapter, and dispatches inbound through GatewayHost.DispatchAsync. Handler registers/unregisters connections on adapter.
- **Channel capability flags as virtual bool properties** on ChannelAdapterBase (default false) — OCP-compliant. New capabilities don't break existing adapters.
- **IStreamEventChannelAdapter as optional interface** — channels that can't render structured events don't implement it. GatewayHost checks via pattern matching.
- **Auth middleware 3-layer agent extraction** — query string → route values → request body (with EnableBuffering). Multi-tenant key support with per-key allow-lists.
- **PlatformConfigWatcher debounce pattern** — FileSystemWatcher + Timer with 500ms debounce prevents rapid-fire reloads. Correct disposal under Lock.
- **Agent workspace convention** — SOUL.md, IDENTITY.md, USER.md, MEMORY.md auto-scaffolded by BotNexusHome.GetAgentDirectory. WorkspaceContextBuilder composes sections with separator.

### Key File Paths
- Review: .squad/decisions/inbox/leela-phase5-design-review.md

## 2026-04-04T09:00:00Z — Phase 6 Design Review (Lead)

**Timestamp:** 2026-04-04T09:00:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen  
**Scope:** Architecture review of Gateway Phase 6 — completion sprint (cross-agent calling, WebUI, dev scripts, integration tests, docs)

**Reviewed Changes:**
1. Cross-agent calling (Bender, commit 2da5dbf) — DefaultAgentCommunicator.CallCrossAgentAsync with registry → supervisor → isolation strategy pipeline, AsyncLocal recursion detection
2. WebUI enhancement (Fry, commit 465f64f) — 1710-line production dashboard with session mgmt, agent selection, thinking/tool display, steering/follow-up, activity feed, responsive design
3. Dev loop scripts (Farnsworth, commit 974d91c) — SkipBuild/SkipTests params, config validation E2E
4. Integration tests (Hermes, commit 9c3bfd3) — 14 new tests: cross-agent calling, live gateway integration, WebSocket connection
5. Documentation (Kif, commit 61852d1) — Dev guide, architecture doc, API reference

**Verification:** Build 0 errors, 0 warnings | Tests 225 passed, 0 failed

**Key Findings:**
1. **Overall grade: A** — Most cohesive delivery yet. Five parallel workstreams converge cleanly. No P0 issues. Cross-agent recursion guard resolves Phase 3 P1.
2. **P1: No configurable max call chain depth** — Recursion guard detects cycles but not depth. Acyclic chain of 50 agents proceeds without limit.
3. **P1: Dev guide missing SkipBuild/SkipTests documentation** — Script tables don't document new parameters.
4. **P1: Cross-agent call has no default timeout** — handle.PromptAsync blocks indefinitely if target hangs.
5. **P2: WebUI app.js single 1710-line file** — approaching module-splitting threshold.
6. **P2: escapeHtml DOM creation per call** — suboptimal during streaming; regex replacer preferred.
7. **P2: API reference base URL shows port 18790 not 5005** — pre-existing doc drift.
8. **SOLID score: 4.5/5** — Same DIP deduction as Phase 5 (GatewayWebSocketHandler concrete dependency). No new violations.

### Learnings
- **AsyncLocal + IDisposable scope** is the correct pattern for tracking async call chains in .NET. The `EnterCallChain`/`CallChainScope` implementation handles concurrent calls, async continuations, and exception cleanup correctly.
- **Session scoping conventions matter** — Sub-agent `{parent}::sub::{child}` (deterministic, reusable) vs cross-agent `{source}::cross::{target}::{GUID}` (unique, isolated) are intentionally different. Both are correct for their use case.
- **Channel capability flags as virtual bool properties** (default false) is textbook OCP. New capabilities never break existing adapters. Pattern confirmed across TUI, Telegram, and WebSocket adapters.
- **DOMPurify + marked** for WebUI markdown rendering provides proper XSS protection. The sanitization pipeline: `marked.parse(text)` → `DOMPurify.sanitize(html)`.
- **Exponential backoff reconnection** in WebUI with `RECONNECT_MAX_ATTEMPTS=10` and `RECONNECT_MAX_MS=30000` is production-grade.
- **Integration tests with WebApplicationFactory** — `LiveGatewayIntegrationTests` demonstrates how to test the full HTTP+WebSocket stack with mock services injected via `ConfigureTestServices`.

### Key File Paths
- Review: .squad/decisions/inbox/leela-phase6-design-review.md
- Cross-agent communicator: src/gateway/BotNexus.Gateway/Agents/DefaultAgentCommunicator.cs
- Cross-agent tests: tests/BotNexus.Gateway.Tests/CrossAgentCallingTests.cs
- Live integration tests: tests/BotNexus.Gateway.Tests/Integration/LiveGatewayIntegrationTests.cs
- WebUI: src/BotNexus.WebUI/wwwroot/app.js

## 2026-04-06T04:00:00Z — Full Gateway Design Review (Lead)

**Timestamp:** 2026-04-06T04:00:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Comprehensive design review of entire Gateway service after 7+ phases

**Context:**
Full architecture review against all 6 refined requirements. Reviewed: Abstractions (23 files), Core (27 files), API (10 files), Sessions (2 stores), Channels (4 adapters). Build green, 276 tests passing.

**Grade: A-**

| Area | Score |
|------|-------|
| SOLID Compliance | 24/25 |
| Requirement Coverage | 5.5/6 complete |
| Extension Model | Exemplary |
| Thread Safety | Production-grade |

**Key Findings:**
- P0: None. Previous Path.HasExtension auth bypass is fixed (ShouldSkipAuth now uses StartsWithSegments).
- P1 (6 items): GatewaySession SRP (replay buffer), GatewayWebSocketHandler size (458 lines, 5 responsibilities), HttpClient singleton (should use IHttpClientFactory), StreamAsync task leak (carried), WebSocket payload size validation, SessionHistoryResponse location.
- P2 (6 items): No correlation IDs, session store indexing, imperative config validation, no REST rate limiting, channel allow-list model, Swagger auth ordering.
- Top 3 recommendations: Extract SessionReplayBuffer, adopt IHttpClientFactory, decompose GatewayWebSocketHandler.

**Review written to:** `.squad/sessions/2026-04-06T04-design-review.md`

## Learnings — Full Gateway Design Review (2026-04-06)

1. **Path.HasExtension bypass is resolved** — ShouldSkipAuth now uses StartsWithSegments for /health, /webui, /swagger. No more file-extension-based auth skipping. This was a P0 from Phase 5 that's been properly fixed.
2. **GatewaySession dual-lock pattern is correct but signals SRP** — Separate _historyLock and _streamReplayLock prevent contention between history and replay operations, but the need for two locks in one class is a design smell.
3. **Bounded channel with drop-oldest for activity broadcasting** — InMemoryActivityBroadcaster uses 500-item bounded channels per subscriber. Slow consumers don't block the gateway. This is the correct pub/sub pattern for real-time monitoring.
4. **WebSocket rate limiting uses exponential backoff with stale entry cleanup** — Connection attempts tracked in ConcurrentDictionary, cleaned every 128 updates. Practical approach that avoids timers.
5. **FileSessionStore uses JSONL + metadata sidecar pattern** — History as append-only JSONL, metadata as separate JSON. Good separation — history can grow without rewriting metadata on every save.
6. **276 gateway tests confirms test coverage is comprehensive** — Up from 225 (Phase 6) → 264 (Sprint 7A) → 276 (current). Test growth tracks feature delivery.

---

## 2026-04-06T05:00:00Z — Phase 9 Requirements Gap Analysis (Lead)

**Timestamp:** 2026-04-06T05:00:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Full requirements validation against 6 gateway requirements + architecture constraints

**Context:**
Validated current codebase against comprehensive requirements brief covering Agent Management, Isolation Strategies, Channel Adapters, Session Management, API Surface, and Platform Configuration. Build: 0 errors/31 CS1591 warnings. Tests: 811 total (276 Gateway), 0 failures.

**Scores:** Agent Mgmt 100%, Isolation 35%, Channels 70%, Sessions 95%, API 85%, Config 65%.

**Key Findings:**
- Agent management fully complete — registration, lifecycle, sub/cross-agent calling, recursion guards, depth limits all implemented and tested
- Biggest gap: Dynamic extension loading (user directive 2a) — channels and providers are hardcoded DI, not loaded from extensions/ folders
- CORS missing — blocks WebUI cross-origin requests
- CLI has only `validate` command — no `init`, no agent management
- JSON Schema references point to non-existent URLs
- Telegram is a stub with no Bot API integration
- 3 frozen code proposals filed: IHttpClientFactory (low risk), provider conformance tests (no risk), StreamAsync leak (defer)

**Sprint Plan:** 9A (dynamic loading + quick wins), 9B (CLI + config), 9C (Telegram + frozen code)

**Decision written to:** `.squad/decisions/inbox/leela-phase9-gap-analysis.md`

## Learnings — Phase 9 Gap Analysis (2026-04-06)

1. **Dynamic extension loading is the #1 architectural gap** — User directive 2a requires config-driven assembly loading. The extension folder structure exists (~/.botnexus/extensions/{channels,providers,tools}/) but no code scans or loads assemblies from it. This blocks the "nothing loads unless configured" design principle.
2. **Agent management is the strongest subsystem** — IAgentRegistry, IAgentSupervisor, IAgentCommunicator form a complete, well-tested agent lifecycle. Cross-agent calling has recursion guards, depth limits (10), and timeouts (120s). No gaps found.
3. **Session management is near-complete** — Only gap is multi-tenant isolation (no TenantId scoping). The reconnection protocol with sequence-based replay, suspend/resume, paginated history, and cleanup service all work correctly.
4. **CORS is a silent blocker for WebUI** — No AddCors/UseCors in Program.cs. If WebUI is served from a different origin (common in dev), all browser requests will fail. Quick fix but needs to be configurable.
5. **CLI has minimal parity with API** — Only `validate` command exists. No `init`, `agent list/add/remove`, `config set/get`. This creates friction for users who prefer CLI over editing JSON files.
6. **811 total tests across 8 projects** — 76 test files, strong Gateway coverage (41 files). xUnit + Moq + FluentAssertions + ASP.NET Mvc.Testing. Test infrastructure is mature.
7. **Active P1 carries are well-scoped** — extract-replay-buffer, IHttpClientFactory, decompose-ws-handler, provider-conformance-tests, CLI-parity. All have clear owners and bounded effort.

## 2026-04-06 — Phase 9 Design Review (Lead)

**Timestamp:** 2026-04-06  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Architectural review of all Phase 9 implementations (8 commits)

**Context:**
Phase 9 delivered 8 commits across 6 agents: IHttpClientFactory migration, WebUI processing status bar, dev-loop docs overhaul, SessionReplayBuffer extraction, configurable CORS, agent descriptor update endpoint, and provider conformance test suite. Build green, 811+ tests passing.

**Grade: A-**

| Area | Grade |
|------|-------|
| SOLID Compliance | 4.8/5 |
| Architecture Alignment | A |
| API Design | A- |
| Security | A- |
| Test Quality | A |

**Key Findings:**
- P0: None.
- P1: HttpClient singleton bridge partially defeats IHttpClientFactory benefits. PUT /api/agents/{agentId} silently reconciles mismatched AgentIds (should 400). CORS AllowAnyMethod in production is too permissive. Copilot conformance tests duplicate OpenAI without explanation.
- Carried resolved: Path.HasExtension auth bypass ✅, SessionReplayBuffer extraction ✅.
- Carried forward: StreamAsync task leak, SessionHistoryResponse location.

**Decision written to:** `.squad/decisions/inbox/leela-phase9-design-review.md`

## Learnings — Phase 9 Design Review (2026-04-06)

1. **Default interface methods are a good OCP pattern** — `IAgentRegistry.Update` uses a default implementation that throws `NotSupportedException`. This extends the interface without breaking existing implementations. Prefer this over separate interfaces when the new method is logically part of the same contract.
2. **Singleton bridge over IHttpClientFactory is transitional debt** — Registering `AddSingleton<HttpClient>(sp => factory.CreateClient(...))` keeps backward compatibility but should not be the final state. The proper end-state is injecting `IHttpClientFactory` at call sites.
3. **Provider conformance tests validate substitutability** — The abstract base + TheoryData + per-provider subclass pattern is an effective way to enforce LSP across provider implementations. This pattern should be replicated for channel adapters.
4. **SRP extraction with façade preservation is the right refactoring pattern** — SessionReplayBuffer was extracted while keeping GatewaySession's public API intact. This avoids cascading changes in callers. The façade methods can be deprecated later when direct buffer access is adopted.
5. **CORS middleware order matters** — `UseCors` must come before auth middleware so OPTIONS preflight requests are handled without credentials. The Phase 9 implementation gets this right.
6. **Silent input reconciliation hides bugs** — The PUT endpoint quietly overrides mismatched AgentIds. Prefer strict validation (return 400) over silent fixup — callers should know when their request is malformed.

## 2026-04-06T05:46:00Z — Phase 10 Design Review (Lead)

**Timestamp:** 2026-04-06T05:46:00Z  
**Status:** ✅ Complete  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Architectural review of Phase 10 (6 commits, 3 agents)

**Context:**
Phase 10 delivered 6 commits across 3 agents: PUT AgentId validation fix, CORS verb restriction, WebSocket handler SRP decomposition, CLI parity commands (init, agent, config), and deployment validation test harness.

**Grade: A-**

| Area | Grade |
|------|-------|
| SOLID Compliance | A |
| Architecture Alignment | A |
| API Design | A |
| Security | A- |
| Test Quality | A- |

**Key Findings:**
- P0: None.
- P1: CLI Program.cs is 850+ lines of monolithic top-level statements — needs decomposition into command handler classes. CLI config get/set reflection has no test coverage.
- P2: SequenceAndPersistPayloadAsync double-serialization persists (carried). Dispatcher takes concrete WebSocketConnectionManager. CORS missing PATCH. Deployment test env var locking scope limited.
- Phase 9 P1s resolved: AgentId validation ✅, CORS verb restriction ✅, WebSocket handler decomposition ✅.
- Carried forward: StreamAsync task leak, SessionHistoryResponse location, Copilot conformance test duplication.

**Decision written to:** `.squad/decisions/inbox/leela-phase10-design-review.md`

## Learnings — Phase 10 Design Review (2026-04-06)

1. **WebSocket handler decomposition is textbook SRP** — Splitting orchestration (handler), admission (connection manager), and routing (dispatcher) into separate classes with preserved endpoint contracts demonstrates the right way to decompose a God class. The key: keep the orchestrator thin, let extracted classes own their state.
2. **Top-level statements don't scale past ~200 lines** — System.CommandLine's compositional model invites putting everything in Program.cs. Beyond a few commands, extract handler classes. The CLI needs the same treatment the WebSocket handler just received.
3. **Save-then-reload-and-validate is a good CLI pattern** — The CLI writes config, reloads it, and validates. This catches serialization/deserialization drift and ensures the file is well-formed. Worth replicating in other config-writing tools.
4. **Deployment tests with isolated BOTNEXUS_HOME are effective** — Using `WebApplicationFactory<Program>` + temp root + env var override gives realistic integration testing without touching real user state. The SemaphoreSlim lock prevents parallel test interference.
5. **Reflection-based config traversal needs tests** — dotted-path property lookup via reflection is inherently fragile. Any rename or structural change in PlatformConfig silently breaks CLI config get/set. This must be test-covered.
