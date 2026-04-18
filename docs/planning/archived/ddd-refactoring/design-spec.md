---
id: ddd-refactoring
title: "BotNexus Domain-Driven Design Refactoring Plan"
type: improvement
priority: high
status: done
created: 2025-07-24
---

# BotNexus Domain-Driven Design Refactoring Plan

**Status**: Done

This plan maps the [domain model](botnexus-domain-model.md) to the current codebase and defines the work needed to align them. The goal is cleaner contracts, better separation of concerns, simpler testing, and a codebase that speaks the same language as the architecture.

---

## Current State: Domain-to-Code Mapping

### What aligns well

| Domain Concept    | Current Code                        | Status  |
| ----------------- | ----------------------------------- | ------- |
| Agent Descriptor  | `AgentDescriptor`                   | Good    |
| Agent Instance    | `AgentInstance`                     | Good    |
| Session           | `GatewaySession`                    | Partial |
| Session Store     | `ISessionStore` + 3 implementations | Good    |
| Channel           | `IChannelAdapter`                   | Partial |
| Channel Manager   | `ChannelManager`                    | Good    |
| Message Routing   | `IMessageRouter`                    | Good    |
| Agent Registry    | `IAgentRegistry`                    | Good    |
| Agent Supervisor  | `IAgentSupervisor`                  | Good    |
| Agent Handle      | `IAgentHandle`                      | Good    |
| Sub-Agent Manager | `ISubAgentManager`                  | Partial |
| Memory            | `BotNexus.Memory` project           | Good    |
| Cron              | `BotNexus.Cron` project             | Good    |
| Tools             | `BotNexus.Tools` + extensions       | Good    |
| Providers         | `BotNexus.Providers.*`              | Good    |

### What's missing or misaligned

| Domain Concept               | Gap                                                                                                            | Priority |
| ---------------------------- | -------------------------------------------------------------------------------------------------------------- | -------- |
| **World**                    | No concept. Isolation strategies exist but aren't modeled as a domain object                                   | P1       |
| **Soul**                     | Just a file in `SystemPromptFiles`. No Soul Session, no daily cycle                                            | P2       |
| **Identity**                 | Just a file in `SystemPromptFiles`. No structured Identity type, no bootstrap ritual                           | P2       |
| **Existence**                | No aggregate. Sessions queryable by agent ID but no owned+participated dual-lookup                             | P1       |
| **Session.Participants**     | Missing. No way to model who is in a session beyond `CallerId`                                                 | P1       |
| **Session.IsInteractive**    | Missing. Implicit by convention (cron = non-interactive)                                                       | P1       |
| **Session.Sealed**           | Code uses `SessionStatus.Closed`. Should be `Sealed`                                                           | P1       |
| **Session Types**            | No type discrimination. All sessions look the same structurally                                                | P2       |
| **Sub-Agent Identity**       | Workers reuse parent's agent ID (`childAgentId = request.ParentAgentId`). Should have archetype-based identity | P1       |
| **Agent-to-Agent**           | No formal conversation pattern. Sub-agents only, no peer conversations                                         | P3       |
| **Cross-World**              | No concept. No cross-world channel, no federation                                                              | P3       |
| **Tools & Skills**           | Skills exist as an extension (`BotNexus.Extensions.Skills`) but not in the domain model contracts              | P2       |
| **Cron as internal trigger** | `CronChannelAdapter` implements `IChannelAdapter` - contradicts the domain model                               | P1       |

### Code quality issues

1. **No domain layer**: Business logic lives in Gateway implementation. No dedicated project for core domain types and rules. (Phase 1)
2. **Primitive obsession**: Raw strings used for AgentId, SessionId, ChannelType, MessageRole, IsolationStrategy - leads to triplicated normalization helpers and type-unsafe method signatures. (Phase 1.6)
3. **Utility/helper class proliferation**: `PathUtils`, `StreamingSessionHelper`, `SimpleOptionsHelper`, `AgentDescriptorValidator`, `MessageTransformer`, `ContextOverflowDetector` - static helpers that should be methods on the objects they operate on, or replaced by richer domain types. (Phase 1.6, Phase 9)
4. **Abstractions grab bag**: `BotNexus.Gateway.Abstractions` mixes domain models, service interfaces, hooks, security, and extension models in one project. (Phase 7.1)
5. **Session is a God object**: `GatewaySession` handles domain state, replay buffers, stream sequencing, and thread-safe locking. (Phase 7.2)
6. **Duplicate implementations**: Two SystemPromptBuilders, two SessionManagers, triplicated helpers across session stores. (Phase 9)
7. **Test coverage gaps**: No tests for session stores, channel adapters, routing pipeline, or sub-agent lifecycle. (Phase 8)

---

## Refactoring Plan

### Phase 1: Domain Foundation (Core Contracts)

**Goal**: Introduce a `BotNexus.Domain` project that owns the canonical domain types. Everything else references it.

#### 1.1 Create `BotNexus.Domain` project

New project with zero dependencies (pure domain types). This becomes the shared vocabulary:

```
BotNexus.Domain/
  Primitives/
    AgentId.cs                   - value object wrapping string, validated, equality
    SessionId.cs                 - value object with typed construction (Create, ForSubAgent, ForCrossAgent)
    AgentSessionKey.cs           - composite of AgentId + SessionId, replaces MakeKey() string concat
    ChannelKey.cs                - value object with built-in normalization, kills NormalizeChannelKey()
    ConversationId.cs            - value object preventing mix-up with SenderId
    SenderId.cs                  - value object preventing mix-up with ConversationId
    ToolName.cs                  - value object with case-insensitive equality
    MessageRole.cs               - smart enum: User, Assistant, System, Tool (extensible, not a C# enum)
  Agent/
    AgentDescriptor.cs           - move from Abstractions, add grouped property categories, use AgentId
    AgentInstance.cs              - move from Abstractions, use AgentId/SessionId
    AgentInstanceStatus.cs       - move from Abstractions
  Session/
    Session.cs                   - new clean type (replaces GatewaySession's domain role)
    SessionStatus.cs             - rename Closed -> Sealed, add states
    SessionType.cs               - smart enum: UserAgent, AgentSelf, AgentSubAgent, AgentAgent, Soul, Cron (extensible)
    SessionParticipant.cs        - new: who is in the session (user or agent, with world context)
  World/
    World.cs                     - new: environment descriptor
    Location.cs                  - new: accessible resource descriptor
    ExecutionStrategy.cs         - smart enum: InProcess, Sandbox, Container, Remote (extensible by extensions)
  Identity/
    AgentIdentity.cs             - new: structured identity (name, archetype, emoji, avatar, vibe)
  Communication/
    ConversationRequest.cs       - new: agent-to-agent conversation initiation
    SubAgentArchetype.cs         - smart enum: Researcher, Coder, Planner, Reviewer, Writer, General (extensible)
  Existence/
    IExistenceQuery.cs           - new: interface for querying an agent's session history (owned + participated)
  Memory/
    MemoryConfig.cs              - move from Abstractions
```

**Key decisions**:

- Domain types are records/value objects where possible (immutable)
- No framework dependencies (no ASP.NET, no EF, no DI)
- No implementation details (no SQLite, no file paths, no HTTP)

#### 1.2 Rename SessionStatus.Closed to Sealed

Across the entire codebase. Small but aligns the language.

#### 1.3 Add Participants to Session

Replace `CallerId` (string) with `Participants` (list of `SessionParticipant`). A participant has:

- Type (User or Agent)
- ID (user ID or agent ID)
- WorldId (for cross-world context, nullable for local)
- Role (User, Agent, Initiator, etc.)

Migrate existing `CallerId` data into the new structure.

#### 1.4 Add IsInteractive to Session

Explicit boolean property. Set based on session type:

- UserAgent = true
- Everything else = false

#### 1.5 Add SessionType discrimination

Smart enum discriminator on the session. Set at creation time based on how the session was created. Extensible so new session types can be introduced by extensions without modifying core code.

---

#### 1.6 Replace primitive obsession with value objects

See [research.md](research.md) "Primitive Obsession Analysis" for the full inventory.

The `Primitives/` folder in `BotNexus.Domain` introduces typed value objects for identifiers and discriminators that are currently raw strings. Key changes:

- **`ChannelKey`** - eliminates all three copies of `NormalizeChannelKey()`. Normalization happens at construction, equality is built-in.
- **`AgentId`** / **`SessionId`** / **`AgentSessionKey`** - replaces `MakeKey()` string concatenation, `Contains("::subagent::")` detection, and prevents ID type mix-ups across method signatures.
- **`MessageRole`** smart enum - replaces `"user"`, `"assistant"`, `"system"`, `"tool"` string literals scattered across 10+ files. Extensible by extensions that introduce new message roles.
- **`ExecutionStrategy`** smart enum - replaces `"in-process"` string comparisons. Parsed once at config load. New strategies can be registered by extensions.
- **`ConversationId`** / **`SenderId`** - prevents accidentally swapping channel-specific identifiers.
- **`ToolName`** - eliminates repeated `.Trim().ToLowerInvariant()` normalization in SystemPromptBuilder.

Adopt incrementally: start with `ChannelKey` and `MessageRole` (highest impact, lowest risk), then `AgentId`/`SessionId` (broader reach, needs careful migration).

---

### Phase 2: Fix Cron and Sub-Agent Identity

#### 2.1 Decouple Cron from IChannelAdapter

The domain model is clear: cron is an internal trigger, not a channel. Currently `CronChannelAdapter` implements `IChannelAdapter`.

Create `IInternalTrigger` interface:

```csharp
public interface IInternalTrigger
{
    TriggerType Type { get; }  // smart enum: Cron, Soul, Heartbeat (extensible)
    Task<Session> CreateSessionAsync(AgentId agentId, ...);
}
```

Refactor `CronChannelAdapter` into `CronTrigger : IInternalTrigger`. The Gateway routes internal triggers differently from channel messages - they bypass the channel dispatcher and go straight to session creation.

#### 2.2 Sub-Agent Archetype Identity

In `DefaultSubAgentManager.SpawnAsync()`, the current code does:

```csharp
var childAgentId = request.ParentAgentId;  // WRONG - identity theft
```

Change to:

- `SubAgentSpawnRequest` gets an optional `Archetype` field (defaults to `General`)
- The sub-agent gets a generated `AgentId` based on archetype (e.g., `AgentId.ForWorker(parentAgentId, archetype, uniqueId)`)
- This identity is trackable, auditable, and distinct from the parent
- Update `SubAgentInfo` to include the archetype

#### 2.3 Soul Session lifecycle

Introduce the daily Soul Session creation and sealing. This is an internal trigger (like cron) that:

- Creates a new Soul Session at a configured time (or on first heartbeat of the day)
- Seals the previous day's Soul Session
- The Soul Session is where heartbeat runs execute

---

### Phase 3: Existence and Dual-Lookup

#### 3.1 Existence query interface

Add to `ISessionStore` (or create `IExistenceStore`):

```csharp
Task<IReadOnlyList<Session>> GetExistenceAsync(
    AgentId agentId,
    ExistenceQuery query,   // date range, type filter, etc.
    CancellationToken ct);
```

This query returns sessions where:

- `AgentId == agentId` (owned sessions), OR
- `Participants.Any(p => p.Id == agentId)` (participated sessions)

This is the agent's Existence - every session it's been part of.

#### 3.2 Implement in session stores

Update `FileSessionStore`, `SqliteSessionStore`, and `InMemorySessionStore` to support the dual-lookup. SQLite can use an index on participants; file store may need a sidecar index.

---

### Phase 4: World as a Domain Object

#### 4.1 World descriptor

Create `WorldDescriptor` that captures:

- Gateway identity (ID, name)
- Hosted agents
- Available locations (file system paths, APIs, services)
- Execution strategies available
- Cross-world permissions

This is largely a configuration concept today but making it explicit enables:

- Multi-gateway federation planning
- Clear boundaries for what an agent can access
- Foundation for cross-world channels

#### 4.2 Location model

Formalize the resources an agent can reach. This maps to the current tool/extension config but gives it a domain name. Initially this may just be a structured view of what's already configured - making implicit things explicit.

---

### Phase 5: Agent-to-Agent Communication

#### 5.1 Conversation pattern

New capability: an agent can initiate a conversation with another registered agent.

- New tool: `agent_converse` (or similar)
- Creates an Agent-Agent session owned by the initiating agent
- Target agent is added to Participants
- Initiating agent's messages have role "user", target agent's messages have role "assistant"
- Session is sealed when the conversation objective is met
- Both agents find this session in their Existence

This builds on the existing sub-agent infrastructure but the key difference is: the target is a *real agent* with its own identity, soul, and system prompt - not a disposable worker.

#### 5.2 Cycle detection

Prevent Agent A -> Agent B -> Agent A loops. Track the call chain and enforce a maximum depth.

---

### Phase 6: Cross-World (Future)

Not immediate, but the foundation is being laid:

- World as a domain object (Phase 4)
- Participants with world context (Phase 1)
- Cross-world channel concept (domain model defines it)

When ready:

- Implement cross-world channel adapter
- Two sessions per conversation (one per world)
- Gateway-to-gateway authentication and message exchange

---

### Phase 7: Clean Up Abstractions

#### 7.1 Split Gateway.Abstractions

The current `BotNexus.Gateway.Abstractions` project is doing too much. Split into:

- **BotNexus.Domain** (Phase 1) - pure domain types, no dependencies
- **BotNexus.Gateway.Contracts** - gateway-specific interfaces (`ISessionStore`, `IChannelAdapter`, `IAgentSupervisor`, etc.)
- Remove `BotNexus.Gateway.Abstractions` once everything is migrated

#### 7.2 Slim down GatewaySession

`GatewaySession` currently handles:

- Domain state (status, participants, history)
- Infrastructure (replay buffer, stream sequencing, thread-safe locks)

Split into:

- `Session` (domain) - the conceptual session from the domain model
- `GatewaySessionRuntime` (infrastructure) - replay buffer, streaming, WebSocket concerns

#### 7.3 Clean up PlatformConfig

Remove the legacy root-level duplication. Everything goes under the nested config sections. One migration step to update existing config files, then remove the `Get*()` resolution methods.

---

### Phase 8: Test Alignment

#### 8.1 Domain unit tests

New `BotNexus.Domain.Tests` project:

- Session lifecycle (Active -> Suspended -> Sealed)
- Participant management
- Existence queries
- Session type discrimination

#### 8.2 Fill coverage gaps

Priority test additions:

- Session store implementations (File, SQLite) - CRUD, lifecycle, dual-lookup
- Channel adapter contracts (test that adapters correctly create sessions)
- Internal trigger contracts (cron, soul session creation)
- Sub-agent archetype assignment
- Message routing with session types

---

### Phase 9: Code Deduplication & Refactoring

See [research.md](research.md) for detailed analysis of each item.

#### 9.1 Unify SystemPromptBuilder (HIGH)

Two separate prompt builders exist - gateway (572 lines) and coding agent (283 lines). They share the same concept but have diverged into separate implementations with different parameter types.

- Extract shared prompt building primitives DOWN into a new `BotNexus.Prompts` project (AgentCore should stay lean and generic - it is the foundation that Gateway and CodingAgent build upon, not a dumping ground for shared logic)
- Gateway builder composes from shared primitives with gateway-specific sections
- CodingAgent delegates to the same shared code with coding-specific overrides
- Neither product references the other - both reference the shared library
- Decompose the gateway's 572-line `Build()` method into a section pipeline (ToolSection, SkillSection, MessagingSection, etc.) - each testable independently

#### 9.2 Extract shared session store logic (MEDIUM)

All three session stores (`FileSessionStore`, `SqliteSessionStore`, `InMemorySessionStore`) independently implement:

- Session creation with default state
- Status-based filtering in `ListAsync` (Active/Suspended/Sealed)
- Channel-based filtering in `ListByChannelAsync`

Note: The triplicated `NormalizeChannelKey()` is eliminated by Phase 1.6's `ChannelKey` value object. What remains is duplicated business logic.

Extract an abstract `SessionStoreBase` with shared creation and query filter logic. Each implementation overrides only storage-specific CRUD.

#### 9.3 Unify session management with CodingAgent (MEDIUM)

`CodingAgent/Session/SessionManager.cs` (759 lines) is a completely separate session system from the gateway's `ISessionStore`. Both persist conversation history with different data models and storage formats.

- If CodingAgent is being absorbed into the gateway: migrate to `ISessionStore`
- If they remain separate: extract shared session primitives (JSONL format, common types) into a shared library
- The gateway must never reference CodingAgent - shared logic flows down into common libraries

#### 9.4 Consolidate OpenAI provider streaming (MEDIUM)

`OpenAICompletionsProvider` (1291 lines) and `OpenAICompatProvider` (652 lines) share significant streaming/parsing logic for the OpenAI chat completions format. `NormalizeToolCallId` is duplicated across OpenAI and Anthropic providers with different max lengths.

- Extract common completions streaming/parsing into a shared `OpenAIStreamProcessor` class in `BotNexus.Agent.Providers.Core`
- `NormalizeToolCallId` becomes a method on a `ToolCallId` value object or a scoped extension method - not a static utility
- Compat provider delegates response parsing to shared code, only overrides auth/endpoint

#### 9.5 Clean PlatformConfig legacy duplication (MEDIUM)

13 config properties exist at root level AND under `Gateway.*` with `Get*()` resolver methods. This is legacy compatibility cruft.

- One-time migration to move all settings under the `Gateway` section
- Add config migration step to auto-rewrite old-format configs
- Remove root-level properties and resolver methods

#### 9.6 Eliminate tool utility classes (LOW)

`NormalizeLineEndings` duplicated between `EditTool` and `ReadTool`. `PathUtils` is a 240-line static class doing work that should be on a value object.

- `NormalizeLineEndings` becomes an extension method on `string` or a method on a `FileContent` type
- `PathUtils.ResolvePath` / `SanitizePath` / `IsUnderRoot` become construction logic on a `WorkspacePath` value object - if the object exists, the path is valid and contained
- The `Utils/` folder should be empty when done

---

## Implementation Order

| Phase                                       | Effort  | Risk   | Depends On  |
| ------------------------------------------- | ------- | ------ | ----------- |
| 1.1 Create BotNexus.Domain                  | Medium  | Low    | Nothing     |
| 1.2 Sealed rename                           | Small   | Low    | 1.1         |
| 1.3 Participants                            | Medium  | Medium | 1.1         |
| 1.4 IsInteractive                           | Small   | Low    | 1.1         |
| 1.5 SessionType                             | Small   | Low    | 1.1         |
| 1.6 Value objects (ChannelKey, MessageRole) | Medium  | Low    | 1.1         |
| 1.6 Value objects (AgentId, SessionId)      | Medium  | Medium | 1.6 partial |
| 2.1 Cron decoupling                         | Medium  | Medium | 1.1         |
| 2.2 Sub-agent archetypes                    | Medium  | Low    | 1.1         |
| 2.3 Soul Session lifecycle                  | Medium  | Medium | 2.1         |
| 3.1 Existence query                         | Medium  | Low    | 1.3         |
| 3.2 Store implementations                   | Medium  | Medium | 3.1         |
| 4.1 World descriptor                        | Small   | Low    | 1.1         |
| 4.2 Location model                          | Small   | Low    | 4.1         |
| 5.1 Agent-to-Agent                          | Large   | Medium | 1.3, 3.1    |
| 5.2 Cycle detection                         | Small   | Low    | 5.1         |
| 6.x Cross-world                             | Large   | High   | 4.x, 5.x    |
| 7.1 Split Abstractions                      | Large   | Medium | 1.1         |
| 7.2 Slim GatewaySession                     | Medium  | Medium | 1.1, 7.1    |
| 7.3 Clean PlatformConfig                    | Medium  | Low    | Nothing     |
| 8.x Tests                                   | Ongoing | Low    | Each phase  |
| 9.1 Unify SystemPromptBuilder               | Large   | Medium | 1.1         |
| 9.2 Session store base class                | Small   | Low    | 1.1         |
| 9.3 Unify CodingAgent sessions              | Medium  | Medium | 7.2         |
| 9.4 Provider streaming consolidation        | Medium  | Medium | Nothing     |
| 9.6 Tool utilities                          | Small   | Low    | Nothing     |

### Recommended execution order

1. **Phase 1.1** - Create BotNexus.Domain. This is the keystone - everything depends on it.
2. **Phase 1.2-1.5** - Quick wins on Session (Sealed, Participants, IsInteractive, SessionType). These are additive and low-risk.
3. **Phase 2.2** - Sub-agent archetypes. Fixes identity confusion.
4. **Phase 2.1** - Cron decoupling. Fixes the biggest domain/code mismatch.
5. **Phase 7.3** - Clean PlatformConfig. Independent, reduces noise.
6. **Phase 3** - Existence. Builds on Participants.
7. **Phase 7.1-7.2** - Split Abstractions and slim GatewaySession. Bigger refactor, do after the domain types stabilize.
8. **Phase 4** - World. Mostly modeling, low risk.
9. **Phase 2.3** - Soul Session. Needs cron decoupling first.
10. **Phase 5** - Agent-to-Agent. New capability, builds on everything.
11. **Phase 8** - Tests throughout, but especially after each phase.
12. **Phase 6** - Cross-world. Future.

---

## Migration Strategy

All changes should be **additive first, then migrative**:

1. Add new types alongside old ones
2. Update producers to populate both old and new
3. Update consumers to read from new
4. Remove old types once nothing references them

This avoids big-bang rewrites and keeps the system deployable at every step. Each phase should be a series of PRs, not one massive change. It also allows testing and validation updates, new tests are added, new types added, tests pass. Migration happens, tests pass. old removed along with tests coverage is still good and tests pass. This incremental approach minimizes risk and keeps the codebase stable throughout the refactor. Consistency with documentation can also be done at each stage reducing the final synchronization effort which would be large.

---

## Success Criteria

- Every domain concept in the domain model document has a corresponding type in `BotNexus.Domain`
- The codebase uses domain language consistently (Sealed not Closed, Participants not CallerId, etc.)
- Sessions are typed and distinguishable (you can query "show me all agent-to-agent conversations")
- Sub-agents have their own identity, not the parent's
- Cron is not a channel
- An agent's Existence is queryable (owned + participated sessions)
- `BotNexus.Gateway.Abstractions` is replaced by `BotNexus.Domain` + `BotNexus.Gateway.Contracts`
- No raw string comparisons for channel types, message roles, or isolation strategies
- Identifier mix-ups are prevented by the type system (AgentId != SessionId != ConversationId)
- Test coverage exists for domain rules and session lifecycle
- No duplicated utility methods across session stores
- SystemPromptBuilder is decomposed into testable section builders
- PlatformConfig has no legacy root-level duplication
- Provider streaming logic is shared, not copy-pasted
- No `Utils/` or `Helpers/` folders remain - logic lives on the objects it operates on
