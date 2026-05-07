# Architecture Re-Review: Wave 1 Memory Alignment — Farnsworth Remediation

**Review Date:** 2026-05-07
**Reviewer:** Leela (Lead/Architect)
**Original Rejection:** leela-memory-wave1-review.md (2026-05-08 — future-dated in original)
**Remediation Commit:** 58d03d13 fix(gateway): centralize memory save path handling
**Branch:** feature/openclaw-memory-alignment
**Verdict:** ✅ APPROVE — both blocking issues resolved

---

## B1 Resolution: MemorySaveTool delegates to IAgentWorkspaceManager ✅

**Status:** RESOLVED

The `MemorySaveTool` (101 lines total) is now a thin delegation wrapper:

- **No filesystem operations.** Zero calls to `File.Append`, `File.Write`, `Directory.Create`, `Path.Combine`, `Path.GetFullPath`, or `Path.IsPathRooted` in `MemorySaveTool.cs`. Confirmed by grep — only `SqliteMemoryStore` has filesystem calls in `BotNexus.Memory`.
- **Single delegation point.** `ExecuteAsync` (line 86) calls `_workspaceManager.SaveMemoryAsync(_agentId, filePath, content, _memoryPathOverride, cancellationToken)` — one line, no path logic.
- **Override support centralized.** The `memoryPathOverride` parameter flows from `MemorySaveTool` constructor → `SaveMemoryAsync` → `FileAgentWorkspaceManager.ResolveMemoryRoot`, which handles workspace-relative resolution, traversal validation, and `.md` suffix detection.
- **All path/write logic in `FileAgentWorkspaceManager`.** Path resolution (`ResolveMemoryRoot`, `ResolveMemoryPath`), traversal safety (`EnsureWithinRoot`), directory creation, and append semantics are exclusively in the workspace manager.
- **Interface contract extended cleanly.** `IAgentWorkspaceManager` now has three `SaveMemoryAsync` overloads: legacy (agentName, content), file-path (agentName, filePath, content), and full (agentName, filePath, content, memoryPathOverride). Each has proper XML doc comments. The implementation chains them correctly.

**Test coverage:** `MemorySaveToolTests` (N1 from prior review) now exists with three tests confirming:
1. Content-only calls delegate with null filePath and override passes through
2. FilePath argument passes through without rewriting
3. Tool never calls `GetWorkspacePath` — it fully delegates save operations (spy throws if called)

---

## B2 Resolution: Dead contract data removed, single canonical daily-note source ✅

**Status:** RESOLVED (Option A implemented as recommended)

- **`DailyMemoryNote` record type:** Deleted from Contracts. Zero references remain anywhere in the codebase (confirmed by grep — no `class DailyMemoryNote` or `record DailyMemoryNote` matches).
- **`AgentWorkspace.RecentMemoryNotes` property:** Removed. Zero references remain (confirmed by grep). `AgentWorkspace` now has only the five original properties: `AgentName`, `Soul`, `Identity`, `User`, `Memory`.
- **`FileAgentWorkspaceManager.LoadRecentDailyNotesAsync`:** Deleted. No `LoadRecentDailyNotes` method exists anywhere in `src/` (confirmed by grep).
- **Single canonical path:** `WorkspaceContextBuilder.LoadRecentDailyMemoryFilesAsync` (lines 137–175) is the sole daily-note loading implementation. It respects `descriptor.Memory?.Path` override via `ResolveMemoryRoot` (lines 177–194).
- **Override-aware loading tested.** `WorkspaceContextBuilderTests.BuildSystemPromptAsync_DefaultPrompt_LoadsRecentDailyMemoryFilesFromOverridePathInDeterministicOrder` (lines 150–195) confirms override path loads from the custom directory, excludes the default `memory/` directory, and maintains deterministic ordering (MEMORY.md → yesterday → today).

---

## Additional Quality Checks

### Path Safety ✅

- `FileAgentWorkspaceManager.EnsureWithinRoot` validates both `memoryPathOverride` and `filePath` against workspace boundaries using canonicalized paths.
- `WorkspaceContextBuilder.ResolveMemoryRoot` independently validates override paths and falls back to `memory/` if traversal is detected.
- Both implementations use `IFileSystem.Path` APIs for cross-platform correctness.
- Rooted path rejection: both `ResolveMemoryRoot` (manager) and `ResolveMemoryPath` check `IsPathRooted` and throw.

### Wave 1 Scope Discipline ✅

- No dreaming, compaction, semantic search, or session-end triggers introduced.
- No premature abstractions beyond Wave 1 requirements.
- `DailyMemoryNote` dead weight removed — cleaner than the original.

### Public API XML Docs ✅

- `IAgentWorkspaceManager`: All three `SaveMemoryAsync` overloads have descriptive XML docs explaining contract semantics (especially the `memoryPathOverride` parameter).
- `MemorySaveTool`: Class-level `<summary>` describes purpose.
- `AgentWorkspace`: Clean record with per-property docs.
- `GetWorkspacePath`: Contract documented.

### Dependency Boundaries ✅

- `BotNexus.Memory` (`MemorySaveTool`) depends on `Gateway.Abstractions` (interface) — not `Gateway` (implementation). Correct per `src/gateway/AGENTS.md`.
- `BotNexus.Gateway` (`FileAgentWorkspaceManager`, `WorkspaceContextBuilder`) stays within allowed deps (Agent, Domain, Gateway.Prompts).
- No circular dependencies introduced.

---

## Non-Blocking Conditions for Hermes/Kif

### C1: DateTime consistency (carried from N3)

`WorkspaceContextBuilder.LoadRecentDailyMemoryFilesAsync` uses `DateTime.Now.Date` (line 147) while `FileAgentWorkspaceManager.ResolveMemoryPath` uses `DateTime.UtcNow` (line 104). This is the same N3 inconsistency from the original review. Low risk for Wave 1 (only diverges near midnight UTC), but should be standardized on one choice before Wave 2.

**Action:** Hermes or Kif should align these to one consistent date source in a follow-up.

### C2: Missing ContextFileOrdering daily note tests (carried from N2)

`ContextFileOrdering.IsDailyMemoryNote` and priority-75 ordering still lack dedicated test coverage.

**Action:** Hermes should add `ContextFileOrdering_DailyNoteTests.cs`.

### C3: 4000-char daily note budget (carried from N4)

Neither loading path enforces the spec's 4000-char combined budget. Low risk (bounded to 2 files), but should be tracked for Wave 2.

---

## Test Stub Compliance ✅

All test stubs (`SpyWorkspaceManager` in `MemorySaveToolTests`, `StubWorkspaceManager` in `WorkspaceContextBuilderTests`) implement the full `IAgentWorkspaceManager` interface including the new three-overload `SaveMemoryAsync` signature. No compilation issues from the contract change.

`FileAgentWorkspaceManagerTests` includes override coverage (`SaveMemoryAsync_WithMemoryPathOverride_AppendsToOverrideLocation`) confirming end-to-end path resolution and file write through the centralized implementation.

---

## Summary

Farnsworth's remediation cleanly addresses both blocking issues. `MemorySaveTool` is now a ~15-line delegation wrapper with zero filesystem logic. The dead `DailyMemoryNote`/`RecentMemoryNotes` contract surface is fully removed, and daily-note loading has one canonical override-aware path in `WorkspaceContextBuilder`. Path safety, dependency boundaries, and public API docs all pass. Three non-blocking conditions (C1–C3) are carried forward for Hermes/Kif.

**Verdict: APPROVE for merge.** Bender lockout remains in effect — this review covers only Farnsworth's remediation commit.
