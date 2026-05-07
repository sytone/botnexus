# Decision: Memory Tool Boundary (PR #179)

**Author:** Leela (Lead / Architect)  
**Date:** 2025-07-22  
**Status:** Accepted  
**Scope:** `BotNexus.Memory.Tools` agent-facing tool surface

---

## Context

PR #179 (OpenClaw memory alignment) documented `memory_save` as the canonical
agent-facing save tool. However, `MemoryStoreTool` (tool name: `memory_store`)
still exists and is registered alongside `memory_save` in
`InProcessIsolationStrategy` (line 134). This creates two overlapping
"write to memory" tools with different semantics:

| Class | Tool name | Mechanism | Purpose |
|-------|-----------|-----------|---------|
| `MemorySaveTool` | `memory_save` | Appends markdown notes via `IAgentWorkspaceManager` | Daily notes, file-based memory |
| `MemoryStoreTool` | `memory_store` | Inserts structured entries via `IMemoryStore` (SQLite) | Indexed, searchable, TTL-aware entries |

Both are currently registered and visible to agents. This violates the naming
convention in `AGENTS.md` ("Do not call it 'memory store'") and creates
confusion about which tool an agent should use to persist knowledge.

---

## Decision

### 1. `MemoryStoreTool` → DELETE (not rename, not hide)

**Rationale:**
- The codebase forbids `[Obsolete]` and dead code. A "hidden compatibility
  wrapper" is dead code with extra steps.
- `memory_save` (file-based) is the documented agent-facing write path.
- Structured/indexed persistence should happen **internally** (compaction,
  ingestion pipelines) — not via a separate agent-invocable tool that
  exposes implementation terms ("store", "TTL", "tags as metadata JSON").
- No external consumers exist; there is no backward-compatibility contract.

### 2. Canonical agent-facing tool surface after PR #179

| Tool name | Class | Verb | Description |
|-----------|-------|------|-------------|
| `memory_save` | `MemorySaveTool` | Write | Append markdown notes to agent memory workspace |
| `memory_search` | `MemorySearchTool` | Read | Semantic search across indexed memory entries |
| `memory_get` | `MemoryGetTool` | Read | Retrieve a specific entry by ID or list by session |

Three tools. One write, two read. No "store" in agent-facing names.

### 3. Backward compatibility

**None required.**
- No released version exposes `memory_store` to external clients.
- No agent personality files reference `memory_store` by name (verified via
  grep — the tool was added recently and hasn't been adopted in SOUL/IDENTITY
  files).
- Session logs that mention `memory_store` tool calls are historical artifacts;
  they don't affect runtime behaviour.

### 4. PR #179 blocking checks

The following must be verified before merge:

- [ ] `MemoryStoreTool.cs` is deleted
- [ ] `MemoryStoreToolTests.cs` tests are **migrated** (not deleted) — rewrite
      them as integration tests verifying that `memory_save` + internal
      ingestion achieves the same persistence, OR as tests for the internal
      `IMemoryStore.InsertAsync` path used by compaction
- [ ] `InProcessIsolationStrategy` no longer instantiates `MemoryStoreTool`
      (remove line 134)
- [ ] No remaining references to `"memory_store"` as a tool name in production
      code (grep for the string literal)
- [ ] `AGENTS.md` memory naming section remains accurate (it already says the
      right thing)
- [ ] `MemoryToolAdditionalTests.cs` doesn't reference `MemoryStoreTool`
- [ ] Full test suite passes: `dotnet test BotNexus.slnx --nologo --tl:off`

---

## Consequences

- Agents have a single, unambiguous write tool (`memory_save`).
- The SQLite `IMemoryStore` remains as internal infrastructure for search
  indexing, used by `memory_search` and `memory_get` on the read path.
- Future "smart persistence" (embedding, consolidation, dreaming) writes to
  `IMemoryStore` internally — never via an agent-facing tool.
- If a future need arises for agents to write structured/tagged entries, we
  extend `memory_save`'s schema (add optional `tags` param) rather than
  reintroducing a separate tool.
