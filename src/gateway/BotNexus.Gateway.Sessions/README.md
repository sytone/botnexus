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
