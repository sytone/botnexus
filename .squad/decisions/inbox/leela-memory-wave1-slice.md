# Decision: Memory Alignment Wave 1 — Implementation Slice

**Decision Date:** 2026-05-07
**Decided By:** Leela (Lead/Architect)
**Status:** Ready for implementation

## Context

The `improvement-memory-lifecycle` spec describes the full OpenClaw memory alignment. This decision defines the smallest complete implementation slice for Wave 1 — file-based daily notes with a new `memory_save` tool, daily note loading into agent context, and optional per-agent memory path override. No dreaming, no pre-compaction flush, no semantic search changes.

## Resolved Decisions (from spec + discussion)

1. **Plain Markdown daily notes only** — `memory/YYYY-MM-DD.md` files, no SQLite for daily notes
2. **Default canonical memory path** — `{workspace}/memory/` with optional per-agent override via config (`agents.<id>.memory.path`)
3. **Tool signature** — `memory_save(file_path, content)` for explicit path + backward-compatible `memory_save(content)` which defaults to `memory/YYYY-MM-DD.md`
4. **Context loading** — `MEMORY.md` + most recent `memory/YYYY-MM-DD.md` (today + yesterday) loaded into agent context at session start
5. **AGENTS.md generation** — If agent AGENTS.md generation exists, append minimal memory instructions section

## Architecture Decisions

### 1. `memory_save` is a NEW tool, not a rename of `memory_store`

`memory_store` writes to the SQLite semantic memory store (embeddings, tags, TTL). `memory_save` writes to the flat-file workspace daily notes. Both coexist. Different purposes:
- `memory_store` → structured recall via `memory_search`
- `memory_save` → human-readable daily notes loaded into context window

### 2. `memory_save` lives in `BotNexus.Memory` project (Tools/ subdirectory)

It writes to the agent's workspace filesystem. It needs `IAgentWorkspaceManager` (from Gateway.Abstractions) to resolve paths. This follows the same pattern as `MemoryStoreTool`.

### 3. Daily note loading is a workspace concern — extends `FileAgentWorkspaceManager`

The `AgentWorkspace` record gets a new property: `RecentMemoryNotes` (list of dated note content). `FileAgentWorkspaceManager.LoadWorkspaceAsync` reads `memory/` directory for today + yesterday's files. The `ContextFileOrdering` already handles `memory.md` at priority 70; daily notes append immediately after.

### 4. Memory path override is config-driven

`MemoryAgentConfig` gains an optional `Path` property. When set, `FileAgentWorkspaceManager` uses it instead of `{workspace}/memory/`. Resolved via `AgentDescriptor.Memory.Path`.

### 5. No interface changes to `IAgentWorkspaceManager`

The daily note loading is an internal enhancement to `LoadWorkspaceAsync`. The existing `SaveMemoryAsync` remains (it writes to MEMORY.md). The new tool calls the workspace manager with a file-path-aware method — add `SaveDailyNoteAsync(agentName, filePath, content)` as a new method on the concrete class first, promoted to interface only if extensions need it.

**Correction:** Actually, the tool needs a contract to call. Add to `IAgentWorkspaceManager`:
```csharp
Task SaveDailyNoteAsync(string agentName, string? filePath, string content, CancellationToken ct = default);
```
- `filePath` null → defaults to `memory/YYYY-MM-DD.md` (today)
- `filePath` relative → resolved relative to memory directory
- Always appends, never overwrites

## Wave 1 Breakdown — Agent Assignments

### Wave 1A: Contracts + Workspace (Farnsworth) — ~2h

**Files to create/modify:**

| File | Change |
|------|--------|
| `src/gateway/BotNexus.Gateway.Contracts/Agents/IAgentWorkspaceManager.cs` | Add `SaveDailyNoteAsync` method |
| `src/gateway/BotNexus.Gateway.Contracts/Agents/AgentWorkspace.cs` | Add `RecentMemoryNotes` property (list of `(string Date, string Content)`) |
| `src/gateway/BotNexus.Gateway/Agents/FileAgentWorkspaceManager.cs` | Implement `SaveDailyNoteAsync` + enhance `LoadWorkspaceAsync` to read recent daily notes |
| `src/domain/BotNexus.Domain/Gateway/Models/MemoryAgentConfig.cs` | Add `Path` property (nullable string) |

**Boundary rules:**
- `SaveDailyNoteAsync` creates the `memory/` directory if absent
- Append-only semantics: never truncates existing file
- Date for "today" comes from `DateTimeOffset.UtcNow` (not injected clock for v1 — keep it simple)
- File path validation: must be within memory directory (no `../` traversal)
- Read at most 2 daily note files (today + yesterday) — bounded context cost

### Wave 1B: `memory_save` Tool (Bender) — ~1.5h

**Files to create/modify:**

| File | Change |
|------|--------|
| `src/gateway/BotNexus.Memory/Tools/MemorySaveTool.cs` | **NEW** — implements `IAgentTool` with name `memory_save` |
| `src/gateway/BotNexus.Gateway/Tools/DefaultToolRegistry.cs` | Register `MemorySaveTool` alongside existing memory tools |

**Tool contract:**
```json
{
  "type": "object",
  "properties": {
    "content": { "type": "string", "description": "Content to append to the daily note" },
    "file_path": { "type": "string", "description": "Optional relative path within memory/ (defaults to YYYY-MM-DD.md)" }
  },
  "required": ["content"]
}
```

**Boundary rules:**
- Tool calls `IAgentWorkspaceManager.SaveDailyNoteAsync`
- `file_path` must not escape memory directory (validate no `..` segments)
- Returns confirmation with the resolved file path
- Tool is registered only when `memory.enabled` is true in agent config

### Wave 1C: Context Integration (Farnsworth) — ~1h

**Files to modify:**

| File | Change |
|------|--------|
| `src/gateway/BotNexus.Gateway/Agents/SystemPromptBuilder.cs` | Include `RecentMemoryNotes` from workspace in context (after MEMORY.md) |
| `src/gateway/BotNexus.Gateway.Prompts/ContextFileOrdering.cs` | Add ordering slot for daily notes (priority 75, between memory.md=70 and unordered=MaxValue) |

**Boundary rules:**
- Daily notes injected as `<!-- memory/2026-05-07.md -->\n{content}` sections
- Total daily note budget: 4000 chars combined (truncate oldest if over)
- Only non-empty files included

### Wave 1D: Tests (Hermes, parallel with 1A/1B) — ~2h

**Files to create:**

| File | Coverage |
|------|----------|
| `tests/BotNexus.Memory.Tests/Tools/MemorySaveToolTests.cs` | Tool execution, path validation, default date, append semantics |
| `tests/BotNexus.Gateway.Tests/Agents/FileAgentWorkspaceManager_DailyNoteTests.cs` | SaveDailyNoteAsync, LoadWorkspaceAsync with notes, path override, missing dir |
| `tests/BotNexus.Gateway.Prompts.Tests/ContextFileOrdering_DailyNoteTests.cs` | Ordering of daily note entries |

## Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| `AgentWorkspace` record change breaks existing consumers | Medium | It's `init`-only with default; existing code doesn't set it |
| Daily notes grow unbounded in filesystem | Low (v1) | Bounded context loading (2 days). Dreaming/cleanup is Phase 4 |
| Path traversal in `file_path` argument | Medium | Validate: resolve path, assert it starts with memory directory prefix |
| Clock mismatch (UTC vs user local) for daily note filename | Low | Use UTC consistently; user sees dates in their notes. Document convention |
| `IAgentWorkspaceManager` interface change breaks extension implementors | Low | No known external implementors; only `FileAgentWorkspaceManager` exists |

## Acceptance Criteria

1. ✅ Agent can call `memory_save("my note")` and content appears in `{workspace}/memory/YYYY-MM-DD.md`
2. ✅ Agent can call `memory_save("note", file_path: "project-x.md")` and content appears in `{workspace}/memory/project-x.md`
3. ✅ On session start, agent context includes MEMORY.md content AND today/yesterday daily note content
4. ✅ Daily notes are append-only (calling memory_save twice appends, doesn't overwrite)
5. ✅ Path traversal attempts (`../secrets.md`) are rejected with clear error
6. ✅ Agent with `memory.path` config override writes to the custom path instead of `{workspace}/memory/`
7. ✅ All existing tests continue to pass (zero regressions)
8. ✅ New tests cover: tool happy path, append semantics, path validation, context loading, ordering

## Out of Scope (Wave 2+)

- Pre-compaction memory flush (Phase 1 in the lifecycle spec)
- Dreaming / consolidation (Phase 4)
- Memory search indexing of daily notes
- Session-end flush trigger
- AGENTS.md generation changes (deferred unless trivial addition exists)
