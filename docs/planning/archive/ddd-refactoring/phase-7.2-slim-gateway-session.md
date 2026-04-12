---
status: deferred
depends-on: Phase 7.1 (Split Abstractions)
created: 2026-04-12
---

# Phase 7.2: Slim Down GatewaySession

## Summary

Split `GatewaySession` into two types: `Session` (domain state) and `GatewaySessionRuntime` (infrastructure - replay buffers, streaming, WebSocket concerns). This is the highest-risk item in the DDD refactoring because GatewaySession is deeply entangled with real-time delivery infrastructure.

## Why Deferred

The original spec gave this 5 lines. It needs a full sub-spec because:
- `GatewaySession` has a native `Lock _historyLock` with 5 locked methods
- The replay buffer (`SessionReplayBuffer`) is tightly coupled to WebSocket delivery
- Stream event sequencing spans both domain (history) and infrastructure (replay)
- Every session consumer in the codebase touches this type

## Current State: GatewaySession Field Inventory

```csharp
public sealed class GatewaySession
{
    // === DOMAIN STATE (should be on Session) ===
    public required string SessionId { get; init; }
    public required string AgentId { get; set; }
    public string? ChannelType { get; set; }          // Now ChannelKey
    public string? CallerId { get; set; }              // Replaced by Participants
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public List<SessionEntry> History { get; init; }
    public Dictionary<string, object?> Metadata { get; init; }
    public SessionType SessionType { get; set; }       // Added in Wave 1
    public bool IsInteractive { get; }                 // Added in Wave 1
    public List<SessionParticipant> Participants { get; init; }  // Added in Wave 1

    // === INFRASTRUCTURE (should be on GatewaySessionRuntime) ===
    private readonly Lock _historyLock;                // Thread-safe history access
    private readonly SessionReplayBuffer _replayBuffer; // WebSocket replay
    public long NextSequenceId { get; set; }           // Outbound sequence counter
    public List<GatewaySessionStreamEvent> StreamEventLog { get; }  // Replay snapshot
    public SessionReplayBuffer ReplayBuffer { get; }   // Direct buffer access

    // === METHODS: Domain ===
    public int MessageCount { get; }
    public void AddEntry(SessionEntry entry);
    public void AddEntries(IEnumerable<SessionEntry> entries);
    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries);
    public IReadOnlyList<SessionEntry> GetHistorySnapshot();
    public IReadOnlyList<SessionEntry> GetHistorySnapshot(int offset, int limit);

    // === METHODS: Infrastructure ===
    public long AllocateSequenceId();
    public void AddStreamEvent(long sequenceId, string payloadJson, int replayWindowSize);
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastId, int max);
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventSnapshot();
    public void SetStreamReplayState(long nextId, IEnumerable<GatewaySessionStreamEvent>? events);
}
```

## Target State

### Session (Domain)

Pure domain type in `BotNexus.Domain`. No threading, no infrastructure, no WebSocket concerns:

```csharp
public sealed record Session
{
    public required SessionId SessionId { get; init; }
    public required AgentId AgentId { get; init; }
    public ChannelKey? ChannelType { get; init; }
    public SessionType SessionType { get; init; }
    public SessionStatus Status { get; set; }
    public bool IsInteractive => SessionType == SessionType.UserAgent;
    public List<SessionParticipant> Participants { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public List<SessionEntry> History { get; init; } = [];
    public Dictionary<string, object?> Metadata { get; init; } = [];
    public int MessageCount => History.Count;
}
```

Note: History is NOT thread-safe here. Thread safety is an infrastructure concern.

### GatewaySessionRuntime (Infrastructure)

Lives in `BotNexus.Gateway`. Wraps a `Session` and adds the infrastructure layer:

```csharp
public sealed class GatewaySessionRuntime
{
    private readonly Lock _lock = new();
    private readonly SessionReplayBuffer _replayBuffer = new();

    public Session Session { get; }

    public GatewaySessionRuntime(Session session)
    {
        Session = session;
    }

    // Thread-safe domain operations (delegates to Session under lock)
    public void AddEntry(SessionEntry entry) { lock (_lock) { Session.History.Add(entry); Session.UpdatedAt = DateTimeOffset.UtcNow; } }
    public IReadOnlyList<SessionEntry> GetHistorySnapshot() { lock (_lock) { return Session.History.ToList(); } }
    // ... other thread-safe wrappers

    // Infrastructure-only operations
    public long AllocateSequenceId() => _replayBuffer.AllocateSequenceId();
    public void AddStreamEvent(long sequenceId, string payloadJson, int windowSize) => ...;
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastId, int max) => ...;
    // ... other replay/streaming methods
}
```

### Relationship

```
GatewaySessionRuntime
    |
    +-- Session (domain state, serializable, testable)
    |
    +-- ReplayBuffer (infrastructure, not serialized long-term)
    +-- Lock (thread safety)
    +-- Sequence tracking
```

## Consumer Impact Analysis

Every place that currently uses `GatewaySession` needs to be updated. The approach depends on what the consumer needs:

### Consumers that need domain state only (update to use `Session`)

- `ISessionStore` implementations - persist/load `Session`
- `SessionSummary` construction
- `SessionWarmupService` - queries session metadata
- `SessionTool` - exposes session info to agents
- `LlmSessionCompactor` - reads/writes history
- API response models (`SessionHistoryResponse`)

### Consumers that need infrastructure (update to use `GatewaySessionRuntime`)

- `GatewayHost` - message processing, streaming
- `StreamingSessionHelper` - stream event recording
- `GatewayHub` (SignalR) - replay, sequence IDs
- `ChatController` - prompt/response with history
- Channel adapters - message delivery

### Migration Pattern

```csharp
// Before
public async Task SaveAsync(GatewaySession session, CancellationToken ct)

// After - stores only care about the domain session
public async Task SaveAsync(Session session, CancellationToken ct)

// Runtime consumers get the runtime wrapper
var runtime = _supervisor.GetRuntime(agentId, sessionId);
runtime.AddEntry(new SessionEntry { ... });
await _sessionStore.SaveAsync(runtime.Session, ct);
```

## Thread-Safety Model

**Session** (domain): Not thread-safe. It's a data container. Thread safety is the caller's responsibility.

**GatewaySessionRuntime**: Thread-safe. All mutations go through the runtime's lock. Consumers never mutate `Session.History` directly - they call `runtime.AddEntry()` etc.

**Rule**: If you're in the Gateway's real-time path (handling messages, streaming), use `GatewaySessionRuntime`. If you're in a store, API, or query path, use `Session` directly.

## Serialization Impact

`ISessionStore` implementations currently serialize `GatewaySession`. After the split:
- Stores serialize/deserialize `Session` (the domain type)
- Replay buffer state is ephemeral (reconstructed on session resume, not persisted long-term)
- `NextSequenceId` and `StreamEventLog` may need to persist for reconnect support - these stay on `Session.Metadata` or a dedicated sidecar

### File store migration

The JSONL file format stores session entries. The metadata sidecar (.meta.json) stores session properties. Both map cleanly to `Session` fields. No format change needed - just the type that gets serialized.

### SQLite store migration

Column mapping stays the same. The SQL schema doesn't change - only the C# type used for reads/writes.

## Snapshot Tests (Required BEFORE Any Changes)

Before touching GatewaySession:
1. Capture the full serialized output of a GatewaySession with history, metadata, replay events
2. Capture the behavior of each thread-safe method (concurrent add, snapshot during add)
3. Capture the behavior of stream event recording and replay
4. These become the regression tests that prove the split didn't change behavior

## Migration Plan

1. **Create `Session` type** in BotNexus.Domain (additive, no breaking changes)
2. **Create `GatewaySessionRuntime`** in BotNexus.Gateway that wraps `Session`
3. **Add conversion**: `GatewaySession` gets a `ToSession()` method and `GatewaySessionRuntime.FromLegacy(GatewaySession)` factory
4. **Update stores first**: `ISessionStore` changes to `Session`. Internal conversion handles the transition.
5. **Update consumers incrementally**: One file at a time, switch from `GatewaySession` to the appropriate split type
6. **Remove `GatewaySession`**: Once nothing references it

## Risks

1. **Replay buffer persistence**: If `NextSequenceId` is needed for WebSocket reconnect, it must survive session save/load. Decide where it lives.
2. **Concurrent access patterns**: The current `_historyLock` protects both history AND the UpdatedAt timestamp atomically. The split must preserve this atomicity.
3. **Performance**: Adding a wrapper layer introduces an indirection. Benchmark critical paths (message processing, streaming).
4. **Store compatibility**: Existing persisted sessions must load into the new `Session` type without data loss.

## Acceptance Criteria

- [ ] `Session` type exists in `BotNexus.Domain` with all domain properties
- [ ] `GatewaySessionRuntime` wraps Session with thread-safety and replay buffer
- [ ] All session stores use `Session` for persistence
- [ ] All real-time consumers use `GatewaySessionRuntime`
- [ ] `GatewaySession` is deleted
- [ ] Snapshot tests pass before and after the split
- [ ] WebSocket reconnect/replay still works
- [ ] No data loss when loading existing persisted sessions
