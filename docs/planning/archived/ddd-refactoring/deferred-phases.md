---
status: deferred
created: 2026-04-12
source: design-spec.md
review: leela-ddd-design-review
---

# DDD Refactoring — Deferred Phases

Phases deferred from the current delivery cycle based on Leela's design review (graded B+). Each section includes the original spec content, the reason for deferral, and what's needed before it can be picked up.

---

## Phase 2.3: Soul Session Lifecycle

**Original scope:** Introduce daily Soul Session creation and sealing as an internal trigger.

**What it would do:**
- Create a new Soul Session at a configured time (or on first heartbeat of the day)
- Seal the previous day's Soul Session
- Soul Session becomes where heartbeat runs execute

**Why deferred (Decision D5):** Underspecified. The spec describes the concept but provides no concrete design:
- What triggers the daily cycle?
- How does it integrate with existing heartbeat?
- What's the Soul Session schema?
- What happens to existing heartbeat sessions?

**Depends on:** Phase 2.1 (Cron decoupling) must be proven first — Soul Sessions use the same `IInternalTrigger` pattern.

**What's needed to unblock:**
- [ ] Detailed design for Soul Session lifecycle (trigger mechanism, schema, heartbeat integration)
- [ ] Decision on whether Soul Sessions replace heartbeat sessions or coexist
- [ ] Phase 2.1 delivered and stable

---

## Phase 4: World as a Domain Object

### 4.1 World Descriptor

**Original scope:** Create `WorldDescriptor` capturing gateway identity, hosted agents, available locations, execution strategies, and cross-world permissions.

### 4.2 Location Model

**Original scope:** Formalize the resources an agent can reach as structured domain types.

**Why deferred (Decision D3):** YAGNI — no consumer of `WorldDescriptor` exists or is planned. The spec admits it's "largely a configuration concept today." Creating domain types without consumers is speculative architecture. The `ExecutionStrategy` smart enum (delivered in Wave 1) provides sufficient foundation for now.

**Depends on:** Phase 1.1 (BotNexus.Domain project).

**What's needed to unblock:**
- [ ] A concrete feature that needs World as a first-class domain object (e.g., multi-gateway federation, cross-world channels)
- [ ] At least one consumer that would use the WorldDescriptor API

---

## Phase 5: Agent-to-Agent Communication

### 5.1 Conversation Pattern

**Original scope:** New capability allowing an agent to initiate a conversation with another registered agent via an `agent_converse` tool. Creates Agent-Agent sessions, adds target to Participants, and both agents find the session in their Existence.

### 5.2 Cycle Detection

**Original scope:** Prevent Agent A → Agent B → Agent A loops by tracking the call chain and enforcing maximum depth.

**Why deferred (Decision D4):** This is a new feature, not a refactor. It introduces a new tool (`agent_converse`), a new communication pattern, and new session lifecycle behavior. It should be spec'd and reviewed independently as a feature rather than bundled into the DDD alignment work.

**Depends on:** Phase 1.3 (Participants model) and Phase 3.1 (Existence queries).

**What's needed to unblock:**
- [ ] Dedicated feature spec for Agent-to-Agent communication
- [ ] Design review by Leela covering: tool API, session ownership, message routing, error handling
- [ ] Phases 1.3 and 3.1 delivered and stable

---

## Phase 6: Cross-World Federation

**Original scope:** Cross-world channel adapter, two sessions per conversation (one per world), gateway-to-gateway authentication and message exchange.

**Why deferred:** Already marked as future in the original spec. Foundational work (World as domain object, Participants with world context) is itself deferred.

**Depends on:** Phase 4 (World), Phase 5 (Agent-to-Agent).

**What's needed to unblock:**
- [ ] Phase 4 and Phase 5 delivered
- [ ] Multi-gateway deployment scenario defined
- [ ] Security model for gateway-to-gateway authentication designed
- [ ] Dedicated feature spec

---

## Phase 7.1: Split Gateway.Abstractions

**Original scope:** Move domain types to `BotNexus.Domain`, move gateway interfaces to `BotNexus.Gateway.Contracts`, remove `BotNexus.Gateway.Abstractions` once everything is migrated.

**Why deferred to next cycle (not cancelled):** This is a high-risk change affecting 13 downstream projects. All Domain types must be finalized and stable before splitting assemblies. Requires `[TypeForwardedTo]` attributes (Decision D8) for incremental migration.

**Depends on:** Waves 1-4 complete (all Domain types finalized).

**What's needed to unblock:**
- [ ] Waves 1-4 delivered and stable
- [ ] Full inventory of types to move (Domain vs Gateway.Contracts)
- [ ] Migration plan for each of the 13 dependent projects
- [ ] `[TypeForwardedTo]` attribute strategy verified with a proof-of-concept

---

## Phase 7.2: Slim Down GatewaySession

**Original scope:** Split `GatewaySession` into `Session` (domain) and `GatewaySessionRuntime` (infrastructure — replay buffer, streaming, WebSocket concerns).

**Why deferred to next cycle (Decision D7):** This is the highest-risk item in the entire spec. `GatewaySession` has a native `Lock _historyLock` with 5 locked methods. The replay buffer is deeply entangled with WebSocket delivery. The spec gives this 5 lines — it needs a dedicated sub-spec.

**Depends on:** Phase 7.1 (Split Abstractions).

**What's needed to unblock:**
- [ ] Dedicated sub-spec with:
  - Exact field migration plan (which properties go to Session vs GatewaySessionRuntime)
  - Thread-safety model for the split types
  - WebSocket handler impact analysis
  - Snapshot tests of current behavior before any changes
- [ ] Phase 7.1 delivered

---

## Phase 9.1: Unify SystemPromptBuilder

**Original scope:** Decompose the gateway's 572-line `Build()` method into a section pipeline (ToolSection, SkillSection, MessagingSection, etc.). Extract shared prompt building primitives into a new `BotNexus.Prompts` project. CodingAgent delegates to shared code.

**Why deferred to next cycle:** Large refactor that is independent of the core DDD alignment. Can execute after Wave 2 (needs MessageRole, SessionType) but is a separate stream of work.

**Depends on:** Wave 2 (MessageRole and SessionType value objects).

**What's needed to unblock:**
- [ ] Wave 2 delivered (MessageRole smart enum available)
- [ ] Snapshot tests of current prompt output BEFORE refactoring (Hermes should write these first)
- [ ] Decision on `BotNexus.Prompts` project boundaries

---

## Phase 9.3: Unify CodingAgent Sessions

**Original scope:** `CodingAgent/Session/SessionManager.cs` (759 lines) is a completely separate session system from the gateway's `ISessionStore`. Both persist conversation history with different data models.

**Why deferred to next cycle:** Depends on Phase 7.2 (GatewaySession decomposition). The decision on whether CodingAgent is absorbed into the gateway or remains separate must be made first.

**Depends on:** Phase 7.2 (Slim GatewaySession).

**What's needed to unblock:**
- [ ] Phase 7.2 delivered
- [ ] Strategic decision: Is CodingAgent being absorbed into the gateway, or do they remain separate?
- [ ] If separate: define shared session primitives (JSONL format, common types)
- [ ] If absorbed: migration plan from CodingAgent sessions to ISessionStore

---

## Summary

| Phase | Risk | Reason | Earliest Unblock |
|-------|------|--------|-----------------|
| 2.3 Soul Session | Medium | Underspecified | After Phase 2.1 + detailed design |
| 4 World | Low | YAGNI — no consumer | When a feature needs it |
| 5 Agent-to-Agent | Medium | Feature work, needs own spec | After Phases 1.3 + 3.1 + feature spec |
| 6 Cross-World | High | Future — foundational work deferred | After Phases 4 + 5 |
| 7.1 Split Abstractions | High | 13 projects affected | After Waves 1-4 stable |
| 7.2 Slim GatewaySession | High | Highest-risk item, needs sub-spec | After Phase 7.1 + sub-spec |
| 9.1 SystemPromptBuilder | Medium | Large but independent | After Wave 2 + snapshot tests |
| 9.3 Unify CodingAgent | Medium | Depends on 7.2 + strategic decision | After Phase 7.2 |
