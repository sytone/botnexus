# Consistency Review: Memory Wave 1 — Planning Spec Contradiction

**Author:** Nibbler (Consistency Reviewer)
**Date:** 2026-07-25
**Scope:** `docs/planning/improvement-memory-lifecycle/design-spec.md`

## Issue

The memory lifecycle design spec (status: Draft) contains a Phase 2b instruction that contradicts the resolved Wave 1 contract:

> **Phase 2b — End-of-Conversation Persistence** (line 118):
> "Update MEMORY.md if significant long-term items were discussed"

The resolved contract states:
- `MEMORY.md` is **read-only** during normal agent turns.
- Only future Wave 5 consolidation (dreaming) writes to `MEMORY.md`.
- Normal-turn `memory_save` writes exclusively to daily notes under `memory/`.

## Why This Needs a Decision

The design spec is a planning artifact, not implementation docs, so I did not modify it. However:

1. Phase 2b is labeled "No Platform Changes" — implying agents should do this **now** with existing tools. But `memory_save` blocks path traversal outside `memory/`, so agents cannot write to `MEMORY.md` via the tool.
2. A future implementer reading Phase 2b could build a bypass or weaken the path guard.
3. The spec should either:
   - **(a)** Align Phase 2b with the resolved contract (remove the MEMORY.md write instruction, keep daily-note writes only), or
   - **(b)** Defer MEMORY.md writes to Phase 4 (Dreaming) where they already belong.

## Recommendation

Option (b): Move the "Update MEMORY.md" bullet from Phase 2b to Phase 4 (Dreaming). Phase 2b should only mention writing to daily notes.

## What Was Fixed (Safe Fixes Already Committed)

- `docs/development/workspace-and-memory.md`: Removed false claim that `memory_save` can write to `MEMORY.md` via a `target` parameter. Clarified consolidation is a future Wave 5 capability. Fixed non-existent `target` param reference (actual param is `file_path`).
