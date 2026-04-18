# Heartbeat Service — Research Findings

**Date:** 2026-07-26  
**Researcher:** Nova (sub-agent)

## Executive Summary

BotNexus has **infrastructure scaffolding** for heartbeats but **no functioning heartbeat service**. The pieces exist in isolation — a `TriggerType.Heartbeat` enum value, a `HeartbeatPrompt` property on the system prompt params, HEARTBEAT.md file conventions, and prompt-building logic that would render a heartbeat section — but nothing ties them together. There is no background service, no scheduler, and no configuration path that actually sends periodic heartbeat polls to agents.

## Current State of Implementation

### What Exists

| Component | Location | Status |
|-----------|----------|--------|
| `TriggerType.Heartbeat` | `BotNexus.Domain\Primitives\TriggerType.cs:17` | **Defined but unused** — no trigger implementation references it |
| `HeartbeatPrompt` property | `SystemPromptBuilder.cs:36` (on `SystemPromptParams`) | **Defined but never populated** — `WorkspaceContextBuilder` constructs `SystemPromptParams` without setting `HeartbeatPrompt` |
| `BuildHeartbeatSection()` | `SystemPromptBuilder.cs:532-549` | **Dead code** — renders a `## Heartbeats` system prompt section with `HEARTBEAT_OK` ack convention, but since `HeartbeatPrompt` is always null, it always returns `[]` |
| `ContextFileOrdering.IsDynamic()` | `BotNexus.Prompts\ContextFileOrdering.cs:41` | Marks `heartbeat.md` as a "dynamic" context file (presumably for special loading behavior) |
| `HEARTBEAT.md` convention | Agent workspace files | Only Nova's main agent has one (`~/.botnexus/agents/nova/workspace/HEARTBEAT.md`); sub-agents don't |
| `SoulAgentConfig` | `BotNexus.Domain\Gateway\Models\SoulAgentConfig.cs` | Enables daily soul sessions with timezone/day-boundary/reflection — **not the same as heartbeat polling** |
| `SoulTrigger` | `BotNexus.Gateway.Api\Hubs\SoulTrigger.cs` | Creates daily soul sessions — runs once per day per agent, not a periodic heartbeat |
| Squad history references | `.squad/agents/*/history.md` | Multiple references to "`IHeartbeatService` runs daily consolidation job" — this was a **previous implementation that was removed** in favor of cron |

### What Doesn't Exist

1. **No `HeartbeatService` or `HeartbeatTrigger` class** — the `IHeartbeatService` mentioned in squad history was removed. No `BackgroundService` or timer-based scheduler exists for heartbeats.
2. **No `IInternalTrigger` implementation for heartbeats** — only `CronTrigger` and `SoulTrigger` are registered in DI (`GatewayApiServiceCollectionExtensions.cs:24-27`).
3. **No heartbeat interval configuration** — `SoulAgentConfig` has no interval/frequency property. `AgentDefinitionConfig` in `PlatformConfig` has no heartbeat settings.
4. **No heartbeat cron jobs** — `cron list` returns empty. No seed jobs configured in `PlatformConfig.Cron`.
5. **No `HeartbeatPrompt` wiring** — nobody sets the `HeartbeatPrompt` on `SystemPromptParams`, so the entire heartbeat system prompt section is dead.
6. **No HEARTBEAT_OK response handling** — the system prompt mentions agents should reply `HEARTBEAT_OK`, but no code checks for or discards this response.

### The Soul Session System (Adjacent but Separate)

The `SoulTrigger` creates **daily** soul sessions — one per agent per calendar day. This is distinct from heartbeat polling:

- **Soul sessions**: Once per day, creates a persistent session scoped to a date. Supports reflection-on-seal when the day rolls over.
- **Heartbeat polling**: Should be every N minutes (e.g., 30min), sending a prompt to the agent's existing session to check HEARTBEAT.md tasks.

The `AgentPromptAction` (cron action) routes to `SoulTrigger` when soul is enabled, or `CronTrigger` otherwise. This is how cron jobs trigger agent prompts, but no heartbeat-specific cron jobs exist.

### OpenClaw Comparison

OpenClaw (the predecessor) had HEARTBEAT.md as a workspace file injected into sessions. The session JSON shows `"label": "heartbeat"` and `"from": "heartbeat"` session entries, indicating OpenClaw had a heartbeat mechanism that created labeled sessions. OpenClaw's approach was simpler — HEARTBEAT.md was a context file read during session creation, and heartbeat sessions were created on a timer.

### Nova's HEARTBEAT.md Content

The only existing HEARTBEAT.md contains:
- Memory file review tasks
- MEMORY.md update checks  
- Teams message monitoring instructions
- Cost efficiency guidance (use haiku subagent)

This is a good template but is currently **never automatically executed** — it only gets read if manually triggered or if the agent happens to read it.

## Gap Analysis

### Critical Gaps

| Gap | Impact | Notes |
|-----|--------|-------|
| **No heartbeat scheduler** | Agents never wake up autonomously | The entire purpose of heartbeat is broken |
| **`HeartbeatPrompt` never set** | System prompt heartbeat section is dead code | Easy fix in `WorkspaceContextBuilder` |
| **No per-agent interval config** | Can't customize polling frequency | Needs `SoulAgentConfig` or new config section |
| **No `HEARTBEAT_OK` response handling** | Heartbeat ack responses would clutter session history | Needs response filtering |
| **No heartbeat trigger type** | Can't distinguish heartbeat sessions from cron/soul | `TriggerType.Heartbeat` exists but no trigger uses it |

### Design Decisions Needed

1. **Should heartbeat be a cron job or a dedicated service?** History shows the team moved away from `IHeartbeatService` toward cron. Using cron is simpler and consistent with existing architecture.
2. **Should heartbeat use soul sessions or create its own?** If soul is enabled, heartbeat should probably inject into the daily soul session rather than creating new ones.
3. **Default interval**: 30 minutes seems right, but should be configurable per-agent.
4. **Quiet hours**: Should heartbeat respect user timezone and skip overnight?
5. **Collision with active conversations**: What happens if the agent is mid-conversation when heartbeat fires?
6. **Cost control**: Heartbeat every 30min × multiple agents × claude-opus = expensive. Should use a cheaper model or have cost guardrails.

## Recommendations

1. **Implement heartbeat as auto-provisioned cron jobs** — when an agent has heartbeat enabled, the gateway auto-creates a cron job with the configured interval.
2. **Add heartbeat config to agent definition** — new `heartbeat` section in agent config with `enabled`, `intervalMinutes`, `prompt`, `quietHours`.
3. **Wire `HeartbeatPrompt` into `WorkspaceContextBuilder`** — read from agent config or default to "Read HEARTBEAT.md if it exists and execute any pending tasks."
4. **Implement `HEARTBEAT_OK` filtering** — discard or minimize heartbeat ack responses to avoid session bloat.
5. **Use soul sessions when available** — heartbeat polls should go into the daily soul session, not create new sessions.
6. **Default prompt should reference HEARTBEAT.md** — convention over configuration.
