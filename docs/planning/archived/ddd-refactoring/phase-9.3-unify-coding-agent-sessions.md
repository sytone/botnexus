---
status: deferred
depends-on: Phase 7.2 (Slim GatewaySession)
created: 2026-04-12
---

# Phase 9.3: Unify CodingAgent Sessions

## Summary

The CodingAgent has its own session management system (759 lines, JSONL-based) that is completely separate from the Gateway's `ISessionStore`. This phase either absorbs CodingAgent sessions into the Gateway's infrastructure or extracts shared session primitives into a common library.

## Strategic Decision Required

Before this work can proceed, a decision must be made:

**Option A: CodingAgent is absorbed into the Gateway**
- CodingAgent becomes another agent running on the Gateway
- Its sessions use `ISessionStore` like every other agent
- CodingAgent-specific features (branching, interactive CLI) become Gateway features or extensions

**Option B: CodingAgent remains a separate product**
- Gateway and CodingAgent share common session types/formats via a shared library
- Each manages its own sessions independently
- The gateway must NEVER depend on CodingAgent (dependency rule)

This spec covers both options. The squad should confirm with Jon which path to take.

## Current State

### CodingAgent SessionManager (759 lines)
- `BotNexus.CodingAgent/Session/SessionManager.cs`
- JSONL file format (one entry per line, header + message entries)
- `.meta.json` sidecar for session metadata
- Features: branching, compaction, session resume, branch navigation
- Types: `SessionInfo`, `SessionBranchInfo`, `SessionHeaderEntry`, `SessionState`
- File-system based, uses `IFileSystem` abstraction

### Gateway Session Stores
- `ISessionStore` interface with Get, GetOrCreate, Save, Delete, Archive, List, ListByChannel
- Three implementations: InMemory, File (JSONL + .meta.json), SQLite
- Types: `GatewaySession` (being split in Phase 7.2 to `Session`)
- No branching support

### Overlap

| Capability | CodingAgent | Gateway |
|-----------|-------------|---------|
| JSONL format | Yes | Yes (FileSessionStore) |
| .meta.json sidecar | Yes | Yes (FileSessionStore) |
| Branching | Yes | No |
| Compaction | Yes (inline) | Yes (LlmSessionCompactor) |
| Multiple backends | No (file only) | Yes (InMemory, File, SQLite) |
| Session resume | Yes | Yes |
| Thread safety | SemaphoreSlim | Lock |

## Option A: Absorb into Gateway

### Migration Path

1. CodingAgent stops managing its own sessions
2. CodingAgent runs as an agent on the Gateway (it may already do this in some configurations)
3. Sessions are created/managed via `ISessionStore`
4. CodingAgent-specific features need accommodation:

**Branching**: The Gateway's `Session` type doesn't support branches. Options:
- Add branching to `Session` (makes the domain type richer)
- Implement branching as a session metadata feature (branches stored as metadata, session store unaware)
- Implement branching as an extension feature

**Interactive CLI**: CodingAgent has an interactive loop (`InteractiveLoop.cs`) that manages session state locally. In the Gateway model, this becomes a channel adapter (a TUI channel already exists).

**Compaction**: Both have compaction. CodingAgent does inline compaction; Gateway uses `LlmSessionCompactor`. Unify on the Gateway's approach.

### Data Migration

Existing CodingAgent sessions (JSONL files) need migration:
- Parse CodingAgent JSONL format
- Convert `SessionHeaderEntry` to `Session` with appropriate `SessionType`
- Convert message entries to `SessionEntry`
- Handle branches (flatten or preserve as metadata)
- Write to the configured `ISessionStore` backend

### Implementation

1. Add branching support to `Session` or session metadata
2. Write a migration tool for existing CodingAgent sessions
3. Update CodingAgent to use `ISessionStore` instead of `SessionManager`
4. Delete `CodingAgent/Session/SessionManager.cs`
5. Update CodingAgent tests

## Option B: Shared Library (Separate Products)

### Shared Types (in BotNexus.Domain or a new BotNexus.Sessions.Common)

```csharp
// Shared session entry format
public sealed record SessionEntry
{
    public required MessageRole Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public bool IsCompactionSummary { get; init; }
}

// Shared JSONL format primitives
public static class SessionJsonl
{
    public static async Task WriteEntryAsync(Stream stream, SessionEntry entry, ...);
    public static async IAsyncEnumerable<SessionEntry> ReadEntriesAsync(Stream stream, ...);
}
```

### What CodingAgent Keeps

- Its own `SessionManager` (but using shared types)
- Branching logic (unique to CodingAgent)
- Interactive CLI session management
- File-system storage (using shared JSONL format)

### What Changes

- `SessionEntry` type shared from `BotNexus.Domain` (already exists there)
- JSONL read/write utilities shared (new `BotNexus.Sessions.Common` or added to Domain)
- `MessageRole` smart enum used by both (already delivered)
- CodingAgent's `SessionManager` updated to use shared types but keeps its own lifecycle

### Implementation

1. Verify `SessionEntry` in `BotNexus.Domain` meets CodingAgent's needs
2. Extract JSONL read/write as shared primitives
3. Update CodingAgent `SessionManager` to use `BotNexus.Domain.SessionEntry` and `MessageRole`
4. CodingAgent adds a project reference to `BotNexus.Domain` (if not already)
5. No changes to Gateway

## Test Requirements

### Option A
- CodingAgent sessions create/resume/save via ISessionStore
- Migration tool converts existing JSONL sessions correctly
- Branching works through the new model
- Interactive CLI session management still works
- All existing CodingAgent tests pass

### Option B
- Shared SessionEntry type used by both products
- JSONL format is compatible (CodingAgent can read Gateway files and vice versa)
- MessageRole smart enum used consistently
- No dependency from Gateway to CodingAgent

## Risks

1. **Option A - Branching complexity**: Adding branches to the Gateway session model adds complexity for all consumers, not just CodingAgent. Consider isolating it.
2. **Option A - CLI coupling**: The interactive CLI has tight coupling to the file-based session manager. Decoupling may be significant.
3. **Option B - Drift**: If shared types evolve, both products need to stay compatible. Shared types should be stable.
4. **Both - Data loss**: Any migration must be thoroughly tested against real session files.

## Acceptance Criteria

### Option A
- [ ] CodingAgent uses `ISessionStore` for all session operations
- [ ] Existing CodingAgent sessions are migrated without data loss
- [ ] Branching works through the new model
- [ ] `CodingAgent/Session/SessionManager.cs` is deleted
- [ ] Gateway has no dependency on CodingAgent

### Option B
- [ ] Both products use `SessionEntry` and `MessageRole` from `BotNexus.Domain`
- [ ] JSONL format is shared and compatible
- [ ] Gateway has no dependency on CodingAgent
- [ ] CodingAgent keeps its own session lifecycle and branching
