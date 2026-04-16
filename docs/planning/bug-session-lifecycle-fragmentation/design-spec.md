---
id: bug-session-lifecycle-fragmentation
title: "Session Lifecycle Fragmentation — 7 Paths, No Single Truth"
type: bug
priority: high
status: ready
created: 2026-04-15
updated: 2026-04-15
author: nova
depends_on: []
tags: [gateway, session, refactor, architecture, subagent, lifecycle]
ddd_types: [Session, SessionId, SessionType, SessionStatus, AgentId, GatewaySession]
---

# Session Lifecycle Fragmentation

## Summary

Every conversation type (user-agent, cron, sub-agent, agent-agent, cross-world, soul, REST chat) manages its own session lifecycle through separate code paths. These paths duplicate — or skip — critical steps like session store creation, history recording, session type assignment, participant tracking, status management, compaction, and telemetry. The result: **sub-agent sessions are never persisted**, the WebUI can't display them, and any future lifecycle feature (audit trail, billing, analytics) must be bolted onto 7+ separate paths.

## Architectural Alignment

The domain model (`docs/architecture/domain-model.md`) is unambiguous:

> **Session** — "A bounded window of interaction. It is the container for a conversation, a task, a piece of focused work, or an internal process. Every time an agent does anything — responds to a user, runs a cron job, reflects in its Soul Session, talks to another agent — it happens within a Session."

> **Existence** — "The totality of an agent's experience — the collection of every Session it has ever participated in."

> **Agent-Sub-Agent** — "Session type: Agent-Sub-Agent. Participants: Parent Agent + Worker. The worker's session becomes part of the parent agent's Existence."

The architecture says **every interaction creates a Session, and every Session contributes to the agent's Existence**. But the implementation has sub-agent and communicator paths that bypass the session store entirely — these interactions are invisible to the agent's Existence, can't be inspected in the WebUI, and are lost on restart.

The system-flows doc (`docs/architecture/system-flows.md`) defines a **Session Lifecycle** with clear states (Active → Suspended → Sealed) and a **Message Routing Flow** showing Channel → Dispatcher → Router → Supervisor → Handle. But 6 of the 11 entry points skip this flow and implement their own ad-hoc lifecycle.

Principle 3 (SRP) assigns `ISessionStore` the responsibility of "session persistence" and `IAgentSupervisor` the responsibility of "instance lifecycle". The current code conflates these — callers must remember to call both, and some don't. Principle 12 (Session as Conversation Unit) states sessions are "the unit of conversation state" — but sub-agent sessions have no persisted state.

The proposed `SessionLifecycleService` is the missing orchestration layer that the architecture implies but never explicitly defines. It sits between the entry points (channels, triggers, managers) and the primitives (`ISessionStore`, `IAgentSupervisor`), ensuring every conversation type gets the same lifecycle treatment.

## Immediate Bug

`DefaultSubAgentManager.SpawnAsync()` calls `_supervisor.GetOrCreateAsync()` to create the agent handle, then `RunSubAgentAsync()` calls `handle.PromptAsync()` directly. **Neither method ever calls `_sessionStore.GetOrCreateAsync()`**, so:

- Sub-agent sessions don't appear in the sessions API
- Sub-agent sessions don't appear in the WebUI sidebar
- Sub-agent conversation history is not persisted
- The seal endpoint (Wave 1-3 feature) has nothing to seal
- Sub-agent sessions don't survive gateway restarts
- WebUI sub-agent activity banner is ephemeral (SignalR events only) -- lost on page refresh
- No way to click into a sub-agent conversation from the chat canvas
- Wave 1-3 sidebar/read-only-view/seal code exists but has nothing to render (no persisted sessions)

## Root Cause: No Unified Session Lifecycle

There are **7 distinct entry points** that each re-implement session setup. Here's what each one does (and doesn't do):

### Path Inventory

| #  | Entry Point                          | Location                                      | Session Types     |
|----|--------------------------------------|-----------------------------------------------|-------------------|
| P1 | `GatewayHost.ProcessInboundMessageAsync` | `GatewayHost.cs:198-430`                  | user-agent, cron  |
| P2 | `GatewayHub.JoinSession`             | `GatewayHub.cs:88-140` (deprecated)           | user-agent        |
| P3 | `GatewayHub.SendMessage` → `ResolveOrCreateSessionAsync` | `GatewayHub.cs:165-185, 390-440` | user-agent |
| P4 | `CronTrigger.CreateSessionAsync`     | `CronChannelAdapter.cs:37-56`                 | cron              |
| P5 | `SoulTrigger.CreateSessionAsync`     | `SoulTrigger.cs:48-72`                        | soul              |
| P6 | `ChatController.Send`                | `ChatController.cs:50-85`                     | user-agent        |
| P7 | `AgentConversationService.ConverseAsync` | `AgentConversationService.cs:60-145`      | agent-agent       |
| P8 | `CrossWorldFederationController.RelayAsync` | `CrossWorldFederationController.cs:45-110` | agent-agent    |
| P9 | `DefaultSubAgentManager.SpawnAsync`  | `DefaultSubAgentManager.cs:42-130`            | agent-subagent    |
| P10 | `DefaultAgentCommunicator.CallSubAgentAsync` | `DefaultAgentCommunicator.cs:65-100` | agent-subagent   |
| P11 | `DefaultAgentCommunicator.CallCrossAgentAsync` | `DefaultAgentCommunicator.cs:115-160` | agent-agent   |

### Lifecycle Step Coverage Matrix

| Step                        | P1 GatewayHost | P2 Hub.Join | P3 Hub.Send | P4 Cron | P5 Soul | P6 ChatCtrl | P7 AgentConv | P8 CrossWorld | P9 SubAgent | P10 Communicator.Sub | P11 Communicator.Cross |
|-----------------------------|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| Session store create        | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| Set SessionType             | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| Set ChannelType             | ✅ | ✅ | ✅ | ✅ | ❌¹ | ❌ | ❌¹ | ✅ | ❌ | ❌ | ❌ |
| Set CallerId                | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Add Participants            | ✅ | ✅ | ✅ | ❌ | ✅ | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ |
| Record user message         | ✅ | ❌ | ❌² | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| Record assistant response   | ✅ | ❌ | ❌² | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| Auto-compaction check       | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Expired → Active reactivation | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Status guard (suspended/sealed) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Telemetry (metrics/traces)  | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Activity broadcast           | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅³ | ❌ | ❌ |
| Save after response          | ✅ | ❌⁴ | ❌² | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| SystemPrompt re-init check   | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

¹ Explicitly set to null (intentional)
² P3 (Hub.SendMessage) delegates to `IChannelDispatcher.DispatchAsync` → P1, so history is recorded there
³ Sub-agent publishes lifecycle activities (spawned/completed/failed) but not message-level activities
⁴ P2 saves on join but not on message exchange (delegated to P1 via dispatch)

### Key Observations

1. **P9 (DefaultSubAgentManager) misses ALL session store operations** — the most complete omission. No create, no history, no save, no type, no participants.

2. **P10/P11 (DefaultAgentCommunicator) also miss all session store operations** — these are synchronous sub-agent/cross-agent calls that bypass the session store entirely. Sessions are created only in the supervisor (in-memory agent handles), not in the session store.

3. **P6 (ChatController) records history but misses participants, channel type, caller ID, status guards, telemetry, and compaction** — it's a lean REST path that skips most lifecycle steps.

4. **P4 (CronTrigger) and P5 (SoulTrigger) are well-behaved** — they create sessions, record history, set type, and save. But they miss compaction, telemetry, and status guards.

5. **P2/P3 (GatewayHub) work correctly** by delegating message processing to P1 via `IChannelDispatcher.DispatchAsync`. But P2 (JoinSession) still duplicates session setup logic.

6. **Only P1 (GatewayHost) has the full lifecycle** — telemetry, compaction, status guards, system prompt re-init, streaming, error reporting. Everything else is a subset.

## Proposed Solution: Extract a `SessionLifecycleService`

### Design Principle

**One path, all conversations.** Extract the common session lifecycle into a service that every entry point calls. The service handles:

1. **Session creation/retrieval** — `GetOrCreateAsync` from the session store
2. **Session initialization** — type, channel, participants, caller, metadata
3. **Status enforcement** — reject messages to sealed/suspended sessions, reactivate expired
4. **History recording** — user message in, assistant response out
5. **Compaction** — check and auto-compact when threshold exceeded
6. **Persistence** — save after response
7. **Telemetry** — traces, metrics, activity broadcast

### Proposed Interface

```csharp
public interface ISessionLifecycleService
{
    /// <summary>
    /// Prepare a session for use — create or retrieve, initialize metadata,
    /// enforce status guards, reactivate if expired.
    /// </summary>
    Task<SessionContext> PrepareAsync(SessionPrepareRequest request, CancellationToken ct);

    /// <summary>
    /// Record a completed turn (user message + assistant response),
    /// auto-compact if needed, save to store, emit telemetry.
    /// </summary>
    Task CommitTurnAsync(SessionContext context, string userMessage, string assistantResponse, CancellationToken ct);

    /// <summary>
    /// Record a completed turn from streaming (history already captured by StreamingSessionHelper).
    /// Handles compaction check, telemetry, and activity broadcast.
    /// </summary>
    Task CommitStreamedTurnAsync(SessionContext context, CancellationToken ct);

    /// <summary>
    /// Seal a session (mark terminal, no further messages).
    /// </summary>
    Task SealAsync(SessionId sessionId, CancellationToken ct);
}

public record SessionPrepareRequest
{
    public required SessionId SessionId { get; init; }
    public required AgentId AgentId { get; init; }
    public required SessionType SessionType { get; init; }
    public ChannelKey? ChannelType { get; init; }
    public string? CallerId { get; init; }
    public SessionId? ParentSessionId { get; init; }
    public IReadOnlyList<SessionParticipant>? Participants { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}

public record SessionContext
{
    public required GatewaySession Session { get; init; }
    public required bool WasCreated { get; init; }
    public required bool WasReactivated { get; init; }
}
```

### Migration Plan

#### Phase 1: Fix the Immediate Bug (P9 — Sub-Agent Sessions)

**Goal:** Sub-agent sessions appear in the session store and WebUI.

1. Inject `ISessionStore` into `DefaultSubAgentManager`
2. In `SpawnAsync()`, after `_supervisor.GetOrCreateAsync()`:
   - Call `_sessionStore.GetOrCreateAsync(childSessionId, childAgentId)`
   - Set `SessionType = SessionType.AgentSubAgent`
   - Set `ChannelType = null` (internal)
   - Add parent agent as participant
   - Save
3. In `RunSubAgentAsync()`, after `handle.PromptAsync()`:
   - Load session, add user + assistant entries, save
4. In `OnCompletedAsync()`, update session status to match sub-agent status
5. **Test:** Spawn a sub-agent, verify it appears in `/api/sessions` and WebUI sidebar

**Files changed:**
- `src/gateway/BotNexus.Gateway/Agents/DefaultSubAgentManager.cs`
- Tests for sub-agent session persistence

**Estimated effort:** Small — 1-2 hours

#### Phase 2: Fix P10/P11 (DefaultAgentCommunicator)

**Goal:** Synchronous sub-agent and cross-agent calls also create persisted sessions.

1. Inject `ISessionStore` into `DefaultAgentCommunicator`
2. In `CallSubAgentAsync()`:
   - Call `_sessionStore.GetOrCreateAsync(childSessionId, childAgentId)`
   - Record user message + assistant response
   - Save
3. In `CallCrossAgentAsync()`:
   - Same pattern with `crossSessionId`

**Files changed:**
- `src/gateway/BotNexus.Gateway/Agents/DefaultAgentCommunicator.cs`

**Estimated effort:** Small — 1 hour

#### Phase 3: Extract `SessionLifecycleService`

**Goal:** Single source of truth for session lifecycle.

1. Create `ISessionLifecycleService` and `DefaultSessionLifecycleService`
2. Move the following logic from `GatewayHost.ProcessInboundMessageAsync` into the service:
   - Session creation/retrieval
   - Status guard (reject suspended/sealed, reactivate expired)
   - SessionType inference
   - Participant management
   - SystemPrompt initialization check
   - User message recording
   - Auto-compaction check
   - Session save after response
   - Telemetry emission
3. Refactor `GatewayHost` to use the service (should dramatically shrink the 230-line method)
4. Refactor `CronTrigger`, `SoulTrigger`, `ChatController`, `AgentConversationService`, `CrossWorldFederationController` to use the service
5. Refactor `DefaultSubAgentManager` and `DefaultAgentCommunicator` to use the service

**Files changed:**
- New: `src/gateway/BotNexus.Gateway/Sessions/ISessionLifecycleService.cs`
- New: `src/gateway/BotNexus.Gateway/Sessions/DefaultSessionLifecycleService.cs`
- New: `src/gateway/BotNexus.Gateway/Sessions/SessionPrepareRequest.cs`
- New: `src/gateway/BotNexus.Gateway/Sessions/SessionContext.cs`
- Modified: `GatewayHost.cs` (major simplification)
- Modified: `CronTrigger.cs` (use service instead of inline setup)
- Modified: `SoulTrigger.cs` (use service)
- Modified: `ChatController.cs` (use service)
- Modified: `AgentConversationService.cs` (use service)
- Modified: `CrossWorldFederationController.cs` (use service)
- Modified: `DefaultSubAgentManager.cs` (use service)
- Modified: `DefaultAgentCommunicator.cs` (use service)
- Modified: `GatewayServiceCollectionExtensions.cs` (register service)
- Tests for the new service

**Estimated effort:** Medium — 4-6 hours (mostly mechanical refactoring)

#### Phase 4: Unify Telemetry and Activity Broadcasting

**Goal:** All session types emit consistent telemetry.

Currently only P1 (GatewayHost) emits OpenTelemetry traces and metrics. After Phase 3, the `SessionLifecycleService` can emit telemetry for all paths uniformly.

**Estimated effort:** Small — 1 hour (done as part of Phase 3)

## Current Architecture (Before)

```
GatewayHub ──────→ IChannelDispatcher.DispatchAsync() ──→ GatewayHost (P1)
                                                             ├── session store create ✅
                                                             ├── history recording ✅
                                                             ├── compaction ✅
                                                             ├── telemetry ✅
                                                             └── all lifecycle ✅

CronTrigger ─────→ sessions.GetOrCreateAsync + supervisor.GetOrCreateAsync (P4)
                      ├── session store create ✅
                      ├── history recording ✅
                      └── compaction ❌, telemetry ❌

SoulTrigger ─────→ sessions.GetOrCreateAsync + supervisor.GetOrCreateAsync (P5)
                      ├── session store create ✅
                      ├── history recording ✅
                      └── compaction ❌, telemetry ❌

ChatController ──→ supervisor.GetOrCreateAsync + sessions.GetOrCreateAsync (P6)
                      ├── session store create ✅
                      ├── history recording ✅
                      └── participants ❌, compaction ❌, telemetry ❌

AgentConvSvc ────→ sessions.GetOrCreateAsync + supervisor.GetOrCreateAsync (P7)
                      ├── session store create ✅
                      ├── history recording ✅
                      └── compaction ❌, telemetry ❌

CrossWorldCtrl ──→ sessions.GetOrCreateAsync + supervisor.GetOrCreateAsync (P8)
                      ├── session store create ✅
                      ├── history recording ✅
                      └── compaction ❌, telemetry ❌

SubAgentMgr ─────→ supervisor.GetOrCreateAsync ONLY (P9)
                      ├── session store create ❌ ← BUG
                      ├── history recording ❌ ← BUG
                      └── everything else ❌

AgentComm ───────→ supervisor.GetOrCreateAsync ONLY (P10/P11)
                      ├── session store create ❌ ← BUG
                      ├── history recording ❌ ← BUG
                      └── everything else ❌
```

## Target Architecture (After)

The `SessionLifecycleService` slots into the Gateway layer as an orchestration service — it composes existing primitives (`ISessionStore`, `ISessionCompactor`, `IActivityBroadcaster`) into a single coherent lifecycle. It does NOT replace any existing interface or violate any dependency direction.

### Where It Lives in the Layer Model

```
┌──────────────────────────────────────────────────────────────┐
│                    Gateway (composition root)                 │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │              Entry Points (callers)                     │ │
│  │  GatewayHost · GatewayHub · CronTrigger · SoulTrigger  │ │
│  │  ChatCtrl · AgentConvSvc · CrossWorldCtrl               │ │
│  │  SubAgentMgr · AgentCommunicator                        │ │
│  └────────────────────────┬────────────────────────────────┘ │
│                           │ uses                             │
│  ┌────────────────────────▼────────────────────────────────┐ │
│  │         SessionLifecycleService (NEW)                   │ │
│  │                                                         │ │
│  │  Orchestrates: ISessionStore + ISessionCompactor        │ │
│  │                + IActivityBroadcaster + Telemetry        │ │
│  │                                                         │ │
│  │  PrepareAsync()  → create/resume session, set metadata  │ │
│  │  CommitTurnAsync() → record history, compact, save      │ │
│  │  SealAsync()     → terminal state transition            │ │
│  └────────────────────────┬────────────────────────────────┘ │
│                           │ uses                             │
│  ┌────────────────────────▼────────────────────────────────┐ │
│  │              Existing Primitives                        │ │
│  │  ISessionStore · IAgentSupervisor · ISessionCompactor   │ │
│  │  IActivityBroadcaster · IMessageRouter                  │ │
│  └─────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

### Architectural Principles Honored

| Principle | How the Service Aligns |
|-----------|------------------------|
| **P3: SRP** | `ISessionStore` stays focused on persistence. The service handles *orchestration* — the "when and what" of session lifecycle, not the "how" of storage. |
| **P2: Clean Architecture** | Service depends on abstractions (`ISessionStore`, `ISessionCompactor`). Entry points depend on `ISessionLifecycleService`. Dependencies flow inward. |
| **P12: Session as Conversation Unit** | Service ensures *every* conversation type creates a proper Session — fulfilling the domain model's promise that "every interaction happens within a Session." |
| **P4: OCP** | New entry points (future channels, triggers) call the service instead of re-implementing lifecycle. Open for extension, closed for modification. |
| **P15: Fail Gracefully** | Service can centralize error handling — e.g., compaction failures don't block session save. |

### Domain Model Alignment

| Domain Concept | Current State | After Service |
|----------------|---------------|---------------|
| **Session** ("every interaction") | Sub-agent, communicator paths create no Session | All paths create Sessions |
| **Existence** ("all sessions") | Sub-agent work invisible to Existence | Sub-agent sessions part of parent's Existence |
| **Participants** | Only some paths track participants | All Sessions have proper Participants |
| **Session Lifecycle** (Active→Sealed) | Only GatewayHost enforces status guards | All paths enforce lifecycle |
| **Non-Interactive** flag | Not consistently set | Service sets based on SessionType |

### Data Flow (After)

```
            ┌───────────┬───────────┬───────────┬────────────┐
            │           │           │           │            │
      GatewayHost  CronTrigger  SoulTrigger  SubAgentMgr  ChatCtrl
      (streaming)  (trigger)    (trigger)    (background)  (REST)
            │           │           │           │            │
            └───────────┴───────────┼───────────┴────────────┘
                                    │
                    ┌───────────────▼─────────────────┐
                    │   SessionLifecycleService        │
                    │                                  │
                    │  PrepareAsync()                  │
                    │    → GetOrCreate session          │
                    │    → Set type, channel, caller    │
                    │    → Add participants              │
                    │    → Status guards                │
                    │    → Reactivate if expired         │
                    │                                  │
                    │  CommitTurnAsync()                │
                    │    → Record user + assistant       │
                    │    → Auto-compact if needed        │
                    │    → Save to store                │
                    │    → Emit telemetry               │
                    │    → Broadcast activity            │
                    │                                  │
                    │  SealAsync()                      │
                    │    → Status → Sealed              │
                    └───────────────┬──────────────────┘
                                    │
                 ┌──────────────────┼──────────────────┐
                 │                  │                   │
          ISessionStore    ISessionCompactor    IActivityBroadcaster
          (persistence)    (context mgmt)       (events/telemetry)
                 │                  │                   │
                 └──────────────────┼──────────────────┘
                                    │
                       ┌────────────┴────────────┐
                       │    IAgentSupervisor      │
                       │  GetOrCreateAsync()      │
                       │  (agent handle only —    │
                       │   no session store)      │
                       └─────────────────────────┘
```

**Note:** `IAgentSupervisor` remains separate from the lifecycle service. The supervisor manages *agent instances* (runtime execution contexts). The lifecycle service manages *sessions* (persistent conversation state). These are two distinct responsibilities per SRP, and callers continue to interact with both — but the session side is now consistent.

## Edge Cases

1. **Streaming paths** — `GatewayHost` uses `StreamingSessionHelper` which writes history incrementally. `CommitStreamedTurnAsync()` handles this by skipping history recording but still doing compaction/telemetry/save.

2. **Agent conversation multi-turn** — `AgentConversationService` records multiple turns in a loop. It would call `CommitTurnAsync()` per turn, or we add a `CommitMultiTurnAsync()` variant.

3. **Sub-agent timeout/kill** — `DefaultSubAgentManager` needs to update session status when a sub-agent terminates. `SealAsync()` handles this.

4. **Soul session day-rollover** — `SoulTrigger` seals previous day's sessions. This maps to `SealAsync()`.

5. **Cross-world relay** — Session is created on the receiving end. `PrepareAsync()` handles remote participant metadata.

## Success Criteria

- [ ] Sub-agent sessions appear in `/api/sessions` with `sessionType: "agent-subagent"`
- [ ] Sub-agent sessions visible in WebUI sidebar (Wave 1-3 code activates)
- [ ] Sub-agent conversation history is persisted and viewable
- [ ] All 11 paths use `SessionLifecycleService` (Phase 3)
- [ ] All sessions emit consistent telemetry
- [ ] Existing tests pass (2,341+)
- [ ] New tests for `SessionLifecycleService`
- [ ] Architecture docs updated (see Documentation Deliverables below)

## Documentation Deliverables

The refactor introduces a new architectural component (`SessionLifecycleService`) that must be reflected in the existing docs. Architecture docs have ownership headers — respect them.

### Doc Ownership Convention

Architecture docs may include a YAML front-matter header indicating ownership:

```yaml
---
owner: human          # human | ai | shared
author: Jon Bullen    # original author
ai-policy: minimal    # minimal | collaborative | open
---
```

**Policy meanings:**
- `minimal` — AI may fix typos and broken links. No restructuring, no content removal. Substantive changes require explicit human approval.
- `collaborative` — AI may propose additions (new sections, updated diagrams) but must not remove or rewrite existing content without approval.
- `open` — AI may freely update (still follows good judgment — don't delete useful content).

Docs without a header default to `open` but agents should still exercise care with well-written human-authored content.

### Files to Update

| File | Owner | What to Change | Phase |
|------|-------|----------------|-------|
| `docs/architecture/system-flows.md` | ai/open | Add `SessionLifecycleService` to Session Lifecycle flow (§4). Update the Session Creation diagram to show the service as the orchestration layer. Add a note that all entry points route through it. | P3 |
| `docs/architecture/overview.md` | ai/open | Add `SessionLifecycleService` to the Solution Map table under BotNexus.Gateway. Mention it in the "Key Relationships" section. | P3 |
| `docs/architecture/principles.md` | ai/open | No changes needed — the service *implements* existing principles, doesn't add new ones. | — |
| `docs/architecture/domain-model.md` | **human/minimal** | **Do not modify without Jon's approval.** The domain model already describes Sessions, Existence, and session types correctly — the code is catching up to the spec, not the other way around. If changes are needed, propose them in a PR comment. | — |
| `docs/architecture/extension-guide.md` | ai/open | No changes needed unless the service becomes an extension point. | — |

### What the Doc Updates Should Cover

1. **system-flows.md §4 (Session Lifecycle)**: The current flow shows session creation happening inline in the channel/hub. After this work, the flow should show:
   ```
   Entry Point → SessionLifecycleService.PrepareAsync() → ISessionStore
                                    ↓
              IAgentSupervisor.GetOrCreateAsync() → IAgentHandle
                                    ↓
              handle.PromptAsync() / handle.StreamAsync()
                                    ↓
              SessionLifecycleService.CommitTurnAsync() → save + telemetry
   ```

2. **system-flows.md §6/7 (Triggers & Agent-to-Agent)**: Update these flows to show they use the same lifecycle service instead of inline session management.

3. **overview.md Solution Map**: Add row:
   | `SessionLifecycleService` | Session orchestration | Unified session create/commit/seal across all entry points. Composes ISessionStore + ISessionCompactor + IActivityBroadcaster. |
