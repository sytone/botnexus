# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-7A Complete. Full Design Review Complete. Phase 12 Extension-Commands Design Review Complete (Grade: B+).** Build green (0 errors), 276 tests passing (up from 264), Full Review grade A-. Core systems operational:
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

## 2026-07-28 — Gateway Detached Process Design Review (Lead)

**Status:** ✅ Complete  
**Session:** Design Review ceremony for `improvement-gateway-detached-process`

**Your Role:** Lead/Architect. Design review facilitation, architectural decisions, wave breakdown.

**Codebase Findings:**
- CLI lives in `src/gateway/BotNexus.Cli/` using System.CommandLine + Spectre.Console
- Current `ServeCommand.cs` blocks via `Process.WaitForExitAsync()` — root cause of UX issue
- Health endpoint already exists: `GET /health` → `{"status":"ok"}` (no auth required)
- `BotNexusHome` class manages `~/.botnexus/` structure including `logs/` directory
- Serilog file sinks already configured in `Program.cs`

**Architectural Decisions:**
1. `IGatewayProcessManager` interface lives in CLI project (process management is CLI concern)
2. PID file is plain text at `~/.botnexus/gateway.pid` (single integer, no JSON)
3. Detached mode is default; `--attached` flag for foreground debugging
4. Windows-only for v1 (`OperatingSystem.IsWindows()` guard)
5. Hard kill for stop (console apps don't respond to `CloseMainWindow()`)
6. 10-second health check timeout with exponential backoff
7. Automatic stale PID cleanup on any PID file read

**Wave Breakdown:**
- Wave 1 (Bender): Core process manager + health checker — 2h
- Wave 2 (Farnsworth): CLI command refactor + DI registration — 1.5h
- Wave 3 (Hermes): Unit + integration tests (18-24 tests) — 2h (parallel with Wave 2)
- Wave 4 (Kif): Documentation + spec archive — 1h

**Key Risks Identified:**
- Cross-platform spawning differs on Linux/macOS → Windows-only v1
- Console apps on Windows don't respond to graceful shutdown signals → hard kill acceptable
- PID recycling mitigated by process name check

**Deliverables:**
- Complete design review document with interface contracts
- Decision file written to `.squad/decisions/inbox/leela-gateway-detached-process.md`
- 4-wave implementation plan with agent assignments and parallelization notes


## Learnings — Sub-Agent Completion Wake Bug (2026-07-22)

1. **InternalChannelAdapter lacks IStreamEventChannelAdapter** — `InternalChannelAdapter.cs` has `SupportsStreaming => true` but doesn't implement `IStreamEventChannelAdapter`. GatewayHost's streaming callback only forwards ContentDelta via `SendStreamDeltaAsync` (which sends `ContentDeltaPayload`), dropping MessageStart/MessageEnd/ToolStart/ToolEnd. The Blazor client expects `AgentStreamEvent` — type mismatch causes silent data loss.
2. **Race window between follow-up drain and status transition** — Between `AgentLoopRunner:224` (last follow-up drain) and `Agent.RunAsync:535` (status → Idle), IsRunning returns true but the loop won't drain again. FollowUpAsync enqueued during this window is stranded until next user message.
3. **DispatchAsync is the correct wake mechanism** — `GatewayHost.DispatchAsync` queues messages for serial processing via session-keyed channels. The session queue worker processes them through `ProcessInboundMessageAsync` which handles routing, agent execution, and session persistence. This is the same path user messages take.
4. **InternalChannelAdapter resolves session's original channel** — Uses `ResolveTargetAdapterAsync` to look up the session's ChannelType and delegate delivery to the actual adapter (SignalR, Telegram, etc.). Falls back to SignalR if session lookup fails. Design is correct; just missing the IStreamEventChannelAdapter interface.
5. **CronTrigger creates new sessions, not waking existing ones** — Despite the spec suggesting cron "wakes idle sessions," CronTrigger actually creates new ephemeral sessions per run. The only mechanism for waking existing sessions is DispatchAsync.
6. **Key file paths for sub-agent completion flow:** `DefaultSubAgentManager.OnCompletedAsync:218`, `InternalChannelAdapter.cs`, `GatewayHost.ProcessInboundMessageAsync:199`, `AgentLoopRunner.RunLoopAsync:224`, `Agent.RunAsync:420`.
7. **Decision: Always use DispatchAsync for completions** — Removing the IsRunning branch simplifies reasoning, eliminates the race, and produces cleaner UX (separate completion turns). Filed to `.squad/decisions/inbox/leela-subagent-completion-wakeup.md`.

## 2026-05-04 — Gateway Decoupling Implementation Signal from Farnsworth

**From:** Farnsworth (Platform Dev)  
**Re:** Your HIGH-priority recommendation #1 (Decouple Gateway from Extensions)  
**Status:** ✅ Implemented

Farnsworth has implemented your recommendation to decouple Gateway from compile-time extension references.

**Implementation Pattern:**
- Introduced IAgentToolContributor runtime contract in Gateway.Contracts
- Extension assemblies self-register tools via AssemblyLoadContextExtensionLoader
- Removed 4 compile-time <ProjectReference> entries (Skills, Mcp, McpInvoke, WebTools)
- InProcessIsolationStrategy now calls contributors per agent/session
- InProcessAgentHandle manages lifecycle cleanup

**Consequence:** Dependency direction restored (Extensions → Gateway.Contracts). Gateway no longer pulls extension implementations.

**What This Enables:** Your remaining recommendations (Agent.Abstractions extraction, Copilot OAuth decoupling) are now better positioned to succeed. Build is green, no new test failures.

---

## 2026-05-07 — Conversation Routing Architecture Design Review (Lead)

**Status:** ✅ Complete — decision approved, ready for implementation  
**Session:** Design review for conversation routing bug + architectural decoupling

**Your Role:** Lead/Architect. Root cause analysis, architectural decision, wave breakdown.

**Bug Root Cause:**
`GatewayHub.ResolveOrCreateSessionAsync` (line 582-632) always calls `IConversationRouter.ResolveInboundAsync` with `conversationId: null`, regardless of what the client sends. This causes:
1. Hub resolves to default conversation's session (Session A) and subscribes client to `session:A` group
2. GatewayHost re-routes using actual `conversationId` → processes on Session B
3. Response goes to `session:B` group — client isn't listening → appears as "only responds on default"

**Architectural Issues Identified:**
1. **Dual routing**: Both GatewayHub and GatewayHost independently call `IConversationRouter` — two owners for one concern
2. **Session pre-resolution**: Hub resolves session before dispatch; GatewayHost may redirect to different session
3. **SignalR group subscription timing**: Client subscribes before final session is known

**Decisions Made:**
1. **Phase 1 (Bender, 1h):** Pass `conversationId` through `GatewayHub.ResolveOrCreateSessionAsync` — fixes the immediate bug
2. **Phase 2 (Farnsworth, 3h):** Remove `IConversationRouter` dependency from GatewayHub entirely. Add `DispatchResult` return type to `IChannelDispatcher.DispatchAsync`. Hub becomes a pure event relay.
3. **Phase 3:** No changes needed for Telegram/TUI — already correctly dispatch via `ChannelAdapterBase`

**Key File Paths:**
- Bug location: `src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs:582-594`
- Correct routing: `src/gateway/BotNexus.Gateway/GatewayHost.cs:261-280`
- Conversation router: `src/gateway/BotNexus.Gateway.Conversations/DefaultConversationRouter.cs:32-63`
- Client send: `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Services/AgentInteractionService.cs:36-73`
- SignalR response delivery: `src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRChannelAdapter.cs:43-109`

**Learnings:**
- Conversation project extraction (previous decision) was completed — `BotNexus.Gateway.Conversations` exists with stores + router
- The `BotNexus.Gateway.Conversations` project exists and has proper contracts in `Gateway.Contracts/Conversations/`
- When explicit `conversationId` is used in `DefaultConversationRouter`, the `OriginatingBinding` is null (direct path doesn't resolve bindings)
- User has 5 conversations for quill agent in SQLite store (`sessions.db`)
- `GatewayEventHandler` on client side uses `ResolveConversationId` which falls back to `ActiveConversationId` — this masks the bug since events from the wrong session can't be mapped

---
