# Memory System Feature Spec — Architectural Review & Implementation Plan

**Decision Date:** 2026-04-09  
**Author:** Leela (Lead/Architect)  
**Requested by:** Jon Bullen  
**Status:** 📋 Review + Plan (no implementation)  
**Spec Source:** Nova's `memory-system-feature-spec.md` (310 lines)

---

## 1. Architectural Review

### 1.1 Alignment with Existing Patterns

**Overall: Strong alignment.** The spec clearly reflects familiarity with BotNexus internals. Key observations:

| Area | Alignment | Notes |
|------|-----------|-------|
| SQLite storage | ✅ Excellent | Matches `SqliteSessionStore` precedent — same NuGet (`Microsoft.Data.Sqlite`), same patterns |
| Per-agent isolation | ✅ Excellent | `BotNexusHome.GetAgentDirectory()` already scaffolds `data/` subdirectory — `data/memory.sqlite` is natural |
| Config-driven | ✅ Good | Config schema fits alongside existing `AgentDefinitionConfig` — but needs a new `MemoryConfig` class, not inline JSON |
| Tool system | ⚠️ Needs refinement | Memory tools don't fit the existing `IAgentToolFactory` pattern cleanly (see §2.1) |
| Session lifecycle hooks | ⚠️ Gap identified | `ISessionStore` has no event system; spec assumes hooks that don't exist (see §2.2) |
| DI registration | ✅ Good | Follows `AddBotNexusTools()` extension method pattern |

### 1.2 SOLID Concerns

**Single Responsibility:**
- ✅ Separate memory store from session store — correct, different lifecycle and query patterns.
- ⚠️ The spec bundles search, indexing, compaction, and embedding into one conceptual system. Implementation should split these into distinct services: `IMemoryStore` (CRUD), `IMemorySearchEngine` (query), `IMemoryIndexer` (session→memory pipeline), `IEmbeddingProvider` (vector generation).

**Open/Closed:**
- ✅ Embedding provider abstraction is well-designed for extensibility.
- ✅ Search strategy (BM25-only vs hybrid) as a pluggable concern is correct.

**Liskov Substitution:**
- ✅ No concerns. The `IAgentTool` contract is well-suited for memory tools.

**Interface Segregation:**
- ⚠️ The spec's `memory_get` tool does double duty: get-by-ID and list-by-session. These are different operations with different parameters. Recommend keeping them combined (the model handles parameter selection well) but ensuring the implementation has clean internal separation.

**Dependency Inversion:**
- ✅ All integration points go through abstractions (`ISessionStore`, `IAgentToolFactory`, `IToolRegistry`).
- ⚠️ Memory tools need agent context (agent ID, config) at construction time. Current `IAgentToolFactory.CreateTools(string workingDirectory)` only takes a path. This interface needs extension (see §4).

### 1.3 Over-Engineering Risks

| Risk | Level | Recommendation |
|------|-------|----------------|
| Embedding pipeline in P0 | 🔴 High | Spec correctly puts this in P1. Enforce this boundary — FTS5 is surprisingly good for conversational search. |
| MMR diversity | 🟡 Medium | Nice to have but adds complexity. Defer until search quality data shows duplicate-heavy results. |
| Dreaming/consolidation | 🟢 Low | P2 is correct placement. Don't even stub this in P0. |
| `expires_at` TTL | 🟡 Medium | The schema includes it, but no P0 feature uses it. Add the column but don't build the reaper until needed. |
| Cross-agent sharing | 🟢 Low | P2 is correct. The per-agent DB isolation makes this harder later, but that's the right trade-off — isolation is more important than future sharing convenience. |

### 1.4 Gaps and Ambiguities

1. **Chunking strategy undefined.** The spec says "conversation turns are indexed" but doesn't specify granularity. Options: per-turn (every user/assistant message), per-exchange (user+assistant pair), per-session (full session as one entry). Recommendation: **per-exchange** (user message + assistant response as one memory entry) — balances searchability with context.

2. **Token/content size limits missing.** What happens when an assistant response is 50KB? FTS5 handles large texts but search relevance degrades. Need a `maxContentLength` with truncation or chunking strategy.

3. **Deduplication not addressed.** If a session is saved multiple times (incremental saves via `StreamingSessionHelper`), auto-indexing could create duplicate memories. Need an idempotency key (e.g., `session_id + turn_index`).

4. **Memory tool availability.** The spec says tools register via `toolIds` in config, but doesn't specify the default. Should memory tools be opt-in (`toolIds: ["memory_search", ...]`) or auto-included when `memory.enabled: true`? Recommendation: **auto-included when enabled** — requiring both config flags is error-prone.

5. **FTS5 content sync triggers.** The spec's triggers use `content='memories'` (contentless FTS with external content). This requires matching rowid types. The `memories` table uses TEXT `id` (ULID) as PK, but FTS5 `content_rowid` needs an INTEGER. Need to add a `rowid` alias or use SQLite's implicit rowid.

6. **Session suspend/resume interaction.** BotNexus has session suspend/resume. When should indexing happen — on suspend? on close? on every save? Recommendation: **on session status transition to Closed/Expired**, not on every save.

---

## 2. Integration Analysis

### 2.1 Tool Registration and Resolution

**Current architecture:**

```
IAgentToolFactory.CreateTools(workingDirectory)    → workspace-scoped tools (Read, Write, Shell, etc.)
IToolRegistry.ResolveTools(toolIds)                → extension tools (singleton, DI-registered)
InProcessIsolationStrategy.CreateAsync()           → merges both, filtered by AgentDescriptor.ToolIds
```

**Problem:** Memory tools are neither workspace-scoped (they need agent ID + config, not just a path) nor pure extension tools (they're per-agent, not singleton).

**Solution:** Extend `IAgentToolFactory` to accept richer context:

```csharp
// New overload or replacement
IReadOnlyList<IAgentTool> CreateTools(AgentToolContext context);

public record AgentToolContext(
    string WorkingDirectory,
    string AgentId,
    AgentDescriptor Descriptor,
    IServiceProvider Services);
```

This is a breaking change to `IAgentToolFactory`. Alternative: register memory tools as extension tools via `IToolRegistry` but scope them per-agent using a factory pattern internally. The factory approach avoids the breaking change but is less clean.

**Recommendation:** Introduce a new `IAgentContextToolFactory` interface alongside the existing one. `InProcessIsolationStrategy` can call both. This preserves backward compatibility.

### 2.2 Session Lifecycle Events

**Current state:** `ISessionStore` is a pure CRUD interface with no events. Session lifecycle transitions happen in:
- `StreamingSessionHelper.ProcessAndSaveAsync()` — saves after stream completes
- `SessionCleanupService` — marks sessions Expired, deletes old Closed sessions
- `ChatController` / `WebSocketMessageDispatcher` — create sessions, save after responses

**No centralized lifecycle event bus exists.**

**Options:**

| Approach | Pros | Cons |
|----------|------|------|
| A) `ISessionLifecycleEvents` interface + pub/sub | Clean, decoupled, testable | New abstraction, need to wire into all save paths |
| B) Decorator on `ISessionStore` | No new interface, transparent | Hard to distinguish "save" from "session complete" |
| C) Poll `ISessionStore` for changes | Zero code changes to session layer | Latency, resource waste, misses rapid session closures |

**Recommendation: Option A — `ISessionLifecycleEvents`.**

```csharp
public interface ISessionLifecycleEvents
{
    event Func<SessionLifecycleEvent, CancellationToken, Task>? SessionChanged;
}

public record SessionLifecycleEvent(
    string SessionId,
    string AgentId,
    SessionLifecycleEventType Type,  // Created, MessageAdded, Closed, Expired, Deleted
    GatewaySession Session);
```

Wire it into `StreamingSessionHelper` (on stream complete) and `SessionCleanupService` (on expire/close). The memory indexer subscribes to `SessionChanged` and indexes on `Closed` or `Expired` events.

### 2.3 Where Memory Tools Should Live

**Options:**

| Location | Pros | Cons |
|----------|------|------|
| New `BotNexus.Memory` project | Clean separation, own deps, own tests | Another project to maintain |
| Existing `BotNexus.Tools` | Co-located with other tools | Tools project has no DI, no SQLite deps, no config awareness |
| `BotNexus.Gateway` | Access to all services | Violates separation, Gateway is already large |

**Recommendation: New `BotNexus.Memory` project** under `src/tools/BotNexus.Memory/`.

Structure:
```
src/tools/BotNexus.Memory/
├── BotNexus.Memory.csproj
├── MemoryStore.cs              // SQLite CRUD + FTS5
├── MemorySearchEngine.cs       // BM25 search with temporal decay
├── MemoryIndexer.cs            // Session → memory pipeline
├── Tools/
│   ├── MemorySearchTool.cs
│   ├── MemoryGetTool.cs
│   └── MemoryStoreTool.cs
├── Configuration/
│   └── MemoryConfig.cs
├── Embedding/                  // P1
│   ├── IEmbeddingProvider.cs
│   └── OllamaEmbeddingProvider.cs
└── Extensions/
    └── MemoryServiceCollectionExtensions.cs
```

Dependencies: `BotNexus.AgentCore` (for `IAgentTool`), `Microsoft.Data.Sqlite`, `BotNexus.Gateway.Abstractions` (for `ISessionStore`, `GatewaySession`).

### 2.4 Config Schema Mapping

**Current `AgentDefinitionConfig`** (in `PlatformConfig.cs`) has no `Memory` property.

**Required addition:**

```csharp
// In AgentDefinitionConfig
public MemoryConfig? Memory { get; set; }

// New class
public sealed class MemoryConfig
{
    public bool Enabled { get; set; }
    public string Indexing { get; set; } = "auto";  // auto | manual | off
    public MemorySearchConfig? Search { get; set; }
    public MemoryEmbeddingConfig? Embedding { get; set; }  // P1
}

public sealed class MemorySearchConfig
{
    public int DefaultTopK { get; set; } = 10;
    public TemporalDecayConfig? TemporalDecay { get; set; }
}

public sealed class TemporalDecayConfig
{
    public bool Enabled { get; set; } = true;
    public int HalfLifeDays { get; set; } = 30;
}
```

This maps to `AgentDescriptor` via a new `MemoryConfig` property. The `PlatformConfigAgentSource` will need to propagate it during descriptor construction.

### 2.5 Memory DB Path

**Current agent data directory:** `~/.botnexus/agents/{id}/data/sessions/` (scaffolded by `BotNexusHome.ScaffoldAgentWorkspace()`).

**Memory DB path:** `~/.botnexus/agents/{id}/data/memory.sqlite`

This requires:
1. Update `BotNexusHome.ScaffoldAgentWorkspace()` to create `data/` directory (already done — `data/sessions/` implies `data/` exists).
2. Memory store receives path from `BotNexusHome.GetAgentDirectory(agentId) + "/data/memory.sqlite"`.
3. Connection string: `Data Source={path};Mode=ReadWriteCreate`

---

## 3. Implementation Plan

### Wave 1 — P0 Core (Memory Store + Search + Indexing)

**Estimated effort:** 3-4 sessions across 2-3 agents

#### Task 1.1: Memory Project Scaffold + SQLite Store
**Agent:** Farnsworth (core/platform)  
**Creates:**
- `src/tools/BotNexus.Memory/BotNexus.Memory.csproj`
- `src/tools/BotNexus.Memory/MemoryStore.cs` — `IMemoryStore` interface + SQLite implementation
- `src/tools/BotNexus.Memory/Models/MemoryEntry.cs` — domain model
- Schema: `memories` table + `memories_fts` FTS5 virtual table + sync triggers
- CRUD: `InsertAsync`, `GetByIdAsync`, `GetBySessionAsync`, `DeleteAsync`
- Add project reference to `BotNexus.slnx`

**Key decisions:**
- Use ULID for `id` (time-sortable, no coordination needed)
- Add explicit `rowid INTEGER PRIMARY KEY AUTOINCREMENT` alongside TEXT `id` for FTS5 content sync
- WAL mode for concurrent read/write
- Connection created on demand, not pooled (same pattern as `SqliteSessionStore`)

**Dependencies:** None

#### Task 1.2: FTS5 Search Engine with Temporal Decay
**Agent:** Farnsworth (core/platform)  
**Creates:**
- `src/tools/BotNexus.Memory/MemorySearchEngine.cs` — `IMemorySearchEngine` interface + BM25 implementation

**Key logic:**
```
final_score = bm25_rank * temporal_decay_factor
temporal_decay_factor = exp(-ln(2) / halfLifeDays * ageDays)
```

**Parameters:** query, topK, filters (sourceType, sessionId, dateRange, tags)  
**Dependencies:** Task 1.1

#### Task 1.3: Memory Tools (search + get)
**Agent:** Bender (runtime/integration)  
**Creates:**
- `src/tools/BotNexus.Memory/Tools/MemorySearchTool.cs` — implements `IAgentTool`
- `src/tools/BotNexus.Memory/Tools/MemoryGetTool.cs` — implements `IAgentTool`

**Integration:** Tools receive `IMemoryStore` + `IMemorySearchEngine` via constructor. Tool definitions follow spec JSON schemas exactly.

**Dependencies:** Tasks 1.1, 1.2

#### Task 1.4: Session Lifecycle Events + Memory Indexer
**Agent:** Farnsworth (core/platform)  
**Creates:**
- `src/gateway/BotNexus.Gateway.Abstractions/Sessions/ISessionLifecycleEvents.cs`
- `src/gateway/BotNexus.Gateway.Abstractions/Sessions/SessionLifecycleEvent.cs`
- `src/tools/BotNexus.Memory/MemoryIndexer.cs` — `IMemoryIndexer` subscribes to session events

**Modifies:**
- `StreamingSessionHelper.cs` — raise `SessionChanged` after final save
- `SessionCleanupService.cs` — raise `SessionChanged` on expire
- `GatewayServiceCollectionExtensions.cs` — register lifecycle events

**Indexing strategy:**
- Trigger on session close/expire
- Extract user+assistant exchange pairs from `GatewaySession.History`
- Skip tool-role entries (noisy)
- Idempotency: composite key `session_id + turn_index` prevents duplicate indexing
- Index asynchronously (fire-and-forget with error logging, don't block session teardown)

**Dependencies:** Task 1.1

#### Task 1.5: Config Schema + Agent Wiring
**Agent:** Bender (runtime/integration)  
**Creates:**
- `src/tools/BotNexus.Memory/Configuration/MemoryConfig.cs`
- `src/tools/BotNexus.Memory/Extensions/MemoryServiceCollectionExtensions.cs`

**Modifies:**
- `PlatformConfig.cs` → add `Memory` property to `AgentDefinitionConfig`
- `AgentDescriptor.cs` → add `MemoryConfig?` property
- `PlatformConfigAgentSource.cs` → map memory config to descriptor
- `InProcessIsolationStrategy.cs` → create memory tools when enabled, inject into agent tool list
- `ToolServiceCollectionExtensions.cs` → register memory services

**Key design:** Memory tools are created per-agent in `InProcessIsolationStrategy.CreateAsync()`, not via `IAgentToolFactory`. This avoids breaking the existing interface. The strategy already has access to `AgentDescriptor` (which carries the memory config) and can construct memory tools directly using DI-resolved `IMemoryStore` factory.

**Dependencies:** Tasks 1.3, 1.4

#### Task 1.6: Tests
**Agent:** Hermes (tests)  
**Creates:**
- `tests/BotNexus.Memory.Tests/BotNexus.Memory.Tests.csproj`
- `tests/BotNexus.Memory.Tests/MemoryStoreTests.cs` — CRUD, schema creation, idempotency
- `tests/BotNexus.Memory.Tests/MemorySearchEngineTests.cs` — BM25 ranking, temporal decay math, filter combinations
- `tests/BotNexus.Memory.Tests/MemoryIndexerTests.cs` — session-to-memory pipeline, deduplication
- `tests/BotNexus.Memory.Tests/Tools/MemorySearchToolTests.cs` — argument validation, result formatting
- `tests/BotNexus.Memory.Tests/Tools/MemoryGetToolTests.cs`
- `tests/BotNexus.Memory.Tests/MemoryIsolationTests.cs` — verify agent A cannot read agent B's memories

**Dependencies:** Tasks 1.1–1.5 (can start test scaffolding in parallel)

---

### Wave 2 — P0 Complete (Explicit Store + CLI + Registration)

**Estimated effort:** 2 sessions

#### Task 2.1: `memory_store` Tool
**Agent:** Bender  
**Creates:**
- `src/tools/BotNexus.Memory/Tools/MemoryStoreTool.cs`

**Follows same pattern as search/get tools. Source type: `manual`.**

**Dependencies:** Wave 1

#### Task 2.2: CLI Commands
**Agent:** Bender  
**Creates:**
- `src/gateway/BotNexus.Cli/Commands/MemoryCommands.cs`

**Commands:**
- `botnexus memory status <agentId>` — entry count, DB size, last indexed timestamp
- `botnexus memory search <agentId> <query>` — CLI search for debugging
- `botnexus memory clear <agentId> [--confirm]` — wipe with confirmation gate

**Modifies:**
- CLI root command registration

**Dependencies:** Wave 1

#### Task 2.3: DefaultAgentToolFactory Integration
**Agent:** Farnsworth  
**Evaluates:** Whether memory tools should also be available through `IToolRegistry` for extension discoverability. If agents use `toolIds` to select tools, memory tools need to be resolvable by name (`"memory_search"`, `"memory_get"`, `"memory_store"`).

**Likely approach:** Register memory tool *factories* in `IToolRegistry` that produce agent-scoped tool instances. This lets `toolIds` filtering work while maintaining per-agent scoping.

**Dependencies:** Wave 1

#### Task 2.4: Wave 2 Tests
**Agent:** Hermes  
**Creates:**
- `tests/BotNexus.Memory.Tests/Tools/MemoryStoreToolTests.cs`
- `tests/BotNexus.Memory.Tests/MemoryCommandTests.cs` (CLI integration tests)

**Dependencies:** Tasks 2.1–2.3

---

### Wave 3 — P1 (Embedding + Hybrid Search)

**Estimated effort:** 3-4 sessions

#### Task 3.1: Embedding Provider Abstraction
**Agent:** Farnsworth  
**Creates:**
- `src/tools/BotNexus.Memory/Embedding/IEmbeddingProvider.cs`
- `src/tools/BotNexus.Memory/Embedding/OllamaEmbeddingProvider.cs`
- `src/tools/BotNexus.Memory/Embedding/OpenAICompatEmbeddingProvider.cs`

**Integration with `BotNexus.Providers.Core`** for auth reuse where possible.

#### Task 3.2: Hybrid Search (BM25 + Vector)
**Agent:** Farnsworth  
**Modifies:**
- `MemorySearchEngine.cs` — add vector similarity path, weighted combination
- `MemoryStore.cs` — add embedding column population, vector index

#### Task 3.3: MMR Diversity
**Agent:** Farnsworth  
**Modifies:**
- `MemorySearchEngine.cs` — post-retrieval MMR reranking

#### Task 3.4: Memory Compaction
**Agent:** Bender  
**Creates:**
- `src/tools/BotNexus.Memory/MemoryCompactor.cs`
- Cron job integration via `BotNexus.Cron`

**Modes:** `off`, `safeguard` (compress + archive originals), `aggressive` (summarize + discard)

#### Task 3.5: Wave 3 Tests
**Agent:** Hermes

---

### Wave 4 — P2 (Dreaming + Import/Export + Workspace Indexing)

**Estimated effort:** 3-4 sessions (can be deferred indefinitely)

#### Task 4.1: Dreaming/Consolidation
**Agent:** Farnsworth + Bender  
**Cron-driven background process.** Requires LLM calls for summarization — expensive, must be opt-in.

#### Task 4.2: Import/Export
**Agent:** Bender  
**CLI commands:** `botnexus memory export/import`

#### Task 4.3: Workspace File Indexing
**Agent:** Bender  
**Index MEMORY.md, playbooks, daily notes into memory FTS.**

#### Task 4.4: Wave 4 Tests
**Agent:** Hermes

---

## 4. Key Design Decisions

### 4.1 New Project vs. Existing Project

**Decision: New `BotNexus.Memory` project** under `src/tools/`.

**Rationale:**
- `BotNexus.Tools` is a leaf project with no DI, no SQLite, no config awareness — memory doesn't fit.
- `BotNexus.Gateway` is already the largest project — adding memory inflates it further.
- A new project gets its own test project, clean dependency graph, and can be conditionally loaded.
- Follows the pattern of `BotNexus.Gateway.Sessions` as a separate project for session storage.

### 4.2 Memory Tool Registration

**Decision: Created per-agent in `InProcessIsolationStrategy`, not via `IAgentToolFactory`.**

**Rationale:**
- Memory tools need agent ID, memory config, and DI services — `IAgentToolFactory.CreateTools(string)` can't provide these.
- Introducing `IAgentContextToolFactory` is a future option but premature for one feature.
- `InProcessIsolationStrategy.CreateAsync()` already has full context (`AgentDescriptor`, DI via constructor). It's the natural point to inject memory tools when `descriptor.MemoryConfig?.Enabled == true`.
- Memory tools are appended to the tool list alongside workspace tools and extension tools.

### 4.3 Session Indexing: Event-Based

**Decision: `ISessionLifecycleEvents` pub/sub — not polling, not decorator.**

**Rationale:**
- Polling wastes resources and has latency. The session store processes are already in our control.
- A decorator on `ISessionStore.SaveAsync()` can't distinguish "save-in-progress" from "session-complete."
- An event bus is the cleanest way for memory indexing to subscribe without coupling to session store internals.
- Other future features (analytics, audit log) can also subscribe.

### 4.4 Embedding Pipeline: Async Background

**Decision: Background embedding, not synchronous.**

**Rationale:**
- Embedding a single entry with `nomic-embed-text` via Ollama takes ~50-200ms. Batching helps but still blocks.
- Memory entries should be searchable via FTS5 immediately. Embeddings can arrive later.
- Use a `Channel<MemoryEntry>` as an in-process queue. A `BackgroundService` dequeues and computes embeddings.
- If the process crashes, un-embedded entries are detected on startup (WHERE `embedding IS NULL`) and re-queued.

### 4.5 Memory DB Connection Management

**Decision: Created on demand, not pooled. One DB file per agent.**

**Rationale:**
- Matches `SqliteSessionStore` pattern exactly — create connection, use, dispose.
- SQLite WAL mode handles concurrent readers well. Single-writer is fine for memory workloads.
- Agent count is small (typically 1-5). No need for pooling overhead.
- Each agent's DB is at `~/.botnexus/agents/{id}/data/memory.sqlite`.
- `IMemoryStore` is registered as a factory: `IMemoryStoreFactory.Create(agentId)` returns a store scoped to that agent's DB.

---

## 5. Risk Assessment

### 5.1 Performance Impact of Auto-Indexing

**Risk: Medium.** Session close triggers indexing of all conversation turns.

**Mitigations:**
- Index asynchronously (fire-and-forget from lifecycle event handler).
- Batch inserts in a single SQLite transaction.
- FTS5 trigger overhead is minimal for insert (the expensive part is queries, not writes).
- Set a maximum entries-per-session cap (e.g., 500 exchanges) with warning log if exceeded.

### 5.2 SQLite Contention with Concurrent Sessions

**Risk: Low-Medium.** Multiple sessions for the same agent could write to the same memory DB concurrently.

**Mitigations:**
- WAL mode (`PRAGMA journal_mode=WAL`) allows concurrent reads during writes.
- Use `SemaphoreSlim` for write serialization (same pattern as `SqliteSessionStore`).
- Indexing is batched and infrequent (session close, not per-message).
- Memory search (read-only) doesn't contend with writes in WAL mode.

### 5.3 Memory Growth Without Compaction

**Risk: Medium.** A chatty agent could accumulate thousands of memory entries over months.

**Mitigations:**
- Compaction is P1, not P2 — should follow quickly after P0 ships.
- `is_archived` flag allows soft-delete without data loss.
- `expires_at` TTL column is in schema from day one (enforcement can come later).
- CLI `memory status` shows DB size — operators can monitor and `memory clear` if needed.
- Rule of thumb: 10,000 conversation exchanges ≈ 50MB SQLite with FTS5. Manageable.

### 5.4 FTS5 Query Injection

**Risk: Low but real.** FTS5 `MATCH` syntax accepts operators (`AND`, `OR`, `NOT`, `NEAR`, `"phrase"`). Malicious or malformed queries could cause unexpected behavior or errors.

**Mitigations:**
- Sanitize user queries: escape FTS5 special characters before passing to `MATCH`.
- Wrap query execution in try-catch — return empty results on parse error, not a crash.
- Use parameterized queries for all non-FTS parts (standard SQL injection prevention).
- FTS5 doesn't allow destructive operations via MATCH — worst case is a bad query, not data loss.

### 5.5 Additional Risk: Migration Path

**Risk: Low.** Schema changes in future waves (adding embedding column, compaction tables) need migration.

**Mitigations:**
- Add a `schema_version` table from day one.
- Apply migrations on startup (same EnsureCreated pattern as `SqliteSessionStore`).
- Schema changes in P1 (embedding column) are backward-compatible (nullable BLOB).

---

## 6. Summary of Recommendations

1. **Start with Wave 1 only.** Don't stub P1/P2 interfaces — build them when needed.
2. **Per-exchange indexing** — one memory entry per user+assistant exchange pair.
3. **Auto-include memory tools** when `memory.enabled: true` — don't require `toolIds` listing.
4. **`ISessionLifecycleEvents`** — invest in the event bus; it pays dividends beyond memory.
5. **`IMemoryStoreFactory`** pattern — creates agent-scoped stores from `BotNexusHome` paths.
6. **WAL mode + SemaphoreSlim** — proven concurrency pattern from existing codebase.
7. **Schema version table** from day one — saves pain in P1 migration.
8. **FTS5 rowid alignment** — fix the spec's schema to use INTEGER rowid for FTS5 content sync.
