---
id: improvement-heartbeat-service
title: "Heartbeat Service — Reliable Periodic Agent Polling"
type: improvement
priority: high
status: delivered
created: 2026-07-26
---

# Heartbeat Service — Reliable Periodic Agent Polling

## Problem Statement

BotNexus agents cannot autonomously wake up to perform periodic tasks. The heartbeat infrastructure is partially scaffolded (TriggerType, prompt section, HEARTBEAT.md convention) but completely non-functional — no scheduler, no wiring, no configuration path. Agents only respond when a user or cron job explicitly triggers them, and no heartbeat cron jobs exist.

## Goals

1. Agents periodically wake up and execute tasks defined in HEARTBEAT.md
2. Default 30-minute interval, configurable per-agent
3. Heartbeat prompt customizable per-agent with sensible defaults
4. Quiet hours respected per-agent timezone
5. Minimal session/cost overhead — heartbeat acks are discarded
6. No disruption to active conversations

## Non-Goals

- Health monitoring / process watchdog (separate concern)
- Memory consolidation (handled by soul session reflection)
- Complex task scheduling (use cron directly)

## Design

### Architecture Decision: Cron-Based Heartbeat

Per prior team decision (see `.squad/agents/leela/history-archive.md:832`), heartbeat is implemented as **auto-provisioned cron jobs**, not a dedicated `BackgroundService`. This is consistent with the existing cron infrastructure and avoids a parallel scheduling system.

When an agent has `heartbeat.enabled = true`, the gateway:
1. Auto-provisions a system cron job with the configured interval
2. The cron job uses `AgentPromptAction` to deliver the heartbeat prompt
3. Routes through `SoulTrigger` (if soul enabled) or `CronTrigger`

### Agent Configuration

Add a `heartbeat` section to agent config in `config.json`:

```json
{
  "agents": {
    "nova": {
      "heartbeat": {
        "enabled": true,
        "intervalMinutes": 30,
        "prompt": "Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK.",
        "quietHours": {
          "enabled": true,
          "start": "23:00",
          "end": "07:00",
          "timezone": "Australia/Melbourne"
        }
      }
    }
  }
}
```

#### Configuration Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `heartbeat.enabled` | bool | `false` | Enable periodic heartbeat polling |
| `heartbeat.intervalMinutes` | int | `30` | Minutes between heartbeat polls |
| `heartbeat.prompt` | string | *(see below)* | The prompt sent to the agent each heartbeat |
| `heartbeat.quietHours.enabled` | bool | `false` | Skip heartbeats during quiet hours |
| `heartbeat.quietHours.start` | string | `"23:00"` | Quiet period start (local time) |
| `heartbeat.quietHours.end` | string | `"07:00"` | Quiet period end (local time) |
| `heartbeat.quietHours.timezone` | string | Agent's soul timezone or `"UTC"` | Timezone for quiet hours |

#### Default Heartbeat Prompt

```
Read HEARTBEAT.md if it exists and execute any pending tasks. If nothing needs attention, reply HEARTBEAT_OK.
```

### Domain Model Changes

#### New: `HeartbeatAgentConfig`

```csharp
// BotNexus.Gateway.Abstractions/Models/HeartbeatAgentConfig.cs
public sealed class HeartbeatAgentConfig
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public string? Prompt { get; set; }
    public QuietHoursConfig? QuietHours { get; set; }
}

public sealed class QuietHoursConfig
{
    public bool Enabled { get; set; }
    public string Start { get; set; } = "23:00";
    public string End { get; set; } = "07:00";
    public string? Timezone { get; set; }
}
```

#### Updated: `AgentDescriptor`

Add `Heartbeat` property alongside existing `Soul`:

```csharp
public HeartbeatAgentConfig? Heartbeat { get; init; }
```

#### Updated: `AgentDefinitionConfig` (PlatformConfig)

```csharp
public HeartbeatAgentConfig? Heartbeat { get; set; }
```

### Cron Job Auto-Provisioning

#### On Gateway Startup

For each agent with `heartbeat.enabled = true`:

1. Check if a system cron job `heartbeat:{agentId}` exists
2. If not, create one:
   ```json
   {
     "id": "heartbeat:nova",
     "name": "Heartbeat — Nova",
     "agentId": "nova",
     "schedule": "*/30 * * * *",
     "message": "<heartbeat prompt>",
     "actionType": "agent-prompt",
     "system": true,
     "enabled": true
   }
   ```
3. If it exists but interval changed, update the cron expression
4. If heartbeat was disabled, disable/remove the cron job

Mark these as `system: true` so they're distinguished from user-created cron jobs and don't appear in normal cron listings unless requested.

#### Quiet Hours Enforcement

The cron job runs on schedule but the `AgentPromptAction` checks quiet hours before executing:

```csharp
// In AgentPromptAction.ExecuteAsync():
if (IsInQuietHours(descriptor.Heartbeat?.QuietHours))
{
    context.Skip("Quiet hours active");
    return;
}
```

### System Prompt Wiring

#### Fix: Populate `HeartbeatPrompt` in `WorkspaceContextBuilder`

```csharp
// In WorkspaceContextBuilder, when building SystemPromptParams:
HeartbeatPrompt = descriptor.Heartbeat?.Enabled == true
    ? descriptor.Heartbeat.Prompt ?? DefaultHeartbeatPrompt
    : null,
```

This activates the existing `BuildHeartbeatSection()` in `SystemPromptBuilder`, which renders:

```
## Heartbeats
Heartbeat prompt: <the configured prompt>
If you receive a heartbeat poll, and there is nothing that needs attention, reply exactly:
HEARTBEAT_OK
```

### HEARTBEAT_OK Response Handling

When the agent replies with `HEARTBEAT_OK` (or a response starting/ending with it):

1. **In heartbeat/cron sessions**: Mark the response as `heartbeatAck = true` in session metadata
2. **Session compaction**: Heartbeat ack turns are aggressively compacted (or removed entirely)
3. **Channel routing**: `HEARTBEAT_OK` responses are NOT forwarded to any user-facing channel
4. **Logging**: Log at Debug level only

Implementation in the response pipeline:

```csharp
private static bool IsHeartbeatAck(string response) =>
    response.Trim().Equals("HEARTBEAT_OK", StringComparison.Ordinal) ||
    response.TrimStart().StartsWith("HEARTBEAT_OK", StringComparison.Ordinal);
```

### HEARTBEAT.md Convention

HEARTBEAT.md is a workspace context file that agents read during heartbeat polls. It defines periodic tasks the agent should perform.

#### File Location

`~/.botnexus/agents/{agentId}/workspace/HEARTBEAT.md`

#### Bootstrap

When `botnexus agent add` creates a new agent with heartbeat enabled, scaffold a default HEARTBEAT.md:

```markdown
# Heartbeat Tasks

<!-- 
  Define periodic tasks for this agent. These are checked every heartbeat interval.
  Keep tasks lightweight to minimize token/cost usage.
  If nothing needs attention, the agent replies HEARTBEAT_OK.
-->

## Periodic Checks
- Review workspace for any pending tasks
- Check if any files need updating
```

#### Dynamic Context File

`ContextFileOrdering.IsDynamic("heartbeat.md")` already returns `true`. Ensure this means HEARTBEAT.md is:
- Always freshly loaded (not cached) on each heartbeat
- Loaded with higher priority during heartbeat sessions

### Collision with Active Conversations

When a heartbeat fires and the agent has an active user conversation:

| Scenario | Behavior |
|----------|----------|
| Soul session enabled | Heartbeat goes into the daily soul session (separate from conversation) — no collision |
| Soul session disabled | Heartbeat creates/uses a cron session — separate from the user's channel session — no collision |
| Agent at max concurrent sessions | Skip heartbeat, log warning, retry next interval |

Since heartbeat uses internal triggers (Soul or Cron), which create their own session types, they are inherently isolated from channel-based user conversations. No special collision handling needed beyond concurrency limits.

## Implementation Plan

### Phase 1: Configuration & Wiring (Small)

1. Add `HeartbeatAgentConfig` and `QuietHoursConfig` to domain models
2. Add `Heartbeat` property to `AgentDescriptor` and `AgentDefinitionConfig`
3. Wire `HeartbeatPrompt` in `WorkspaceContextBuilder` from agent config
4. Map heartbeat config in `FileAgentConfigurationSource` and `PlatformConfigAgentSource`

### Phase 2: Cron Auto-Provisioning (Medium)

5. Add `system` flag to `CronJob` model
6. Implement `HeartbeatCronProvisioner` — runs on gateway startup, syncs heartbeat cron jobs
7. Add quiet hours check to `AgentPromptAction`
8. Hide system cron jobs from default `cron list` output

### Phase 3: Response Handling (Small)

9. Implement `HEARTBEAT_OK` detection in response pipeline
10. Suppress channel forwarding for heartbeat acks
11. Add session metadata tagging for heartbeat turns

### Phase 4: Bootstrap & Polish (Small)

12. Add HEARTBEAT.md to agent workspace bootstrap template
13. Add `heartbeat` section to config JSON schema
14. Update `example-agent.json` with heartbeat config example
15. Documentation

## Cost Considerations

At 30-minute intervals:
- ~48 heartbeat polls per agent per day
- If most return `HEARTBEAT_OK` (minimal tokens): ~$0.10-0.50/day on Opus
- Consider: allow `heartbeat.model` override to use cheaper model (e.g., `gpt-4.1-mini`)
- Future: adaptive interval — increase interval if agent consistently returns HEARTBEAT_OK

## Open Questions

1. **Should heartbeat have its own trigger type?** `TriggerType.Heartbeat` exists but using cron routing through Soul/Cron triggers is simpler. Should we create a `HeartbeatTrigger : IInternalTrigger`?
2. **Model override for cost**: Should heartbeat polls use a cheaper model than the agent's primary model?
3. **Heartbeat session type**: Should heartbeat turns go into soul sessions, or should there be a `SessionType.Heartbeat`?
4. **Agent tool to manage heartbeat**: Should agents be able to modify their own HEARTBEAT.md tasks or adjust their interval?
