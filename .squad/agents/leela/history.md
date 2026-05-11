# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET. Lean core with extension points for assembly-based plugins. SOLID patterns with vigilance against over-abstraction.
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels, Command, Cron, Gateway, Heartbeat, Providers, Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-12 Complete. Full Design Review Complete.** Build green, 337+ tests passing. Leela leads architecture: system boundaries, code review, extension points.

**Key Architectural Decisions:**
- Conversation project extraction: 3 stores + router → `BotNexus.Gateway.Conversations` (Domain ← Contracts ← Conversations ← Gateway)
- Gateway decoupling: `IAgentToolContributor` runtime contract, removed compile-time extension references, dependency direction restored
- Conversation routing Phase 1+2: Root cause (Hub always passes null conversationId), Phase 1 hotfix + Phase 2 dispatch layer extraction
- Dispatch layer: `IConversationDispatcher` single-method orchestration interface as seam between transport and resolution
- Sub-agent completion wake: Always use `DispatchAsync` (not `IsRunning` branch), eliminates race window
- Gateway detached process: `IGatewayProcessManager` in CLI, PID file at `~/.botnexus/gateway.pid`, Windows-only v1
- Tool timeout: Metadata-bag approach for timeout wiring (pragmatic escape valve)
- OpenClaw Memory Wave 1: Strict rejection protocol (Bender locked out, Farnsworth remediated), pure delegation pattern

**Active Stream:** OpenClaw memory alignment, extension commands, gateway lifecycle, provider filtering.

## Learnings

- When a hub method has an optional parameter forwarded to routing, always verify it's actually passed — defaults silently mask bugs.
- Single-method orchestration interface (`IConversationDispatcher`) is the right seam for preventing dual-routing bugs without over-abstracting.
- When a hub retains a dependency solely for lifecycle cleanup (disconnect muting), document why it's acceptable.
- Back-compat paths during incremental migration are acceptable but need tracking items for removal.
- `InternalChannelAdapter` lacks `IStreamEventChannelAdapter` — race window between follow-up drain and status transition. Correct wake: `DispatchAsync`.
- CronTrigger creates ephemeral sessions; only DispatchAsync wakes existing sessions.
- Strict rejection protocol: rejected work delegates to fresh implementer, prevents scope drift.
- Metadata-bag approach minimizes descriptor contract surface; future options may justify first-class fields.
- PID recycling mitigated by process name check; hard kill acceptable for console apps on Windows.

## Learnings

- 2026-07-29: Team reskill pass reduced cold-loaded context from ~197KB to ~59KB (70%). Charter trimming: remove Collaboration, Voice, verbose Model, boilerplate Boundaries. History summarization: distill session entries into Core Context + high-signal Learnings only.
