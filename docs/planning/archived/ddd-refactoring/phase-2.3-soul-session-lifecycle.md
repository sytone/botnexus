---
status: deferred
depends-on: Phase 2.1 (Cron decoupling / IInternalTrigger)
created: 2026-04-12
---

# Phase 2.3: Soul Session Lifecycle

## Summary

Introduce a daily Soul Session lifecycle so each agent has a private, scheduled session for reflection, memory maintenance, and heartbeat execution. The Soul Session is the agent's inner life - where it thinks, reviews its day, and proactively reaches out.

## Current State

- Heartbeat runs as cron jobs that create sessions via `CronChannelAdapter`
- These sessions have `SessionType.Cron` and `ChannelKey.From("cron")`
- No concept of a "daily session" - each heartbeat creates a new independent session
- No automatic sealing of old sessions
- Agent has no single persistent session for self-reflection within a day

## Target State

Each agent gets one Soul Session per day:
- Created automatically at the start of the day (or on first heartbeat trigger)
- All heartbeat executions within that day run within the same Soul Session
- Session is automatically sealed at the end of the day (configurable boundary, default midnight in agent's configured timezone)
- New Soul Session created for the next day

## Detailed Design

### Soul Session Creation

The Soul Session is created by a `SoulTrigger : IInternalTrigger` (depends on Phase 2.1 delivering the `IInternalTrigger` interface).

```csharp
public class SoulTrigger : IInternalTrigger
{
    public TriggerType Type => TriggerType.Soul;

    // Called by the gateway's daily scheduler or on first heartbeat of the day
    public Task<GatewaySession> CreateSessionAsync(AgentId agentId, ...);
}
```

**Trigger timing options** (configurable per agent):
1. **On first heartbeat of the day** (default) - lazy creation. The Soul Session is created when the first heartbeat fires after the day boundary. Simple, no extra scheduling needed.
2. **At a configured time** - proactive creation. A daily timer creates the session at e.g., 00:00 agent-local-time. Useful if the agent should reflect even without a heartbeat configured.

Recommendation: option 1 (lazy) as default, option 2 as opt-in via agent config.

### Soul Session Schema

A Soul Session is a standard `GatewaySession` with:
- `SessionType` = `SessionType.Soul`
- `IsInteractive` = false (computed from SessionType, already works)
- `AgentId` = the agent this session belongs to
- `ChannelType` = null (no channel - this is an internal trigger)
- `Participants` = agent only
- `Metadata["soulDate"]` = the date this session covers (YYYY-MM-DD)

### Session ID Convention

Soul Session IDs follow: `{agentId}::soul::{date}` (e.g., `nova::soul::2026-04-12`)

This makes them:
- Deterministic (you can predict the ID for any agent+date)
- Queryable (find soul sessions by date range)
- Using the existing `SessionId` value object with a factory method: `SessionId.ForSoul(agentId, date)`

### Daily Lifecycle

```
Day boundary (midnight agent-local or configured time)
    |
    v
[Previous Soul Session] --> Sealed automatically
    |
[New Soul Session] --> Created (Active)
    |
    +-- Heartbeat 1 executes within this session
    +-- Heartbeat 2 executes within this session
    +-- Agent reflects, updates memory
    +-- Heartbeat N executes within this session
    |
Day boundary
    |
    v
[This Soul Session] --> Sealed automatically
[Next Soul Session] --> Created
```

### Heartbeat Integration

Currently heartbeats create new cron sessions. After this change:

1. Heartbeat cron job fires
2. Instead of creating a new session, it looks for the current day's Soul Session for this agent
3. If found and Active, the heartbeat message is injected into that session
4. If not found (first heartbeat of the day), create the Soul Session, then inject
5. The agent responds within the Soul Session context - it has the full day's reflection history

This means the Soul Session accumulates the agent's inner dialogue throughout the day. The agent can reference earlier heartbeat interactions, building a richer reflection context.

### Sealing Previous Sessions

When a new Soul Session is created:
1. Query for any Active Soul Sessions for this agent with a `soulDate` before today
2. Seal them all (there should be at most one, but handle edge cases)
3. Optionally trigger a "end of day reflection" prompt before sealing (configurable)

### Agent Configuration

Add to `AgentDescriptor` (or agent config):

```json
{
  "soul": {
    "enabled": true,
    "timezone": "America/Los_Angeles",
    "dayBoundary": "00:00",
    "reflectionOnSeal": true,
    "reflectionPrompt": "Review your day. What happened? What did you learn? Update MEMORY.md if needed."
  }
}
```

All fields optional with sensible defaults. If `soul.enabled` is false (or omitted), heartbeats continue as cron sessions (backward compatible).

### Migration Path

1. **Phase 1**: Deliver `SoulTrigger` alongside existing `CronChannelAdapter`. Both work in parallel.
2. **Phase 2**: Agents with `soul.enabled = true` use Soul Sessions for heartbeat. Others keep using cron sessions.
3. **Phase 3**: Once stable, consider making Soul Sessions the default for agents with heartbeats configured.

Existing cron sessions are NOT migrated. Old heartbeat sessions remain as `SessionType.Cron` in the agent's Existence. New heartbeats go to Soul Sessions.

## Test Requirements

- Soul Session creation on first heartbeat of the day
- Multiple heartbeats within a day share the same Soul Session
- Previous day's Soul Session is sealed when new one is created
- Soul Session ID follows the convention
- Soul Sessions appear in agent's Existence
- Backward compatibility: agents without soul config still use cron sessions
- Day boundary handling across timezones
- Gateway restart mid-day resumes the existing Soul Session (not create a new one)

## Risks

1. **State accumulation**: Soul Sessions accumulate all heartbeat interactions for a day. Could get large. Mitigate with session compaction or a max-history config.
2. **Timezone edge cases**: Day boundaries depend on agent timezone config. Need robust timezone handling.
3. **Concurrent heartbeats**: If two heartbeats fire simultaneously, both try to find/create the Soul Session. Need locking or optimistic concurrency.

## Acceptance Criteria

- [ ] Each agent has at most one Active Soul Session per day
- [ ] Heartbeats execute within the Soul Session when enabled
- [ ] Previous day's Soul Session is automatically sealed
- [ ] Soul Sessions use SessionType.Soul and are non-interactive
- [ ] Backward compatible - agents without soul config are unaffected
- [ ] Gateway restart resumes existing Soul Session
