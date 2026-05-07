---
id: botnexus-openclaw-memory-alignment
title: "BotNexus OpenClaw Memory Model Alignment"
type: improvement
priority: high
status: planning
created: 2026-05-07
updated: 2026-05-07
author: Kif (Documentation Engineer)
tags: [memory, architecture, openclaw, migration, data-portability]
depends_on: [improvement-memory-lifecycle]
notes: "Foundation for interoperability with OpenClaw; enables file-based memory authoring and embeddings-backed search."
---

# Design Spec: BotNexus OpenClaw Memory Model Alignment

**Type**: Improvement  
**Priority**: High (architectural alignment, migration compatibility, data portability)  
**Status**: Planning  
**Author**: Kif (Documentation Engineer) with input from Leela (Architecture) and Sytone (Product)

---

## 1. Problem Statement

BotNexus currently treats **SQLite `memory_store` as the primary authoring surface** for agent memory, while OpenClaw implements a **Markdown-first, file-authoritative** memory model where SQLite/embeddings are derived indexes (rebuildable). This architectural divergence creates:

- **Data lock-in**: Memory is tied to SQLite schema; migrating between BotNexus and OpenClaw requires complex transforms
- **Portability friction**: Users cannot easily examine or edit memory files directly
- **Limited reasoning**: Agents are told to write to `memory_save()` tool (opaque database) rather than structured Markdown files
- **Search misalignment**: BotNexus indexes conversation turns; OpenClaw indexes file contents (MEMORY.md + daily notes)
- **Migration incompatibility**: Porting an agent from BotNexus to OpenClaw or vice versa requires data re-indexing

---

## 2. Current BotNexus Memory Behavior

### 2.1 Current Architecture
- **Primary storage**: SQLite database (per-agent memory tables)
- **Authoring surface**: `memory_save()` tool (opaque; writes to DB)
- **Indexing**: Conversation turn pairs indexed into SQLite (via `MemoryIndexer`)
- **Search**: SQLite queries with basic FTS5
- **File equivalents**: None — daily notes are not persisted or tracked

### 2.2 Current Data Flow
```
Agent turn (IAgentRunner)
  ↓
Agent calls memory_save(text)
  ↓
IMemoryStore.Set("daily/YYYY-MM-DD", text)
  ↓
SQLite write (agent_memory table)
  ↓
MemoryIndexer indexes turn pairs → SQLite FTS
  ↓
memory_get() reads from SQLite
```

### 2.3 Current Limitations
- No canonical Markdown files; SQLite is source of truth
- Daily notes path is `memory/daily/YYYY-MM-DD.md` (not yet implemented; spec draft exists)
- No pre-compaction memory flush (agents unaware of token limits)
- No dreaming/consolidation phase
- No embeddings-backed search
- `AGENTS.md` has no memory authoring instructions

---

## 3. OpenClaw-Aligned Target Model

### 3.1 Architecture Principles
1. **Markdown-first, file-authoritative**: Memory files (MEMORY.md, memory/YYYY-MM-DD.md) are source of truth
2. **SQLite/embeddings are derived indexes**: Rebuilt from files; never directly authored by agents or tools
3. **Explicit daily notes**: Agents write to memory/YYYY-MM-DD.md during sessions (structured format)
4. **Durable memory consolidation**: Pre-compaction flush, optional dreaming phase
5. **Embedding-backed hybrid search**: Fast semantic + keyword search over indexed file corpus
6. **File-focused tools**: memory_get() and memory_save() work with actual files, not DB rows

### 3.2 Target Data Flow
```
Agent turn (IAgentRunner)
  ↓
Agent calls memory_save("memory/YYYY-MM-DD.md", content)
  ↓
IMemoryStore.Append("memory/YYYY-MM-DD.md", content)
  ↓
File write (agent workspace: ~/.botnexus/agents/{name}/memory/YYYY-MM-DD.md)
  ↓
MemoryIndexer watches file changes → chunks files → embeddings
  ↓
SQLite FTS + embeddings indexed (derived)
  ↓
memory_get(file_path) reads directly from file (or queries index)
  ↓
memory_search(query) runs embedding + keyword search
```

### 3.3 Target File Structure
```
~/.botnexus/agents/{agent-name}/
├── SOUL.md                      (agent personality)
├── IDENTITY.md                  (role & context)
├── MEMORY.md                    (durable long-term memories, consolidated)
├── memory/
│   ├── 2026-05-07.md           (daily notes for May 7, 2026)
│   ├── 2026-05-06.md
│   └── 2026-05-05.md
└── (tools.md, etc. — per existing convention)
```

### 3.4 Daily Notes Format (Structured Markdown)
```markdown
# 2026-05-07 — Agent Session Notes

## Decisions Made
- Approved RFC-123: Use embeddings for semantic search
- Decided to delay Wave 3 pending performance analysis

## Tasks in Progress
- [ ] Implement MemoryIndexer file chunking
- [ ] Add embedding generation to memory pipeline

## Important Context
- User mentioned budget constraints for Q3 — affects roadmap
- Leela requested ADR template review before next merge

## Questions
- Should daily notes use frontmatter metadata (timestamps, topics)?
- How to handle agent-specific vs. shared memory?

## Tool Outputs (Reference)
- Ran build: passed (123 sec)
- Code review: 2 approvals, 1 request for changes
```

### 3.5 Memory File Access Rules
- **Direct file read/write**: `memory_save()`, `memory_get()` operate on actual files
- **Append-only daily notes**: Never overwrite; append to memory/YYYY-MM-DD.md
- **MEMORY.md is consolidated**: Only modified by consolidation (dreaming) phase, not during turns
- **Indexing is automatic**: File changes trigger re-indexing; index is always in sync or recovering

---

## 4. Proposed Staged Implementation

### Wave 1: File-First Authoring + Daily Note Loading (Foundation)
**Goal**: Switch agents to think about memory as files, not a database.

**Changes**:
- Update `memory_save()` tool definition to accept `file_path` parameter (e.g., "memory/YYYY-MM-DD.md")
- Implement `IMemoryStore.Append()` for file appending (not just Set/Get)
- Update `IContextBuilder` to load MEMORY.md + memory/YYYY-MM-DD.md into system prompt
- Add memory authoring instructions to generated AGENTS.md
- Create daily note file on first agent turn (auto-generated template if empty)

**Acceptance**:
- Agent receives memory authoring prompt in context
- Agent successfully writes to memory/YYYY-MM-DD.md via `memory_save()` tool
- Daily notes are visible in agent context at next turn
- Backward compatibility: old `memory_store` SQLite writes still work (redirect to file)

**Risk**: Agents may still use old `memory_save()` signature; need clear tool definition update.

---

### Wave 2: File-Based Indexing into SQLite/FTS (Search Foundation)
**Goal**: Index file contents (not conversation turns) into searchable store.

**Changes**:
- Refactor `MemoryIndexer` to chunk memory files instead of conversation turns
- Index chunks into SQLite FTS with file path + line-number metadata
- Implement `IMemorySearchEngine.Search(query)` to query FTS index
- Update `memory_search()` tool to use `IMemorySearchEngine` instead of raw SQLite
- FileSystemWatcher monitors memory/ directory for changes; triggers re-indexing on write
- Add backward-compat migration: bulk-index existing SQLite `memory_store` rows as pseudo-files

**Acceptance**:
- `memory_search(query)` returns results with file paths, line numbers, and context
- Searches work across MEMORY.md and all daily notes
- Index updates within 1-2 seconds of file write
- Index can be rebuilt from files in < 30 seconds per agent

**Risk**: Chunking strategy must preserve semantic meaning; verify search quality on real use cases.

---

### Wave 3: Embedding-Backed Hybrid Search (Semantic Foundation)
**Goal**: Add embeddings for semantic search; reduce keyword-only limitations.

**Changes**:
- Integrate embedding provider (`IEmbeddingProvider`) — local (e.g., Ollama) or cloud (e.g., OpenAI)
- Extend `MemoryIndexer` to generate embeddings for each chunk alongside FTS indexing
- Implement `IMemorySearchEngine.SearchSemantic(query, topK)` for embedding-based search
- Hybrid `memory_search()`: keyword results + semantic results + re-rank
- Store embeddings in SQLite (separate vectors table, normalized to [0,1])

**Acceptance**:
- `memory_search("user mentioned budget constraints")` returns relevant memories even if exact phrase differs
- Semantic search latency < 500ms for typical query
- Hybrid search balances keyword precision with semantic recall
- Embeddings table can be rebuilt from file chunks in < 2 minutes per agent

**Risk**: Embedding provider availability/cost; fallback to FTS-only if provider unavailable.

---

### Wave 4: Pre-Compaction Memory Flush (Agent-Aware Persistence)
**Goal**: Agents write memories before compaction; mirror OpenClaw's dreaming trigger.

**Changes**:
- Detect when session token count exceeds threshold (e.g., 80% of max)
- Before compaction: inject flush-prompt turn asking agent to persist memories
- Flush prompt tells agent to write to memory/YYYY-MM-DD.md with specific guidance
- Agent responds with NO_REPLY if nothing to save; otherwise writes memories
- Post-flush, proceed with normal compaction
- Track flush timestamps to avoid duplicate flushes

**Acceptance**:
- Agent receives flush prompt and writes context before compaction
- Compaction completes after flush
- No duplicate flushes within 30-minute window
- Session can resume with full context from memory files

**Risk**: Flush may consume tokens; ensure flush prompt is concise and impactful.

---

### Wave 5: Optional Dreaming & Consolidation (Maintenance)
**Goal**: Consolidate daily notes into MEMORY.md; optional architectural pattern for agents.

**Changes**:
- Optional: Trigger periodic consolidation job (e.g., weekly)
- Consolidation runs memory_consolidate(agent_name) system action
- Reads all memory/YYYY-MM-DD.md files from past week
- Summarizes into concise entries; appends to MEMORY.md with date range
- Deletes or archives old daily notes (configurable)
- Re-indexes memory store post-consolidation

**Acceptance**:
- Weekly consolidation reduces memory file count
- MEMORY.md grows but remains queryable
- Old daily notes archived or deleted
- Agents continue operating normally

**Risk**: Consolidation may lose nuance; consider manual review step or async logging.

---

## 5. Likely BotNexus Files/Projects to Inspect/Change

### Core Memory Services
- **`src/core/abstractions/IMemoryStore.cs`** — Extend with `Append()` method
- **`src/agent/MemoryStore.cs`** — Implement Append for files; ensure cross-platform paths
- **`src/core/abstractions/IMemorySearchEngine.cs`** — New interface (if not exists)
- **`src/agent/MemoryIndexer.cs`** — Refactor to index files, not conversation turns (Waves 2–3)
- **`src/core/abstractions/IEmbeddingProvider.cs`** — New abstraction (Wave 3)

### Context & Prompt Building
- **`src/agent/AgentContextBuilder.cs`** — Load MEMORY.md + daily notes into context (Wave 1)
- **`src/core/abstractions/IContextBuilder.cs`** — Ensure interface supports memory file loading

### Memory Tools
- **`src/tools/memory/MemorySaveTool.cs`** — Update to accept file_path parameter (Wave 1)
- **`src/tools/memory/MemoryGetTool.cs`** — Read from files, not just SQLite (Wave 2)
- **`src/tools/memory/MemorySearchTool.cs`** — Use `IMemorySearchEngine` (Wave 2)
- **`src/tools/memory/MemoryConsolidateTool.cs`** — New tool for dreaming (Wave 5)

### Agent Workspace
- **`src/agent/AgentWorkspaceManager.cs`** (if exists) — Ensure MEMORY.md + memory/ directory created
- **`src/agent/IAgentWorkspace.cs`** — Interface for workspace file access (per decisions.md 4.3)

### Configuration & Session
- **`src/core/config/BotNexusConfig.cs`** — Add `MemoryConfig` section (embeddings provider, indexing settings)
- **`src/session/SessionManager.cs`** — Token threshold tracking for flush trigger (Wave 4)
- **`src/agent/AgentLoop.cs`** — Inject flush-prompt turn before compaction (Wave 4)

### Testing
- **`tests/agent/MemoryStoreTests.cs`** — Test file append, cross-platform paths, daily note structure
- **`tests/agent/MemoryIndexerTests.cs`** — Test file chunking, FTS indexing
- **`tests/tools/MemorySaveToolTests.cs`** — Test tool with file_path parameter
- **`tests/agent/AgentContextBuilderTests.cs`** — Test MEMORY.md loading

### Optional: Index & Schema
- **`src/database/Migrations/`** — SQLite schema update for embeddings table (Wave 3)
- **`docs/concepts/memory-model.md`** — Document final memory architecture

---

## 6. Migration and Backward-Compatibility Notes

### Phase 1: Soft Launch (Waves 1–2)
- Old `memory_save()` calls (no file_path) default to memory/YYYY-MM-DD.md
- Existing SQLite memory_store rows are migrated to memory/legacy/ files (one-time)
- `memory_search()` queries both old SQLite index and new file-based index
- Agents gradually learn new file-aware pattern via updated AGENTS.md

### Phase 2: File-Primary (Wave 3+)
- SQLite indexes become derived-only (no direct writes)
- Tools exclusively read/write files; index is auto-rebuilt
- Legacy agent configs still supported but logged as deprecated

### Phase 3: Cleanup
- Remove SQLite memory_store table after 2–3 release cycles
- Update documentation to reflect Markdown-first model
- Provide migration script for external agents using old schema

### Backward Compatibility
- Old tool signatures (`memory_save(text)` without file_path) still accepted
- Redirect to current date file (memory/YYYY-MM-DD.md)
- Log deprecation warning once per agent per session
- All existing tests updated to mock file-based store

---

## 7. Open Questions for Sytone to Review

1. **Embedding Provider**: Should embeddings be optional at launch? Which provider(s) to support in Wave 3?
   - Local (Ollama, LiteLLM) — lower cost, offline capable
   - Cloud (OpenAI, Anthropic embeddings) — higher quality, but API dependencies

2. **Daily Notes Format**: Should daily notes use YAML frontmatter (for topics, sentiment, keywords)?
   - Pro: Structured metadata enables better indexing
   - Con: Added complexity; free-form markdown is simpler for agents

3. **Consolidation Trigger**: Should dreaming (Wave 5) be:
   - Automatic (weekly cron job)?
   - Manual (admin trigger)?
   - Agent-initiated (agent asks to consolidate)?

4. **File Storage Path**: Confirm ~/.botnexus/agents/{name}/memory/ is the canonical location?
   - Should it be configurable per agent (multi-workspace support)?

5. **Index Rebuild Cost**: Is < 30s rebuild time per agent acceptable for initial launches?
   - Should we cache embeddings across sessions to avoid re-computing?

6. **Scope**: Should this alignment also update AGENTS.md generation to include memory authoring instructions?
   - How detailed should memory instructions be?

---

## 8. Acceptance Criteria

### Wave 1: File-First Authoring
- [ ] Agent receives memory instructions in system prompt
- [ ] `memory_save(file_path, content)` tool accepts file path parameter
- [ ] Daily note file (memory/YYYY-MM-DD.md) is created on first agent turn
- [ ] Agent can write to daily note and read it back in next turn
- [ ] Backward-compat: `memory_save(content)` still works (redirects to current date file)
- [ ] Agent context includes MEMORY.md + recent daily notes (last 7 days)
- [ ] Unit tests for file append, context loading, tool parameter parsing pass

### Wave 2: File-Based Indexing
- [ ] MemoryIndexer chunks memory files into semantic units
- [ ] Chunks indexed into SQLite FTS with file path + line metadata
- [ ] FileSystemWatcher triggers re-indexing on file write (latency < 2s)
- [ ] `memory_search(query)` returns results with file paths and context
- [ ] Search results include old SQLite data (migrated) and new file data
- [ ] Index rebuild < 30s per agent
- [ ] Unit tests for chunking, FTS indexing, search quality pass

### Wave 3: Embedding-Backed Search
- [ ] `IEmbeddingProvider` interface implemented for chosen provider
- [ ] Embeddings generated and stored for all file chunks
- [ ] Hybrid `memory_search()` returns keyword + semantic results
- [ ] Semantic search latency < 500ms for typical query
- [ ] Embedding fallback: if provider unavailable, search uses FTS-only (no errors)
- [ ] Embeddings table can be rebuilt in < 2 minutes per agent

### Wave 4: Pre-Compaction Flush
- [ ] Session token counter tracks % of max tokens
- [ ] Flush trigger fires when token count > 80% of max
- [ ] Flush prompt injected as system turn before compaction
- [ ] Agent successfully writes memories to daily note during flush
- [ ] Post-flush, compaction proceeds normally
- [ ] No duplicate flushes within 30-minute window
- [ ] Flush does not error if agent replies NO_REPLY

### Wave 5: Optional Consolidation
- [ ] `memory_consolidate(agent_name)` system action reads past week's daily notes
- [ ] Consolidation summarizes and appends to MEMORY.md
- [ ] Old daily files archived or deleted (configurable)
- [ ] Re-indexing completes post-consolidation
- [ ] Agents continue operating after consolidation (no interruption)

### All Waves
- [ ] Integration tests verify full workflows (authoring → indexing → search → compaction)
- [ ] Cross-platform tests for file I/O (Windows, Linux, macOS)
- [ ] Documentation updated with new memory model, tool definitions, and examples
- [ ] No regressions in existing memory features
- [ ] Performance metrics (latency, disk usage) meet targets

---

## 9. Next Steps

1. **Sytone Review**: Address open questions (embedding provider, daily format, consolidation trigger)
2. **Implementation Kickoff**: Squad assigns Wave 1 to developer(s); Kif updates documentation
3. **Spec Refinement**: Iterate on tech debt, edge cases, schema changes
4. **ADR**: Consider proposing ADR for "Markdown-first memory model" as architectural decision
5. **Proof of Concept**: Implement Wave 1 in isolated branch; validate agent behavior with test suite
6. **Phased Rollout**: Deploy waves incrementally; gather feedback after each wave

---

## 10. Related Documents

- **Decisions**: `.squad/decisions.md` — OpenClaw memory research (Kif + Leela, 2026-05-07)
- **Related Spec**: `docs/planning/improvement-memory-lifecycle/design-spec.md` — Pre-compaction flush (foundation for Wave 4)
- **Reference**: OpenClaw memory documentation (external) — https://github.com/mrfrunze/openclaw/docs/memory
- **Agent Workspace**: `docs/planning/` — Existing specs on agent configuration, context building

---

**Document Metadata**:
- **Created**: 2026-05-07 (Kif, Documentation Engineer)
- **Last Updated**: 2026-05-07
- **Reviewers**: Sytone (Product), Leela (Architecture), Bender (Backend)
- **Status**: Ready for review and planning discussion
