# BotNexus.Gateway.Sessions

> File-backed and in-memory session store implementations for the BotNexus Gateway.

## Overview

This package provides two implementations of `ISessionStore` (defined in `BotNexus.Gateway.Abstractions`): a durable file-backed store using JSONL with JSON metadata sidecars, and a fast in-memory store for development and testing. Both are thread-safe. JSONL, sidecar metadata, and compaction primitives are included in this package.

## Key Types

| Type | Kind | Description |
|------|------|-------------|
| `FileSessionStore` | Class | Durable file-backed session store. Writes history as JSONL and metadata as a `.meta.json` sidecar. Thread-safe via `SemaphoreSlim`. |
| `InMemorySessionStore` | Class | Non-durable in-memory store for development and testing. Thread-safe via `Lock`. |

## File Storage Layout

`FileSessionStore` persists each session as two files:

```
{storePath}/
  {sessionId}.jsonl         ← Conversation history (one JSON entry per line)
  {sessionId}.meta.json     ← Session metadata (agent, channel, caller, timestamps)
```

Session IDs are sanitized via `Uri.EscapeDataString` for filesystem safety.

### JSONL History Format

Each line is a serialized `SessionEntry`:

```json
{"role":"user","content":"Hello","timestamp":"2026-04-05T10:00:00+00:00"}
{"role":"assistant","content":"Hi there!","timestamp":"2026-04-05T10:00:01+00:00"}
{"role":"tool","content":"{\"result\":42}","toolName":"calculator","toolCallId":"tc_1","timestamp":"2026-04-05T10:00:02+00:00"}
```

### Metadata Sidecar Format

```json
{
  "agentId": "coding-agent",
  "channelType": "signalr",
  "callerId": "user-123",
  "createdAt": "2026-04-05T10:00:00+00:00",
  "updatedAt": "2026-04-05T10:05:00+00:00"
}
```

## Usage

### FileSessionStore

```csharp
var store = new FileSessionStore(
    storePath: "/var/botnexus/sessions",
    logger: loggerFactory.CreateLogger<FileSessionStore>());

// Create or resume a session
var session = await store.GetOrCreateAsync("session-abc", "coding-agent");

// Add entries and save
session.AddEntry(new SessionEntry { Role = "user", Content = "Hello" });
await store.SaveAsync(session);

// List sessions for a specific agent
var sessions = await store.ListAsync(agentId: "coding-agent");
```

### InMemorySessionStore

```csharp
var store = new InMemorySessionStore();

// Same ISessionStore interface — swap in for tests
var session = await store.GetOrCreateAsync("test-session", "test-agent");
```

### Custom Session Store

To implement your own session store (Redis, SQLite, PostgreSQL, etc.):

```csharp
public class RedisSessionStore : ISessionStore
{
    public Task<GatewaySession?> GetAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken ct = default) { /* ... */ }
    public Task SaveAsync(GatewaySession session, CancellationToken ct = default) { /* ... */ }
    public Task DeleteAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken ct = default) { /* ... */ }
}

// Register via DI
services.AddSingleton<ISessionStore, RedisSessionStore>();
```

## Configuration

`FileSessionStore` requires a `storePath` constructor argument — the directory where session files are stored. The directory is created automatically if it doesn't exist.

`InMemorySessionStore` requires no configuration.

## Dependencies

- **Target framework:** `net10.0`
- **Project references:**
  - `BotNexus.Gateway.Abstractions` — `ISessionStore`, `GatewaySession`, `SessionEntry`
- **NuGet packages:**
  - `Microsoft.Extensions.Logging.Abstractions` — `ILogger<T>` for `FileSessionStore`

## Extension Points

| Extension | How |
|-----------|-----|
| Custom persistence backend | Implement `ISessionStore` and register in DI. |
| Session middleware | Wrap an `ISessionStore` with a decorator to add caching, encryption, or auditing. |

### Thread Safety Notes

- **`FileSessionStore`** uses a single `SemaphoreSlim(1, 1)` to serialize all file I/O. It also maintains an in-memory cache for fast reads. Suitable for single-instance deployments.
- **`InMemorySessionStore`** uses `Lock` for synchronization. All operations are synchronous under the hood (returning completed tasks).
- **`GatewaySession`** provides thread-safe `AddEntry`/`AddEntries`/`GetHistorySnapshot` methods via its internal lock.
- All `ISessionStore` implementations must be thread-safe per the contract.

### ConfigureAwait Pattern

This library uses `ConfigureAwait(false)` on all awaited tasks to prevent deadlocks when consumed by callers with a synchronization context. This is intentional — see the source comment in `FileSessionStore.cs` for the rationale.

## Legacy inventory preflight

`LegacyAggregateInventory.InspectAsync(sessionDirectory, dataDirectory, cancellationToken)`
provides a **read-only byte inventory**, not a migration command. Pass the already resolved
File session directory and writable data directory explicitly. It is not registered at startup
and does not change the selected backend.

Use only against a quiesced, offline source. The inspector fingerprints the same bytes it
validates, but does not acquire an aggregate writer lock or guarantee a consistent snapshot
while another process replaces files. A successful inventory is **not import-readiness
certification**.

The inventory covers paired session sidecars and ordered JSONL (including archived generations
and retained sub-agent consumption markers), nested conversations/read state, retained sub-agent
details, and spawn indexes. Results contain relative paths, categories, byte lengths, record
counts and SHA-256 fingerprints, never transcript contents. The separate `sections.sqlite` path
is reported for later aggregate handling; this inspector neither opens nor verifies that database.
No audit history is inferred or invented.

Unsupported files, duplicate JSON members, malformed or unsupported wire shapes, unmatched
session files and selected identity conflicts fail the whole inspection. Documents and individual
JSONL records are limited to 4 MiB and nesting depth 64; histories are streamed rather than loaded
as one document. Nonempty polymorphic content-part shapes unsupported by the existing file
serializer are refused rather than guessed. File descriptors and identity indexes scale with the
number of source files.

Automatic startup import, complete relationship validation, concurrent-writer exclusion, durable
restart progress, destination conflict handling, verified activation and recovery remain tracked
by #4962. Retain source files; this preflight never imports, deletes, repairs or rewrites them.

### Opt-in explicit relationship checks (developer API)
Call `InspectRelationshipsAsync(sessionDirectory, dataDirectory, cancellationToken)` explicitly
for the same byte inventory plus these checks during the same streaming pass:

- Every explicit `Conversation.ActiveSessionId` resolves to an **active** history/metadata pair
  whose metadata `ConversationId` exactly matches that conversation. An archived pair cannot
  satisfy this link, and missing metadata binding is not inferred from an agent or session name.
- An explicit legacy metadata `AgentId`, when accompanied by a conversation binding, agrees
  with that conversation's agent. This includes archived metadata. Legacy agent-only metadata
  is not assigned an inferred conversation.
- Every read cursor references an existing conversation. World and reader values remain part
  of the existing cursor filename hash; no local-world derivation or cross-world merging occurs.
- For each retained sub-agent parent and child, an explicit recorded conversation/agent pair
  must agree with that conversation's agent when the conversation still exists. Conversation
  ownership is immutable. Missing optional identities are not filled in or inferred from a
  session's current metadata.

`InspectAsync` retains its byte-inventory behavior; none of these additional checks run there.
Both methods return the same paths, byte lengths, record counts and SHA-256 values for a valid
source. The opt-in indexes retain only scalar identities, scale with source records, and never
retain transcripts, run results, summaries, or decoded conversation payloads.

Retained run session identifiers are **historical references, not foreign keys to current session
bindings**. `SessionStoreBase.RebindSessionsAsync` changes and saves a session's conversation without
rewriting retained runs; the same session may therefore have a different current conversation or
agent. The inspector never compares a run's recorded conversation or agent with current session
metadata, even if that session still exists. An omitted run conversation is not inferred from it.
Retained run references are also **not existence constraints**: `FileSessionStore.DeleteAsync`
removes only the session files, and `ArchiveAsync` moves them out of active lookup without removing
retained run details. Missing parent/child sessions or recorded conversations do not fail this
check. A recorded conversation/agent mismatch still fails when the conversation exists, even if
its run's session is gone. Archived generations are not used to guess a run's historical binding.
Conversation `ParentConversationId` links are also excluded: the file conversation store accepts
creation without requiring the parent target. This preflight does not impose a stronger graph rule.

This is a dormant developer API, not a CLI command, startup hook, importer, configuration switch,
writer lock, or SQLite operation. It does not certify the complete graph or activate a destination.
Use a quiesced offline source and retain it unchanged; ordinary remote validation and independent
review remain necessary before integrating any later migration stage.
