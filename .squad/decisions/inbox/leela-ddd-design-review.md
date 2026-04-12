# Design Review: DDD Refactoring

**Decision Date:** 2026-04-12
**Decided By:** Leela (Lead/Architect)
**Status:** Approved with modifications
**Spec:** `docs/planning/ddd-refactoring/design-spec.md`
**Research:** `docs/planning/ddd-refactoring/research.md`

---

## Architecture Assessment

### Spec Quality

The spec is research-backed and thorough. The domain-to-code mapping is accurate — I verified every claim against the codebase. The primitive obsession analysis is correct (NormalizeChannelKey triplication confirmed in 5 locations, Role magic strings in 13 occurrences across 6 files, IsolationStrategy string comparisons in 40+ files). The migration strategy (additive-first) is sound.

### What the Spec Gets Right

1. **BotNexus.Domain as the keystone** — Zero-dependency domain project is the correct first move. Everything should reference it downward.
2. **Primitive obsession as P1** — ChannelKey and MessageRole are the highest-ROI value objects. The codebase evidence supports this.
3. **CronChannelAdapter is misplaced** — Confirmed: it implements IChannelAdapter but disables every capability flag. It's a trigger, not a channel.
4. **Sub-agent identity theft is real** — `childAgentId = request.ParentAgentId` (line 64 of DefaultSubAgentManager.cs) makes parent and child indistinguishable in logs and audit.
5. **Additive migration strategy** — Adding new types alongside old ones before migrating consumers is the only safe approach with 13 projects referencing Gateway.Abstractions.

### What the Spec Gets Wrong or Misses

#### M1. Phase 1.1 is too large — decompose it
The spec proposes creating ~20 types in BotNexus.Domain in one shot (Primitives/, Agent/, Session/, World/, Identity/, Communication/, Existence/, Memory/). This is a big-bang within Phase 1. **Decompose into sub-waves:**
- 1.1a: Project creation + Primitives/ (value objects only)
- 1.1b: Session types (Session, SessionStatus, SessionType, SessionParticipant)
- 1.1c: Agent types (AgentDescriptor, AgentInstance moves)
- World/, Identity/, Communication/, Existence/ → defer to their respective phases

#### M2. Serialization strategy is unaddressed
Every value object needs JSON serialization (for REST APIs, config files, session stores) and SQLite storage. The spec says nothing about:
- `JsonConverter<T>` for System.Text.Json compatibility
- SQLite column mapping (Dapper/raw ADO.NET parameter binding)
- Config file backward compatibility (string → smart enum deserialization)
This is not optional — it determines whether value objects are `record struct` (stack-allocated, blittable) or `sealed class` (heap, with custom serialization).

**Decision D1:** Value objects use `readonly record struct` with implicit string conversion operators and `JsonConverter<T>`. Smart enums use `sealed class` with `JsonConverter<T>` and a `FromString()` factory. This gives us type safety without breaking existing serialization.

#### M3. Missing test migration strategy
~2,000 tests use string-based patterns (`AgentId = "test-agent"`, `ChannelType = "signalr"`, `Role = "user"`). The spec mentions Phase 8 tests but doesn't address how existing tests migrate to value objects. Every test that constructs a GatewaySession or AgentDescriptor will need updating.

**Decision D2:** Value objects support implicit conversion from string so existing test code compiles without changes initially. Explicit `FromString()` is preferred in production code. Tests migrate incrementally per-phase.

#### M4. World (Phase 4) is premature — YAGNI
No consumer of WorldDescriptor exists or is planned in the current delivery scope. The spec admits it's "largely a configuration concept today." Creating domain types without consumers is speculative architecture.

**Decision D3:** Defer Phase 4 entirely. If/when cross-world or multi-gateway work begins, model the World then. The extension point in ExecutionStrategy smart enum is sufficient foundation.

#### M5. Agent-to-Agent (Phase 5) is a feature, not a refactor
Phase 5.1 introduces a new `agent_converse` tool and creates an entirely new communication pattern. This is feature development, not domain alignment. It should be spec'd and reviewed independently.

**Decision D4:** Defer Phase 5 to a separate feature spec. The Participants model from Phase 1.3 provides the foundation. The feature can be delivered later without blocking DDD alignment.

#### M6. Soul Session (Phase 2.3) is underspecified
The spec describes the concept but provides no concrete design: What triggers the daily cycle? How does it integrate with existing heartbeat? What's the Soul Session schema? What happens to existing heartbeat sessions?

**Decision D5:** Defer Phase 2.3 to after Phase 2.1 (Cron decoupling) is proven. Spec needs a detailed design for Soul Session lifecycle before implementation.

#### M7. Phase 9 deduplication should be concurrent, not sequential
Phases 9.4 (Provider streaming), 9.5 (PlatformConfig cleanup), and 9.6 (Tool utilities) have zero dependency on the Domain project. They can execute in parallel with Phase 1 work, using different agents.

**Decision D6:** Move Phases 9.4, 9.5, 9.6 to Wave 1 as independent cleanup work. This parallelizes effort.

#### M8. Missing: GatewaySession decomposition sequencing risk
Phase 7.2 (Slim GatewaySession) requires extracting replay buffer and streaming state. GatewaySession has a native `Lock _historyLock` with 5 locked methods. The replay buffer is deeply entangled with WebSocket delivery. This is the highest-risk item in the entire spec.

**Decision D7:** Phase 7.2 requires a dedicated sub-spec with:
- Exact field migration plan (which properties go to Session vs GatewaySessionRuntime)
- Thread-safety model for the split types
- WebSocket handler impact analysis
- Snapshot tests of current behavior before any changes

#### M9. Missing: Abstractions split needs type-forwarding
The research doc mentions type-forwarding attributes but the spec doesn't include this in the plan. With 13 projects referencing Gateway.Abstractions, a cold-turkey split is a build-breaking change.

**Decision D8:** Phase 7.1 uses `[TypeForwardedTo]` attributes. The Gateway.Abstractions assembly remains as a thin forwarder during migration. Types move to Domain or Gateway.Contracts, but the old assembly forwards references so downstream projects compile without changes. Remove the forwarder assembly only after all projects are updated.

---

## Interface Patterns & Contracts

### Value Object Pattern (for Primitives/)

```csharp
// Recommended pattern: readonly record struct with string backing
[JsonConverter(typeof(AgentIdJsonConverter))]
public readonly record struct AgentId(string Value) : IComparable<AgentId>
{
    public static AgentId From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("AgentId cannot be empty", nameof(value))
            : new(value.Trim());

    public static implicit operator string(AgentId id) => id.Value;
    public static explicit operator AgentId(string value) => From(value);

    public override string ToString() => Value;
    public int CompareTo(AgentId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
}
```

- **`AgentId`** — `readonly record struct`, validated, ordinal comparison
- **`SessionId`** — `readonly record struct` with factory methods: `Create()`, `ForSubAgent(parentId, uniqueId)`, `ForCrossAgent(sourceId, targetId)`
- **`ChannelKey`** — `readonly record struct`, normalizes at construction (trim + lowercase + alias mapping), eliminates NormalizeChannelKey()
- **`ConversationId`**, **`SenderId`** — `readonly record struct`, prevents accidental swap
- **`AgentSessionKey`** — `readonly record struct` composing `AgentId` + `SessionId`, replaces `MakeKey()` string concat
- **`ToolName`** — `readonly record struct`, case-insensitive equality via `StringComparer.OrdinalIgnoreCase`

### Smart Enum Pattern (for discriminators)

```csharp
// Recommended pattern: sealed class with static instances + extensibility
[JsonConverter(typeof(SmartEnumJsonConverter<MessageRole>))]
public sealed class MessageRole : IEquatable<MessageRole>
{
    public static readonly MessageRole User = new("user");
    public static readonly MessageRole Assistant = new("assistant");
    public static readonly MessageRole System = new("system");
    public static readonly MessageRole Tool = new("tool");

    private static readonly ConcurrentDictionary<string, MessageRole> _registry = new(StringComparer.OrdinalIgnoreCase);

    public string Value { get; }

    private MessageRole(string value)
    {
        Value = value;
        _registry.TryAdd(value, this);
    }

    public static MessageRole FromString(string value) =>
        _registry.GetOrAdd(value.Trim().ToLowerInvariant(), v => new MessageRole(v));

    public static implicit operator string(MessageRole role) => role.Value;

    public bool Equals(MessageRole? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is MessageRole other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;
}
```

Apply this pattern to: **MessageRole**, **SessionType**, **SessionStatus**, **ExecutionStrategy**, **SubAgentArchetype**, **TriggerType**.

Key properties:
- **Extensible** — Extensions can register new values without modifying core code
- **Serialization-safe** — `FromString()` handles any string input gracefully
- **Reference equality for known values** — Static instances use `==` efficiently
- **Case-insensitive** — Registry uses OrdinalIgnoreCase

### IInternalTrigger Interface (Phase 2.1)

```csharp
public interface IInternalTrigger
{
    TriggerType Type { get; }
    string DisplayName { get; }
    Task<SessionId> CreateSessionAsync(AgentId agentId, string prompt, CancellationToken ct = default);
}
```

### Session Domain Type (Phase 1.1b)

```csharp
public sealed record Session
{
    public required SessionId SessionId { get; init; }
    public required AgentId AgentId { get; init; }
    public required ChannelKey ChannelType { get; init; }
    public required SessionType Type { get; init; }
    public SessionStatus Status { get; private set; } = SessionStatus.Active;
    public bool IsInteractive => Type == SessionType.UserAgent;
    public IReadOnlyList<SessionParticipant> Participants { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    // Domain behavior
    public void Seal() { Status = SessionStatus.Sealed; UpdatedAt = DateTimeOffset.UtcNow; }
    public void Suspend() { Status = SessionStatus.Suspended; UpdatedAt = DateTimeOffset.UtcNow; }
    public void Resume() { Status = SessionStatus.Active; UpdatedAt = DateTimeOffset.UtcNow; }
}
```

Note: History, replay buffer, streaming state stay on GatewaySession (infrastructure) until Phase 7.2.

---

## Risk Assessment

| # | Risk | Severity | Mitigation |
|---|------|----------|------------|
| R1 | **Abstractions split breaks 13 projects** | HIGH | Type-forwarding attributes (D8). Migrate incrementally. Gateway.Abstractions becomes a thin forwarder. |
| R2 | **Value object serialization breaks APIs/config** | HIGH | Implicit string conversion (D1). JSON converters on every value object. Backward-compatible deserialization. |
| R3 | **~2,000 tests need migration** | HIGH | Implicit string→value object conversion (D2). Tests compile without changes initially. Migrate per-phase. |
| R4 | **GatewaySession decomposition (Phase 7.2)** | HIGH | Requires dedicated sub-spec (D7). Snapshot tests before changes. Most dangerous single item. |
| R5 | **Cron session creation path changes** | MEDIUM | IInternalTrigger still creates sessions via ISessionStore. Path changes, API stays. Feature-flag the new path. |
| R6 | **Sub-agent identity change breaks session queries** | MEDIUM | Generated AgentIds must still be discoverable. Add parent→child index to SubAgentInfo. Existing sessions unaffected (new sessions only). |
| R7 | **Smart enum registry memory/concurrency** | LOW | ConcurrentDictionary handles concurrency. Known instances are static. Only truly new values allocate. |
| R8 | **Scope creep from Phase 4/5 speculative work** | MEDIUM | Defer (D3, D4). Strict scope boundary enforcement. |
| R9 | **Provider consolidation breaks edge cases** | MEDIUM | Phase 9.4 must have comprehensive provider conformance tests first. Don't merge without diff-level review of each provider's error handling. |
| R10 | **Performance of value objects vs raw strings** | LOW | readonly record struct is stack-allocated. No heap allocation for value objects. Smart enums (class-based) are singleton instances. |

---

## Wave Plan

### Wave 1: Domain Foundation + Independent Cleanup
*Can start immediately. 3 parallel streams.*

| Work Item | Agent | Description | Depends On |
|-----------|-------|-------------|------------|
| 1.1a Create BotNexus.Domain project + Primitives | Farnsworth | New project, zero deps. AgentId, SessionId, ChannelKey, ConversationId, SenderId, AgentSessionKey, ToolName as readonly record struct. MessageRole, SessionStatus, SessionType, ExecutionStrategy as smart enums. Include JsonConverters. | Nothing |
| 1.1a Domain tests | Hermes | Unit tests for all value objects: construction, validation, equality, comparison, serialization round-trip, implicit conversion, edge cases (null, empty, whitespace). | Parallel with Farnsworth |
| 9.4 Provider streaming consolidation | Bender | Extract shared OpenAI streaming/parsing into OpenAIStreamProcessor in Providers.Core. NormalizeToolCallId becomes extension method. | Nothing (independent) |
| 9.5 PlatformConfig cleanup | Farnsworth | Remove root-level config duplication. Migrate to nested Gateway section. Add config migration step. | Nothing (independent) |
| 9.6 Tool utility elimination | Bender | NormalizeLineEndings → string extension method. Consolidate within BotNexus.Tools. | Nothing (independent) |
| Docs: DDD patterns guide | Kif | Document value object pattern, smart enum pattern, serialization approach for team reference. | Nothing |

### Wave 2: Session Model + Sealed Rename
*Depends on Wave 1 (BotNexus.Domain project must exist).*

| Work Item | Agent | Description | Depends On |
|-----------|-------|-------------|------------|
| 1.2 Rename SessionStatus.Closed → Sealed | Farnsworth | Codebase-wide rename. Update enum, all usages (3 locations in src), all tests, SQLite migration script. | Wave 1 (Domain project) |
| 1.3 Add Participants to Session | Farnsworth | SessionParticipant type in Domain. Add Participants list to GatewaySession. Migrate CallerId → first Participant. Session stores: additive (both fields populated). | Wave 1 |
| 1.4 Add IsInteractive to Session | Farnsworth | Computed property based on SessionType. Add to GatewaySession. | Wave 1 |
| 1.5 Add SessionType discrimination | Farnsworth | Smart enum on Session. Set at creation time. Update session stores to persist. | Wave 1 |
| 1.6a Value objects: ChannelKey + MessageRole adoption | Farnsworth | Replace raw strings in GatewaySession, IChannelAdapter, SessionEntry, InboundMessage, OutboundMessage. Eliminates NormalizeChannelKey x5. | Wave 1 |
| Wave 2 tests | Hermes | Tests for: SessionStatus lifecycle (Active→Suspended→Sealed), Participant management, SessionType discrimination, ChannelKey normalization elimination, MessageRole equality. Snapshot tests for GatewaySession serialization. | Parallel with Farnsworth |
| Docs: Session model update | Kif | Update architecture docs to reflect new session model, status terminology, participant concept. | After Farnsworth delivers |

### Wave 3: Identity Fixes + Cron Decoupling
*Depends on Wave 2 (Session types must be stable).*

| Work Item | Agent | Description | Depends On |
|-----------|-------|-------------|------------|
| 2.2 Sub-agent archetype identity | Bender | Add Archetype field to SubAgentSpawnRequest (default General). Generate distinct AgentId for sub-agents via AgentId.ForWorker(). Update DefaultSubAgentManager, SubAgentInfo. | Wave 2 (AgentId value object) |
| 2.1 Cron decoupling from IChannelAdapter | Bender | Create IInternalTrigger interface. Refactor CronChannelAdapter → CronTrigger : IInternalTrigger. Update session creation path in Gateway to handle internal triggers without channel dispatcher. | Wave 2 (SessionType, ChannelKey) |
| 1.6b Value objects: AgentId + SessionId adoption | Farnsworth | Replace raw strings for AgentId/SessionId across the stack. Update ISessionStore, IAgentSupervisor, IAgentRegistry signatures. Eliminate MakeKey() and Contains("::subagent::") patterns. | Wave 2 |
| Wave 3 tests | Hermes | Sub-agent archetype tests (identity is distinct, trackable, auditable). Cron trigger tests (session creation without channel adapter). AgentId/SessionId migration tests. | Parallel with Bender/Farnsworth |

### Wave 4: Existence + Session Store Improvements
*Depends on Wave 3 (Participants and AgentId value objects needed).*

| Work Item | Agent | Description | Depends On |
|-----------|-------|-------------|------------|
| 3.1 Existence query interface | Farnsworth | Add IExistenceQuery or extend ISessionStore with GetExistenceAsync(). Define ExistenceQuery filter type. | Wave 3 (Participants, AgentId) |
| 3.2 Implement existence in stores | Farnsworth | Update InMemory, File, SQLite stores. SQLite: index on participants. File: sidecar participant index. | 3.1 |
| 9.2 Extract SessionStoreBase | Farnsworth | Abstract base class with shared creation logic, status filtering, channel filtering. Each store overrides storage CRUD only. | Wave 3 |
| Wave 4 tests | Hermes | Existence dual-lookup tests (owned + participated). Session store contract tests (all 3 implementations). SessionStoreBase tests. | Parallel with Farnsworth |

### Wave 5: Abstractions Split + GatewaySession Decomposition
*Depends on Wave 4 (Domain types must be stable before splitting assemblies). This is the highest-risk wave.*

| Work Item | Agent | Description | Depends On |
|-----------|-------|-------------|------------|
| 7.1 Split Gateway.Abstractions | Farnsworth | Move domain types → BotNexus.Domain. Move gateway interfaces → BotNexus.Gateway.Contracts. Gateway.Abstractions becomes thin forwarder with [TypeForwardedTo]. Update all 13 project references incrementally. | Wave 4 (all Domain types finalized) |
| 7.2 Slim GatewaySession (sub-spec required) | Bender | Extract replay buffer, streaming state, lock mechanisms into GatewaySessionRuntime. Session (domain) becomes a clean record. **Requires dedicated sub-spec before implementation.** | 7.1 |
| Wave 5 tests | Hermes | Build verification across all 13 projects. GatewaySession/Runtime split behavioral tests. Regression suite for WebSocket replay. | After 7.1 and 7.2 |

### Wave 6: SystemPromptBuilder Decomposition
*Can start after Wave 2 (needs Domain types) but is independent of Waves 3-5.*

| Work Item | Agent | Description | Depends On |
|-----------|-------|-------------|------------|
| 9.1 SystemPromptBuilder pipeline | Bender | Decompose 572-line Build() into section pipeline (ToolSection, SkillSection, MessagingSection, etc.). Extract shared primitives into BotNexus.Prompts library. | Wave 2 (MessageRole, SessionType) |
| 9.1 Tests | Hermes | Snapshot tests of current prompt output BEFORE refactoring. Per-section unit tests after decomposition. Regression: full prompt output must match. | Before Bender starts |
| Docs: Prompt architecture | Kif | Document section pipeline, extension points for custom sections. | After Bender delivers |

---

## Scope Recommendation

### This Delivery Cycle (Waves 1-4, ~3-4 weeks)

**IN SCOPE:**
- Phase 1 complete (BotNexus.Domain, all value objects, session model improvements)
- Phase 2.1-2.2 (Cron decoupling, sub-agent archetypes)
- Phase 3 (Existence queries)
- Phase 9.2 (SessionStoreBase)
- Phase 9.4-9.6 (Provider consolidation, config cleanup, tool utilities)
- Phase 8 (tests throughout)
- Documentation for all changes

**Rationale:** This delivers the core DDD alignment (shared vocabulary, type safety, identity fixes) without the highest-risk refactors (Abstractions split, GatewaySession decomposition).

### Next Delivery Cycle (Waves 5-6, ~2-3 weeks)

**DEFERRED — Ready to start after Wave 4 stabilizes:**
- Phase 7.1 (Split Abstractions) — needs all Domain types stable first
- Phase 7.2 (Slim GatewaySession) — needs dedicated sub-spec, highest-risk item
- Phase 9.1 (SystemPromptBuilder decomposition) — large but independent
- Phase 9.3 (Unify CodingAgent sessions) — depends on 7.2

### Future Cycles (Not this quarter)

**DEFERRED — Speculative or feature work:**
- Phase 2.3 (Soul Session lifecycle) — underspecified, needs design
- Phase 4 (World as domain object) — YAGNI, no consumer
- Phase 5 (Agent-to-Agent communication) — new feature, needs own spec
- Phase 6 (Cross-World) — already marked future in spec
- Phase 7.3 (PlatformConfig nested migration) — moved to Wave 1 as 9.5

---

## Grade: B+

**Rationale:**

The spec is well-researched, correctly identifies the real problems, and proposes sound solutions. The domain-to-code mapping is accurate and every claim I verified against the codebase held up. The migration strategy is correct (additive-first). The primitive obsession analysis is the strongest section — it precisely identifies the value objects that will have the highest impact.

It loses points for:
- **Scope ambition** — 9 phases mixing DDD alignment with speculative features (World, Agent-to-Agent) and a full dedup pass dilutes focus
- **Missing serialization strategy** — The spec proposes value objects without addressing how they serialize, which is where most migration pain lives
- **Phase 1.1 monolith** — Creating 20 types in one phase is a big-bang wearing an incremental disguise
- **GatewaySession decomposition underestimated** — The most dangerous refactor gets 5 lines of spec
- **No test migration strategy** — ~2,000 tests using string patterns will resist value object adoption unless implicit conversion is planned

With the modifications above (D1-D8), the spec is actionable and the wave plan provides a safe execution path. The core insight — BotNexus needs a shared domain vocabulary — is absolutely correct and overdue.

---

## Key Decisions Summary

| ID | Decision | Rationale |
|----|----------|-----------|
| D1 | Value objects: `readonly record struct` with implicit string conversion + JsonConverter | Stack-allocated, serialization-safe, backward-compatible |
| D2 | Tests migrate incrementally via implicit conversion | ~2,000 tests can't all change at once |
| D3 | Defer Phase 4 (World) | YAGNI — no consumer exists |
| D4 | Defer Phase 5 (Agent-to-Agent) | Feature work, not DDD alignment — needs own spec |
| D5 | Defer Phase 2.3 (Soul Session) | Underspecified — needs detailed design |
| D6 | Move Phases 9.4/9.5/9.6 to Wave 1 | Zero dependency on Domain project — parallelize |
| D7 | Phase 7.2 requires dedicated sub-spec | GatewaySession decomposition is highest-risk item |
| D8 | Phase 7.1 uses TypeForwardedTo attributes | 13 projects can't all switch at once |

---

## Impact

- **Domain types:** ~15 value objects + 6 smart enums in BotNexus.Domain
- **Eliminated duplication:** NormalizeChannelKey x5, NormalizeToolCallId x2, MakeKey(), string comparisons ~25
- **Projects affected:** 13 (via Abstractions split, deferred to Wave 5)
- **Estimated new tests:** ~120-150 across all waves
- **Breaking changes:** None if implicit conversion is implemented (D1, D2)

---

## Related Files

- `docs/planning/ddd-refactoring/design-spec.md` — the spec under review
- `docs/planning/ddd-refactoring/research.md` — supporting research
- `src/gateway/BotNexus.Gateway.Abstractions/` — primary refactor target
- `src/gateway/BotNexus.Gateway.Sessions/` — session store implementations
- `src/gateway/BotNexus.Gateway/Agents/DefaultSubAgentManager.cs` — identity theft fix
- `src/gateway/BotNexus.Gateway.Api/Hubs/CronChannelAdapter.cs` — cron decoupling target
