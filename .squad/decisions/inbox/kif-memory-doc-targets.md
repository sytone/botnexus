### Memory Alignment — Documentation Update Map (Wave 1)

**Decision Date:** 2026-05-07  
**Decided By:** Kif (Documentation Engineer)  
**Status:** Proposed — awaiting implementation landing  

---

## Context

The `improvement-memory-lifecycle` design spec introduces pre-compaction memory flush, session-end flush, and new config fields. Multiple existing docs reference the current memory model (file-only, no flush triggers) and will become stale once Wave 1 lands. This decision documents what docs must change and in what order.

## Docs Requiring Updates (Wave 1 Scope)

| Doc | What changes | Priority |
|-----|-------------|----------|
| `docs/development/workspace-and-memory.md` | Add §Memory Flush (pre-compaction + session-end triggers), update §Memory Consolidation to reference automatic flush, add config keys (`compaction.memoryFlush.*`), note that flush is best-effort | P0 |
| `docs/user-guide/configuration.md` | Add `compaction.memoryFlush` to Gateway Settings Reference table, add annotated JSON example | P0 |
| `docs/configuration.md` | Mirror the new compaction keys in the developer-facing config guide | P1 |
| `docs/user-guide/agents.md` | Update §Memory System to mention automatic flush; replace the minimal `"memory": { "enabled": true }` example with richer config showing flush settings | P1 |
| `docs/development/session-stores.md` | Add note that session metadata now carries `memoryFlushAt` / `memoryFlushCompactionCount` fields | P1 |
| `docs/cron-and-scheduling.md` | Reference that flush is NOT a cron job — it's inline before compaction. Distinguish from future dreaming cron. | P2 |
| `docs/architecture/system-flows.md` | Add memory-flush to the compaction flow diagram (if applicable) | P2 |

## New Docs to Create

| Doc | Purpose |
|-----|---------|
| _(none for Wave 1)_ | Existing docs cover the right topics; new sections within them are sufficient |

## Stale References to Fix

These currently describe the memory model as file-only / manual-only:

1. **`workspace-and-memory.md` line ~572**: "Currently, consolidation is manual" — must be updated to acknowledge the automatic pre-compaction flush as a complementary path.
2. **`workspace-and-memory.md` §Memory Model**: No mention of flush triggers — reads as though the only write path is `memory_save` tool.
3. **`user-guide/agents.md` §Memory System**: Minimal two-line example; says nothing about flush or when persistence actually happens.
4. **`research.md` line ~100-108**: "BotNexus is MISSING: Pre-compaction memory flush" — once implemented, this file should be left as-is (historical research) but the design-spec status must update to `in-progress` or `delivered`.

## Stale References to SQLite-Primary Memory

The following docs reference `sessions.sqlite` or `SqliteMemoryStore` as the storage layer for memory. After Wave 1, memory persistence is primarily **file-based** (daily markdown notes), with SQLite used only as a search index. Docs must clarify this distinction:

1. **`workspace-and-memory.md` §Separation of Concerns (line 34)**: "Distinct from session data in `~/.botnexus/sessions.sqlite`" — accurate but incomplete; should note that `SqliteMemoryStore` is a search index, not the source of truth for memory content.
2. **`user-guide/agents.md` line ~315**: "Session history is preserved in `~/.botnexus/sessions.sqlite`" — correct for sessions but readers may conflate session store with memory store.
3. **`session-stores.md`**: Covers session persistence only — no confusion, but should cross-link to workspace-and-memory.md for memory persistence (currently no link).

## Examples Needed

Once implementation lands, add these examples:

1. **Config example** — Full annotated `compaction.memoryFlush` block in config docs
2. **Flush prompt example** — Show the injected system prompt during flush turn
3. **Daily note output** — Show what a flush writes to `memory/YYYY-MM-DD.md`
4. **Safety guard behavior** — Example of flush attempting to write SOUL.md and being blocked

## Acceptance Criteria for Docs Consistency

- [ ] All compaction config keys in code (`MemoryFlushConfig`) have corresponding entries in both config docs
- [ ] `workspace-and-memory.md` describes the full write path: manual (`memory_save`) + automatic (pre-compaction flush) + session-end flush
- [ ] No doc claims memory persistence is "manual only" after Wave 1 lands
- [ ] AGENTS.md auto-generation behavior documented accurately (it remains unchanged by this feature)
- [ ] Cross-links exist between: workspace-and-memory ↔ configuration ↔ session-stores
- [ ] Design spec `improvement-memory-lifecycle` status updated from `draft` to match implementation state
- [ ] Planning INDEX.md reflects current status

## Decision

Kif will prepare doc updates in a single batch PR after Wave 1 implementation merges. No doc changes before code lands (to avoid drift). The update targets above serve as the checklist.
