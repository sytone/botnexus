# Decision Review: Memory Tool Surface Fix

**Reviewer:** Leela (Lead/Architect)
**Date:** 2025-07-09
**Branch:** feature/openclaw-memory-alignment
**Boundary Decision:** ea24971b
**Fix Commits:** 7a4785a6, 553eaf24

## Verdict: ✅ APPROVED

## Findings

### 1. MemoryStoreTool removed from agent-facing surface
- **File deleted:** `src/gateway/BotNexus.Memory/Tools/MemoryStoreTool.cs` (167 lines)
- **Registration removed:** `InProcessIsolationStrategy.cs` no longer adds `MemoryStoreTool`
- **Zero references** to `MemoryStoreTool` or `memory_store` remain in production code (`src/`)

### 2. Agent-facing write tool is exclusively `memory_save`
- `MemorySaveTool` remains the sole write path, registered as `memory_save`
- Contract test (`MemorySaveToolContractTests`) validates name, label, and schema

### 3. Guard tests are adequate
- `CreateAsync_WithMemoryEnabled_ExposesMemorySaveAsCanonicalAgentTool` — positive assertion
- `CreateAsync_WithMemoryEnabled_DoesNotExposeSeparateMemoryStoreTool` — regression guard
- `CreateAsync_WithMemoryEnabled_AgentFacingToolDescriptionsAvoidMemoryStoreTerminology` — description hygiene

These tests prevent accidental re-introduction without preserving the old tool as visible compatibility.

### 4. Docs alignment
- Only reference to `memory_store` is in `docs/planning/archived/` (historical research table) — appropriate for archival context, no action needed.

## Remaining References (acceptable)
| Location | Purpose | Action |
|----------|---------|--------|
| Guard test name/assertion | Prevents regression | Keep |
| Archived planning doc | Historical record | Keep |

## Summary
The fix cleanly implements the boundary decision: `MemoryStoreTool` is deleted, `memory_save` is the single canonical agent-facing write tool, and guard tests prevent drift. No blockers.
