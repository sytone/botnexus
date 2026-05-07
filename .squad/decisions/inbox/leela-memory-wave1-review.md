# Architecture Review: Wave 1 Memory Alignment Implementation

**Review Date:** 2026-05-08
**Reviewer:** Leela (Lead/Architect)
**Commit Range:** e21e9e38..494804c8
**Branch:** feature/openclaw-memory-alignment
**Verdict:** ❌ REJECT — two blocking issues require remediation before merge

---

## Summary

Bender delivered the full Wave 1 scope (1A contracts, 1B tool, 1C context integration) in a single implementation commit. The scope is correct for Wave 1 — no premature Wave 2/3 abstractions were introduced. Path traversal validation, append-only semantics, and cross-platform path handling are all present. However, the implementation has two SOLID/boundary violations that create maintenance hazards and must be fixed.

---

## Blocking Issues

### B1: MemorySaveTool bypasses IAgentWorkspaceManager — DIP + DRY violation

**Severity:** HIGH
**Files:** `MemorySaveTool.cs`, `FileAgentWorkspaceManager.cs`

The architecture decision (leela-memory-wave1-slice.md §5) states: *"Tool calls `IAgentWorkspaceManager.SaveDailyNoteAsync`"* (later renamed to the `SaveMemoryAsync` overload).

Instead, `MemorySaveTool.ExecuteAsync` (line 79–103) does its own filesystem writing:
- Resolves memory root from workspace path + override (~40 lines of path logic)
- Validates traversal
- Creates directories
- Appends content directly via `_fileSystem.File.AppendAllTextAsync`

This duplicates all of the path resolution, normalization, traversal validation, and file append logic already in `FileAgentWorkspaceManager.SaveMemoryAsync(agentName, filePath, content)`.

**Root cause:** `FileAgentWorkspaceManager.SaveMemoryAsync` doesn't accept or honor `MemoryAgentConfig.Path` override. Since the tool needs override support, it reimplemented everything.

**Required fix:**
1. Pass the memory path override into `FileAgentWorkspaceManager` (via construction or the `SaveMemoryAsync` method signature)
2. `MemorySaveTool.ExecuteAsync` should call `_workspaceManager.SaveMemoryAsync(_agentId, filePath, content)` — roughly 5 lines, not 100
3. All path resolution, traversal validation, and file writing logic lives exclusively in the workspace manager

### B2: Daily note loading duplicated with behavioral divergence

**Severity:** HIGH
**Files:** `FileAgentWorkspaceManager.cs`, `WorkspaceContextBuilder.cs`, `AgentWorkspace.cs`

Two independent implementations load daily notes:

| Location | Respects `memory.path` override? | Output type |
|----------|----------------------------------|-------------|
| `FileAgentWorkspaceManager.LoadRecentDailyNotesAsync` | ❌ No — always uses `workspace/memory/` | `DailyMemoryNote` list on `AgentWorkspace.RecentMemoryNotes` |
| `WorkspaceContextBuilder.LoadRecentDailyMemoryFilesAsync` | ✅ Yes — resolves from `descriptor.Memory?.Path` | `ContextFile` list added to prompt |

The `AgentWorkspace.RecentMemoryNotes` property is **populated but never consumed** — zero references read it. The prompt builder does its own loading. This means:
- The `DailyMemoryNote` record type in Contracts is dead weight
- The `LoadRecentDailyNotesAsync` method in `FileAgentWorkspaceManager` is dead code
- If someone later reads `RecentMemoryNotes` expecting it to match the prompt, they'll get different results (no override support)

**Required fix — pick ONE canonical path:**
- **Option A (recommended):** Remove `RecentMemoryNotes` from `AgentWorkspace` and `DailyMemoryNote` from Contracts. The `WorkspaceContextBuilder` owns daily note loading for prompt context. The workspace manager is not the right layer for this (it doesn't have access to agent config).
- **Option B:** Move the override-aware loading into `FileAgentWorkspaceManager` (requires injecting config), remove the duplicate from `WorkspaceContextBuilder`, and have the prompt builder consume `workspace.RecentMemoryNotes`.

---

## Non-Blocking Observations

### N1: Missing dedicated MemorySaveTool tests

The spec (Wave 1D) calls for `MemorySaveToolTests.cs` covering tool happy path, append semantics, and path validation. No such test file exists. This was assigned to Hermes and may be in progress — but after B1 is fixed the tool will be ~15 lines and the test surface will be primarily on the workspace manager (which does have tests).

**Action:** Hermes should create `MemorySaveToolTests.cs` after B1 remediation.

### N2: Missing ContextFileOrdering daily note tests

The spec calls for `ContextFileOrdering_DailyNoteTests.cs`. The `IsDailyMemoryNote` logic and priority-75 ordering have no dedicated test coverage.

**Action:** Hermes should add these tests.

### N3: DateTime.Now vs DateTimeOffset.UtcNow inconsistency

The spec says *"Date for 'today' comes from DateTimeOffset.UtcNow"*. The implementation uses `DateTime.Now.Date` (local time) in three places:
- `FileAgentWorkspaceManager.cs:76`
- `FileAgentWorkspaceManager.cs:109`
- `WorkspaceContextBuilder.cs:147`
- `MemorySaveTool.cs:136`

For Wave 1 this is acceptable (spec says "keep it simple"), but the local vs UTC choice should be consistent. Recommend standardizing on `DateTime.UtcNow` across all four sites in the remediation pass.

### N4: Missing 4000-char daily note budget

The spec (Wave 1C boundary rules) requires: *"Total daily note budget: 4000 chars combined (truncate oldest if over)."* Neither `WorkspaceContextBuilder` nor `FileAgentWorkspaceManager` enforces this limit. Low risk for now (bounded to 2 files), but should be tracked.

### N5: AGENTS.md template update is appropriate scope

The minimal Memory Notes section added to `Templates/AGENTS.md` is exactly right for Wave 1 — sufficient guidance without over-engineering.

### N6: Public API documentation is adequate

All new public members (`DailyMemoryNote`, `RecentMemoryNotes`, `SaveMemoryAsync` overload, `MemorySaveTool`, `MemoryAgentConfig.Path`) have XML doc comments. The `MemorySaveTool` class-level doc is concise but adequate. The interface method docs describe the contract correctly.

---

## Dependency Boundary Assessment

| Boundary | Status | Notes |
|----------|--------|-------|
| `Gateway.Contracts` → `Domain` only | ✅ Pass | `DailyMemoryNote` in Contracts has no Domain dependency |
| `BotNexus.Memory` → `Agent, Gateway.Contracts, Domain` | ✅ Pass | `MemorySaveTool` correctly references `IAgentWorkspaceManager` from Gateway.Contracts |
| `BotNexus.Gateway` → `Agent, Domain, Gateway.Prompts` | ✅ Pass | `WorkspaceContextBuilder` and `FileAgentWorkspaceManager` stay within allowed deps |
| `Gateway.Prompts` → `Domain` only | ✅ Pass | `ContextFileOrdering` has no new dependencies |
| No circular deps | ✅ Pass | `MemorySaveTool` depends on Contracts (interface), not Gateway (impl) |

---

## Cross-Platform Path Handling Assessment

- ✅ `IFileSystem.Path.Combine` used consistently in production code
- ✅ `Path.GetFullPath` for canonicalization before traversal checks
- ✅ Backslash-to-forward-slash normalization for display paths
- ✅ `DirectorySeparatorChar` + `AltDirectorySeparatorChar` in traversal prefix checks
- ⚠️ `NormalizeRelativePath` in `MemorySaveTool` checks both `/` and `\\` — correct but will be eliminated by B1 fix

---

## Remediation Assignment

Per strict lockout: Bender cannot revise rejected work.

| Fix | Assignee | Rationale |
|-----|----------|-----------|
| B1: Collapse MemorySaveTool to delegate through workspace manager | Farnsworth | Owns Wave 1A contracts + workspace implementation; needs to add override support to `SaveMemoryAsync` |
| B2: Eliminate dual daily-note loading path | Farnsworth | Owns Wave 1A workspace + Wave 1C context integration boundary |
| N1–N2: Missing tests | Hermes | Owns Wave 1D test coverage |
| N3: DateTime consistency | Farnsworth | Part of B1/B2 remediation pass |

---

## Wave 1 Scope Compliance

- ✅ No dreaming, no compaction flush, no semantic search of daily notes
- ✅ No session-end triggers
- ✅ File-first authoring correctly separated from SQLite retrieval (`memory_save` vs `memory_store`)
- ✅ No premature interfaces or abstractions beyond what Wave 1 needs
- ⚠️ `AgentWorkspace.RecentMemoryNotes` + `DailyMemoryNote` in Contracts is premature if B2/Option A is chosen — remove in remediation
