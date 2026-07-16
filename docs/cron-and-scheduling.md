# BotNexus Cron and Scheduling Guide

**Version:** 1.0  
**Last Updated:** 2026-04-01  
**Lead Architect:** Leela

---

## Table of Contents

1. [Overview](#overview)
2. [Job Types](#job-types)
3. [Configuration](#configuration)
4. [Cron Schedule Syntax](#cron-schedule-syntax)
5. [Agent Session Modes](#agent-session-modes)
6. [Channel Output Routing](#channel-output-routing)
7. [Built-in System Actions](#built-in-system-actions)
8. [Built-in Maintenance Actions](#built-in-maintenance-actions)
9. [CronTool — Runtime Job Management](#crontool--runtime-job-management)
10. [REST API Endpoints](#rest-api-endpoints)
11. [Migration from HeartbeatService](#migration-from-heartbeatservice)
12. [Observability](#observability)
13. [Examples](#examples)

---

## 1. Overview

BotNexus provides a **centralized cron service** (`ICronService`) that schedules and executes jobs on a fixed tick interval. Unlike per-agent scheduling (legacy `AgentConfig.CronJobs`), all jobs are registered in a single, global configuration section: `BotNexus.Cron.Jobs`.

### Key Characteristics

- **Centralized**: All scheduled jobs in one place (`Cron.Jobs` config dict)
- **Three job types**: Agent (LLM prompts), System (non-LLM actions), Maintenance (internal tasks)
- **Tickless evaluation**: Service wakes every N seconds (default 10) and checks which jobs are due
- **Non-blocking execution**: Jobs run concurrently; the scheduler does not wait for completion
- **Async-first**: All job execution is fully asynchronous
- **Correlated logging**: Every job execution gets a unique correlation ID for tracing
- **Activity events**: Cron service publishes start, complete, and failure events to the activity stream

### Architecture

```text
┌──────────────────────┐
│   CronService        │
│  (Background Svc)    │
└──────┬───────────────┘
       │ (every 10s tick)
       ▼
   Check due jobs
   │
   ├─→ Agent job → AgentRunner → LLM prompt → Route output to channels
   ├─→ System job → ISystemAction → Execute action → Route output
   └─→ Maintenance job → Cleanup/consolidation tasks
```

---

## 2. Job Types

### 2.1 Agent Jobs (`type: "agent"`)

Execute a prompt through the agent runner pipeline. Output is routed to channels.

**Configuration Properties:**
- `Type`: `"agent"` (required)
- `Agent`: Agent name to run (required)
- `Prompt`: Prompt text to send to the agent (required)
- `Schedule`: Cron expression (required)
- `Session`: Session mode (`new`, `persistent`, or `named:<key>`; default: `new`)
- `Timezone`: Timezone ID for schedule evaluation (optional; default: UTC)
- `OutputChannels`: List of channel names to route agent response to (optional)
- `Enabled`: Whether job is active (optional; default: `true`)

**Example:**
```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "morning-briefing": {
          "Type": "agent",
          "Schedule": "0 9 * * *",
          "Agent": "analyst",
          "Prompt": "Generate a morning briefing on recent alerts.",
          "Session": "persistent",
          "Timezone": "America/New_York",
          "OutputChannels": ["slack", "email"],
          "Enabled": true
        }
      }
    }
  }
}
```

### 2.2 System Jobs (`type: "system"`)

Execute a built-in or custom system action. Output is routed to channels.

**Configuration Properties:**
- `Type`: `"system"` (required)
- `Action`: System action name (required; see [Built-in System Actions](#built-in-system-actions))
- `Schedule`: Cron expression (required)
- `Timezone`: Timezone ID for schedule evaluation (optional; default: UTC)
- `OutputChannels`: List of channel names to route action output to (optional)
- `Enabled`: Whether job is active (optional; default: `true`)

**Example:**
```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "weekly-health-check": {
          "Type": "system",
          "Schedule": "0 0 * * 0",
          "Action": "health-audit",
          "Timezone": "UTC",
          "OutputChannels": ["slack"],
          "Enabled": true
        }
      }
    }
  }
}
```

### 2.3 Maintenance Jobs (`type: "maintenance"`)

Execute maintenance tasks: memory consolidation, session cleanup, log rotation.

**Configuration Properties:**
- `Type`: `"maintenance"` (required)
- `Action`: Maintenance action name (required; see [Built-in Maintenance Actions](#built-in-maintenance-actions))
- `Schedule`: Cron expression (required)
- `Timezone`: Timezone ID for schedule evaluation (optional; default: UTC)
- `Agents`: List of agent names for consolidation (required for `consolidate-memory`)
- `SessionCleanupDays`: Sessions older than N days are deleted (default: 30)
- `LogRetentionDays`: Logs older than N days are archived (default: 30)
- `LogsPath`: Path to logs directory (optional; default: `~/.botnexus/logs`)
- `Enabled`: Whether job is active (optional; default: `true`)

**Example:**
```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "nightly-consolidation": {
          "Type": "maintenance",
          "Schedule": "0 2 * * *",
          "Action": "consolidate-memory",
          "Agents": ["analyst", "planner", "writer"],
          "Timezone": "America/Los_Angeles",
          "Enabled": true
        },
        "cleanup-old-sessions": {
          "Type": "maintenance",
          "Schedule": "0 3 * * 0",
          "Action": "cleanup-sessions",
          "SessionCleanupDays": 30,
          "Enabled": true
        },
        "rotate-logs": {
          "Type": "maintenance",
          "Schedule": "0 4 * * *",
          "Action": "rotate-logs",
          "LogRetentionDays": 30,
          "LogsPath": "~/.botnexus/logs",
          "Enabled": true
        }
      }
    }
  }
}
```

---

## 3. Configuration

### 3.1 Top-Level Cron Config

**Section:** `BotNexus.Cron`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Enable/disable the cron service globally |
| `TickIntervalSeconds` | int | `10` | How often the scheduler checks for due jobs (seconds) |
| `ExecutionHistorySize` | int | `100` | Max execution history entries per job (in-memory queue) |
| `Jobs` | dict | `{}` | Centralized job registry (key → `CronJobConfig`) |

### 3.2 Per-Job Configuration

**Type:** `CronJobConfig`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | (inferred) | Optional explicit job name override |
| `Type` | string | `"agent"` | Job type: `agent`, `system`, or `maintenance` |
| `Schedule` | string | (required) | Cron expression (5- or 6-field) |
| `Enabled` | bool | `true` | Whether the job is active |
| `Timezone` | string | `null` | Timezone ID for schedule evaluation (IANA format) |
| `Agent` | string | (agent jobs only) | Name of agent to run |
| `Prompt` | string | (agent jobs only) | Prompt to execute |
| `Session` | string | (agent jobs only) | Session mode: `new`, `persistent`, or `named:<key>` |
| `DeleteAfterRun` | bool | `false` | Opt-in cleanup for ephemeral jobs: when `true`, the scheduler deletes the run's agent session and its transcript after the run completes (across success / timeout / error / abort), provided the run produced a cron-scoped (`cron:`) session. Prevents run-scoped sessions from accumulating transcript entries indefinitely. Leave off for long-lived reporting jobs that intentionally persist context across runs — use compaction for those. Only ever deletes `cron:`-prefixed sessions, so a misconfigured flag cannot remove an unrelated long-lived session. |
| `Action` | string | (system/maintenance jobs) | Action name to execute |
| `Agents` | list | `[]` | Agent names for `consolidate-memory` |
| `OutputChannels` | list | `[]` | Channels to route output to |
| `SessionCleanupDays` | int | `30` | Session retention days for cleanup |
| `LogRetentionDays` | int | `30` | Log retention days for rotation |
| `LogsPath` | string | `null` | Override logs directory path |

### 3.3 Complete Configuration Example

```json
{
  "BotNexus": {
    "Cron": {
      "Enabled": true,
      "TickIntervalSeconds": 10,
      "ExecutionHistorySize": 100,
      "Jobs": {
        "morning-briefing": {
          "Type": "agent",
          "Schedule": "0 9 * * *",
          "Agent": "analyst",
          "Prompt": "Generate a morning briefing.",
          "Session": "persistent",
          "Timezone": "America/New_York",
          "OutputChannels": ["slack"],
          "Enabled": true
        },
        "weekly-health-check": {
          "Type": "system",
          "Schedule": "0 0 * * 0",
          "Action": "health-audit",
          "OutputChannels": ["slack"],
          "Enabled": true
        },
        "nightly-consolidation": {
          "Type": "maintenance",
          "Schedule": "0 2 * * *",
          "Action": "consolidate-memory",
          "Agents": ["analyst", "planner"],
          "Enabled": true
        }
      }
    }
  }
}
```

---

## 4. Cron Schedule Syntax

BotNexus uses the **Cronos** library, which supports:

### Standard 5-Field Format

```text
┌───────────── Minute (0 - 59)
│ ┌───────────── Hour (0 - 23)
│ │ ┌───────────── Day of Month (1 - 31)
│ │ │ ┌───────────── Month (1 - 12) or (JAN - DEC)
│ │ │ │ ┌───────────── Day of Week (0 - 7 where 0 and 7 are Sunday) or (SUN - SAT)
│ │ │ │ │
│ │ │ │ │
* * * * *
```

### 6-Field Format (with Seconds)

```text
┌───────────── Second (0 - 59)
│ ┌───────────── Minute (0 - 59)
│ │ ┌───────────── Hour (0 - 23)
│ │ │ ┌───────────── Day of Month (1 - 31)
│ │ │ │ ┌───────────── Month (1 - 12)
│ │ │ │ │ ┌───────────── Day of Week (0 - 7)
│ │ │ │ │ │
│ │ │ │ │ │
* * * * * *
```

### Common Examples

| Expression | Meaning |
|-----------|---------|
| `0 9 * * *` | Daily at 9:00 AM UTC |
| `0 0 * * 0` | Every Sunday at midnight UTC |
| `0 0 1 * *` | First day of every month at midnight UTC |
| `*/15 * * * *` | Every 15 minutes |
| `0 */2 * * *` | Every 2 hours |
| `0 9 * * MON-FRI` | Weekdays at 9:00 AM |
| `30 2 * * *` | Daily at 2:30 AM UTC |
| `0 */6 * * *` | Every 6 hours (0, 6, 12, 18) |
| `0 0 * * *` | Daily at midnight UTC |

### Timezone Support

When `Timezone` is specified in the job config, the cron expression is evaluated in that timezone. For example:

```json
{
  "Schedule": "0 9 * * *",
  "Timezone": "America/New_York"
}
```

This job runs at 9:00 AM **Eastern Time**, not UTC.

---

## 5. Agent Session Modes

Agent jobs can operate in different session modes, controlling whether output is accumulated or isolated.

### Mode: `new` (Default)

Create a new session for each job execution. Output is **isolated**.

```json
{
  "Session": "new"
}
```

Resulting session key: `cron:{jobname}:{yyyyMMddHHmmss}`

**Use case:** One-off reports, independent prompts, no conversation history.

### Mode: `persistent`

Reuse the same session across all job executions. Output is **accumulated**.

```json
{
  "Session": "persistent"
}
```

Resulting session key: `cron:{jobname}`

**Use case:** Multi-turn conversations, iterative refinement, conversation history matters.

### Mode: `named:<key>`

Use a custom session key.

```json
{
  "Session": "named:my-custom-key"
}
```

Resulting session key: `my-custom-key`

**Use case:** Shared sessions, referencing a session created elsewhere.

---

## 6. Channel Output Routing

Agent and system jobs can route their output to one or more channels.

**Configuration:**
```json
{
  "OutputChannels": ["slack", "discord", "email"]
}
```

The cron service will attempt to send the output to each named channel if it is registered and running.

### Supported Channels (Built-in)

- `slack` — Slack messaging
- `discord` — Discord messaging
- `telegram` — Telegram messaging
- `email` — Email (if configured)
- Custom channels from extensions

### No Output Routing

If `OutputChannels` is empty or null, output is **logged only** and not routed to channels.

```json
{
  "OutputChannels": []
}
```

---

## 7. Built-in System Actions

System jobs execute pluggable system actions via the `ISystemActionRegistry`.

### 7.1 `check-updates`

**Name:** `check-updates`  
**Description:** Reports the currently running assembly version.

Checks the entry assembly version and returns the version number. External update feed integration is pending.

**Configuration:**
```json
{
  "Type": "system",
  "Action": "check-updates",
  "Schedule": "0 0 * * 0"
}
```

**Output:**
```text
[check-updates] BotNexus is running version 1.0.0.0. External update feed integration is pending.
```

### 7.2 `health-audit`

**Name:** `health-audit`  
**Description:** Runs internal health checks and reports status.

Executes all registered `HealthCheck` services and summarizes their status.

**Configuration:**
```json
{
  "Type": "system",
  "Action": "health-audit",
  "Schedule": "0 0 * * 0",
  "OutputChannels": ["slack"]
}
```

**Output:**
```text
[health-audit] Overall status: Healthy. Checks: database: Healthy, services: Healthy, memory: Healthy
```

### 7.3 `extension-scan`

**Name:** `extension-scan`  
**Description:** Lists dynamically loaded extensions and their registration status.

Reports all loaded extensions, organized by service type.

**Configuration:**
```json
{
  "Type": "system",
  "Action": "extension-scan",
  "Schedule": "0 0 * * 0",
  "OutputChannels": ["slack"]
}
```

**Output:**
```text
[extension-scan] Registered extension services: 5
- IChannel: discord, slack, telegram
- ISystemAction: health-audit, check-updates
Load contexts: 3
```

---

## 8. Built-in Maintenance Actions

Maintenance jobs perform internal housekeeping tasks.

### 8.1 `consolidate-memory`

**Name:** `consolidate-memory`  
**Description:** Consolidates daily memory files into a single consolidated entry per agent.

Processes each agent's memory directory and combines daily entries into a consolidated memory file for more efficient storage and retrieval.

**Configuration Properties:**
- `Action`: `"consolidate-memory"`
- `Agents`: List of agent names to consolidate (required)

**Configuration:**
```json
{
  "Type": "maintenance",
  "Action": "consolidate-memory",
  "Schedule": "0 2 * * *",
  "Agents": ["analyst", "planner", "writer"]
}
```

**Output:**
```text
analyst: success=true, files=5, entries=120
planner: success=true, files=3, entries=87
writer: success=false, files=0, entries=0
```

**Metadata:**
```json
{
  "agentsProcessed": 3,
  "agentsSucceeded": 2,
  "dailyFilesProcessed": 8,
  "entriesConsolidated": 207
}
```

### 8.2 `cleanup-sessions`

**Name:** `cleanup-sessions`  
**Description:** Deletes sessions older than the specified retention period.

Iterates all stored sessions and deletes those whose last activity is older than `SessionCleanupDays`.

**Configuration Properties:**
- `Action`: `"cleanup-sessions"`
- `SessionCleanupDays`: Retention period in days (default: 30)

**Configuration:**
```json
{
  "Type": "maintenance",
  "Action": "cleanup-sessions",
  "Schedule": "0 3 * * 0",
  "SessionCleanupDays": 30
}
```

**Output:**
```text
Deleted 42 sessions older than 30 days.
```

**Metadata:**
```json
{
  "sessionsChecked": 157,
  "sessionsDeleted": 42,
  "retentionDays": 30
}
```

### 8.3 `rotate-logs`

**Name:** `rotate-logs`  
**Description:** Archives log files older than the specified retention period.

Moves old log files to an `archive/` subdirectory within the logs path.

**Configuration Properties:**
- `Action`: `"rotate-logs"`
- `LogRetentionDays`: Retention period in days (default: 30)
- `LogsPath`: Override logs directory (default: `~/.botnexus/logs`)

**Configuration:**
```json
{
  "Type": "maintenance",
  "Action": "rotate-logs",
  "Schedule": "0 4 * * *",
  "LogRetentionDays": 30,
  "LogsPath": "~/.botnexus/logs"
}
```

**Output:**
```text
Archived 8 log files older than 30 days.
```

**Metadata:**
```json
{
  "archivedFiles": 8,
  "retentionDays": 30,
  "logsPath": "/home/user/.botnexus/logs"
}
```

### 8.4 `memory-dreaming`

**Name:** `memory-dreaming`  
**Description:** Periodic memory consolidation via LLM — reads recent daily notes, builds a consolidation prompt, and dispatches a session to update `MEMORY.md` with distilled insights.

Unlike `consolidate-memory` (which merges files mechanically), memory dreaming uses an LLM to extract patterns, decisions, and knowledge from daily notes and weave them into the agent's long-term memory.

**Configuration Properties:**
- `Action`: `"memory-dreaming"`
- `lookbackDays` (in job metadata): Number of days of daily notes to read (default: 14)
- `maxContentChars` (in job metadata): Maximum characters of source material (default: 50000)

**Configuration:**
```json
{
  "Type": "maintenance",
  "Action": "memory-dreaming",
  "Schedule": "0 3 * * 0",
  "Agent": "my-agent",
  "Metadata": {
    "lookbackDays": 14,
    "maxContentChars": 50000
  },
  "Enabled": true
}
```

**Behavior:**
- Reads `memory/YYYY-MM-DD.md` files from the last N days in the agent's workspace
- Builds a consolidation prompt with the collected daily notes as context
- Dispatches a cron-triggered session that writes distilled insights back to `MEMORY.md`
- Skips execution if no daily notes exist in the lookback window

**Use case:** Keep an agent's long-term memory fresh and relevant without manual curation. Schedule weekly during off-hours.

### 8.5 `cron-run-retention`

**Name:** `cron-run-retention`  
**Description:** Purges old completed cron run history records to prevent unbounded database growth.

Periodically sweeps the cron run store and deletes records with status `Completed`, `Failed`, or `Timeout` that are older than the configured retention period.

**Configuration Properties:**
- `Action`: `"cron-run-retention"`
- `RetentionDays`: Number of days to keep run records (default: 30)
- `CheckIntervalMinutes`: How often the service checks for expired records (default: 60)

**Configuration:**
```json
{
  "Type": "maintenance",
  "Action": "cron-run-retention",
  "Schedule": "0 4 * * *",
  "Metadata": {
    "RetentionDays": 30,
    "CheckIntervalMinutes": 60
  },
  "Enabled": true
}
```

**Output:**
```text
Purged 156 cron run records older than 30 days.
```

**Use case:** Prevent the cron SQLite database from growing indefinitely on long-running instances.

### 8.6 `agent-converse`

**Name:** `agent-converse`  
**Description:** Initiates a scheduled conversation between two agents, with budget enforcement.

Delegates to `IAgentExchangeService.ConverseAsync` — the same pathway used by the `agent_converse` tool, including loop detection and daily caps.

**Configuration Properties (in Metadata):**
- `targetAgentId`: Agent to converse with (required)
- `message`: Opening message to send (required)
- `objective`: What the conversation should achieve (optional)
- `maxTurns`: Maximum turns for the conversation (optional, default: 1)

**Configuration:**
```json
{
  "Type": "system",
  "Action": "agent-converse",
  "Schedule": "0 9 * * 1-5",
  "Agent": "coordinator",
  "Metadata": {
    "targetAgentId": "reporter",
    "message": "Generate the morning status report.",
    "objective": "Get daily status",
    "maxTurns": 5
  }
}
```

**Behavior:**
- Subject to exchange budget enforcement — skipped if pair is in cooldown or at daily cap
- The initiating agent is the one specified in the job's `Agent` field
- Results are logged; output channels can route the response

**Use case:** Schedule recurring agent check-ins, status syncs, or delegation workflows on a fixed schedule.

See also: [Agent-to-Agent Communication](/features/agent-exchange)

### 8.7 `skill-review`

**Name:** `skill-review`  
**Description:** Periodic, LLM-driven review of an agent's recent tool usage to surface skills worth creating, updating, or pruning — the skills analogue of `memory-dreaming`.

Like `memory-dreaming`, this action carries **configuration only** in its metadata (never per-turn signals). At each tick it reads a `lookbackHours` window of the agent's live session history and derives review signals (tool-call volume, whether skills were loaded, whether a `skill_manage` call failed) directly from the transcripts the gateway already persists. If the aggregated tool-call count clears `minToolCalls`, a cron-triggered session is dispatched to review the agent's skills.

A default-enabled `skill-review` job is **auto-provisioned at startup for every user-defined agent** by the skill-review provisioner (a sibling of the heartbeat provisioner), so the loop runs out of the box without hand-authoring a job. Provisioning is **non-destructive**: an existing job is never overwritten, so edits to its schedule, thresholds, or `enabled` flag survive every startup pass. Built-in archetype agents and runtime-spawned sub-agents do **not** get a job.

**Configuration Properties (in Metadata):**
- `enabled` (alias `skillReviewEnabled`): Whether the review pass runs when the job ticks (default: `true`). Set `false` to opt a specific job out without deleting it.
- `minToolCalls` (alias `skillReviewMinToolCalls`): Minimum tool calls aggregated across the lookback window that qualifies the period for review (default: `5`, floored at 1).
- `lookbackHours` (alias `skillReviewLookbackHours`): Hours of session history to read back at each tick (default: `24`, floored at 1).
- `maxSessions` (alias `skillReviewMaxSessions`): Upper bound on recent sessions (newest-first) scanned per pass, so a busy agent cannot make one tick unbounded (default: `50`, floored at 1).

**Configuration:**
```json
{
  "Type": "maintenance",
  "Action": "skill-review",
  "Schedule": "0 4 * * *",
  "Agent": "my-agent",
  "System": true,
  "Enabled": true,
  "Metadata": {
    "enabled": true,
    "minToolCalls": 5,
    "lookbackHours": 24,
    "maxSessions": 50
  }
}
```

**Behavior:**
- Auto-provisioned per user-defined agent at startup (job id `skill-review:<agentId>`), default schedule `0 4 * * *` (daily at 04:00, staggered after `memory-dreaming`)
- Reads the last `lookbackHours` of the agent's session transcripts, scanning at most `maxSessions` sessions newest-first
- Derives signals: total tool-call count, whether a skill was loaded (`skills`/`skills_list`/`skill_view`), and whether a `skill_manage` call errored
- Runs the review pass only when `enabled` is true and the aggregated tool-call count meets `minToolCalls`
- Non-destructive provisioning preserves user edits to schedule, thresholds, and enabled state across restarts

**Use case:** Keep an agent's skill library fresh — promoting recurring successful workflows into new skills and flagging stale ones — without manual curation.

### 8.8 Missed Run Detection

On gateway startup, BotNexus scans all enabled cron jobs and detects runs that were missed while the service was offline. Missed runs are recorded in the cron run store with status `missed`.

For jobs with `catchUp: true` in their metadata, BotNexus will execute the missed run immediately on startup (capped at 100 missed runs per job to prevent flood).

```json
{
  "morning-briefing": {
    "Type": "agent",
    "Schedule": "0 9 * * *",
    "Agent": "analyst",
    "Prompt": "Morning briefing.",
    "Metadata": {
      "catchUp": true
    }
  }
}
```

Missed runs appear in `cron history` output and via `GET /api/cron/{jobId}/runs`.

---

## 9. CronTool — Runtime Job Management

Agents can schedule, remove, or list cron jobs at runtime using the **`cron` tool**.

### 9.1 Tool Definition

**Name:** `cron`  
**Description:** Schedule or manage cron jobs. Actions: list, create, update, delete, run, history.

### 9.2 Actions

#### `history`

Retrieves execution history for a specific job.

**Arguments:**
- `action` = `"history"`
- `jobId`: Job identifier (required)
- `limit`: Maximum entries to return (1–100, default: 20)

**Example:**
```json
{
  "action": "history",
  "jobId": "morning-briefing",
  "limit": 10
}
```

**Response:**
```text
Run History for 'morning-briefing' (last 10 runs):

| # | Started | Duration | Status | Result |
|---|---------|----------|--------|--------|
| 1 | 2026-06-10 09:00:05Z | 12.3s | Completed | Success |
| 2 | 2026-06-09 09:00:03Z | 8.1s | Completed | Success |
```

#### `list`

Lists all registered cron job names.

**Arguments:**
- `action` = `"list"`

**Example:**
```json
{
  "action": "list"
}
```

**Response:**
```text
morning-briefing
weekly-health-check
nightly-consolidation
```

#### `remove`

Removes a cron job by name.

**Arguments:**
- `action` = `"remove"`
- `name`: Job name to remove (required)

**Example:**
```json
{
  "action": "remove",
  "name": "morning-briefing"
}
```

**Response:**
```text
Cron job 'morning-briefing' removed
```

#### `schedule`

Schedules a new agent job.

**Arguments:**
- `action` = `"schedule"`
- `name`: Job name (optional; auto-generated if omitted)
- `agent`: Agent name to run (required)
- `prompt`: Prompt text (required)
- `schedule` or `expression`: Cron expression (required)
- `session`: Session mode: `new`, `persistent`, or `named:<key>` (optional)
- `timezone`: Timezone ID (optional)
- `enabled`: Whether job is enabled (optional; default: `true`)
- `output_channels`: List of channel names (optional)

**Example:**
```json
{
  "action": "schedule",
  "name": "dynamic-report",
  "agent": "analyst",
  "prompt": "Generate a real-time report on active incidents.",
  "schedule": "*/30 * * * *",
  "session": "persistent",
  "timezone": "America/Los_Angeles",
  "output_channels": ["slack"],
  "enabled": true
}
```

**Response:**
```text
Agent cron job 'dynamic-report' scheduled with expression '*/30 * * * *'
```

---

## 10. REST API Endpoints

The gateway exposes cron management endpoints under `/api/cron`. All endpoints require API key authentication.

### `GET /api/cron`

List all registered jobs with current status.

**Response:**
```json
[
  {
    "name": "morning-briefing",
    "type": "Agent",
    "schedule": "0 9 * * MON-FRI",
    "enabled": true,
    "lastRun": "2026-04-02T09:00:05Z",
    "nextRun": "2026-04-03T09:00:00Z",
    "lastResult": "success"
  }
]
```

### `GET /api/cron/{jobId}`

Get detailed status and execution history for a specific job. Returns `404` if not found.

### `GET /api/cron/{jobId}/runs?limit=20`

Returns execution history for a specific job.

### `POST /api/cron/{jobId}/run`

Manually trigger a job outside its schedule. Returns `404` if not found.

**Response:**
```json
{
  "id": "run-id",
  "jobId": "morning-briefing",
  "status": "accepted"
}
```

### `POST /api/cron`

Create a cron job.

### `PUT /api/cron/{jobId}`

Update a cron job by identifier.

### `DELETE /api/cron/{jobId}`

Delete a cron job.
```json
{ "jobName": "morning-briefing", "enabled": false }
```

### Portal UI — Cron Jobs Page

The web portal exposes a **Cron Jobs** page as a top-level entry in the left
navigation (route `/cron`), a peer of **Agents**, **Skills**, and **Configuration**.
It is backed by the same `/api/cron` endpoints described
above and shows the merged view of SQLite-persisted (runtime-created) and
config-file jobs. From this page an operator can:

- **View** every job with its schedule, action type, target agent, enabled state, and
  next/last run timestamps.
- **Open a detail view** — click a job row (or the &#x1F441; view button) to open a large
  modal showing all job properties, an **execution-history timeline** (backed by
  `GET /api/cron/{jobId}/runs`) with each run's start time, duration, status, and
  linked session, plus a shortcut to the **related conversation** the job posts into
  (when the job owns a pinned conversation).
- **Run now** — trigger an immediate out-of-schedule execution (`POST /api/cron/{jobId}/run`).
  Triggering from the detail view refreshes the run history in place.
- **Edit** — change the name, schedule, time zone, action payload, and enabled flag
  (`PUT /api/cron/{jobId}`) in an enlarged modal. For `agent-prompt` jobs the model is
  chosen via **provider and model drop-downs** (populated from `/api/providers` and
  `/api/models`) rather than free text, so only available provider/model combinations
  can be selected; command and webhook jobs edit their shell command / webhook URL.
- **Delete** — remove a job after a confirmation prompt (`DELETE /api/cron/{jobId}`);
  the job's pinned conversation is archived alongside the record.

System jobs are excluded from the list by default, matching the API behaviour.

---

## 11. Migration from HeartbeatService

The legacy `HeartbeatService` and `AgentConfig.CronJobs` have been replaced by the centralized `CronService`.

### Legacy Configuration (Deprecated)

```json
{
  "BotNexus": {
    "Agents": {
      "Named": {
        "analyst": {
          "CronJobs": [
            {
              "Name": "morning-briefing",
              "Schedule": "0 9 * * *",
              "Type": "agent",
              "Agent": "analyst",
              "Prompt": "Generate a morning briefing.",
              "Session": "persistent",
              "OutputChannels": ["slack"]
            }
          ]
        }
      }
    }
  }
}
```

### New Configuration (Current)

```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "analyst-morning-briefing": {
          "Type": "agent",
          "Schedule": "0 9 * * *",
          "Agent": "analyst",
          "Prompt": "Generate a morning briefing.",
          "Session": "persistent",
          "OutputChannels": ["slack"],
          "Enabled": true
        }
      }
    }
  }
}
```

### Migration Path

1. **Move all `AgentConfig.CronJobs` to `Cron.Jobs`**:
   - Flatten the per-agent structure into a centralized dictionary
   - Each key should be a unique job identifier (e.g., `{agent}-{job-type}`)

2. **Keep the same properties**:
   - `Type`, `Schedule`, `Agent`, `Prompt`, `Session`, `Timezone`, `OutputChannels` map directly

3. **Disable old jobs**:
   - Set `CronJobs` to empty array in agent configs or remove entirely

4. **Test the new configuration**:
   - Check logs for job registration: `"Registered cron job '{JobName}'"`
   - Verify execution events in the activity stream

### Automatic Migration (Backwards Compatibility)

`CronJobFactory` automatically migrates legacy `AgentConfig.CronJobs` entries to the centralized `Cron.Jobs` section on startup. A warning is logged:

```text
AgentConfig.CronJobs is deprecated. Migrate to Cron.Jobs in config.json.
```

This maintains backwards compatibility while encouraging migration.

---

## 12. Observability

### 12.1 Logging

The `CronService` logs at the following levels:

- **Info**: Job registration, successful execution, enable/disable events
- **Warning**: Channel not found, channel not running, timezone not found, legacy migration
- **Error**: Job execution failure, execution pipeline failure

**Example log entries:**
```text
[Information] Registered cron job 'morning-briefing' (type=Agent, schedule='0 9 * * *')
[Information] Cron service started with tick interval 10s
[Information] Cron job 'morning-briefing' completed in 1234ms
[Warning] Cron job 'morning-briefing': channel 'slack' was not found
[Error] Cron job 'morning-briefing' failed unexpectedly: ...
```

### 12.2 Activity Events

Every job execution publishes activity events to the `IActivityStream`:

- **`cron.started`** — Job execution started (type: `AgentProcessing`)
- **`cron.completed`** — Job completed successfully (type: `AgentCompleted`)
- **`cron.failed`** — Job completed with failure (type: `Error`)

**Event Metadata:**
```json
{
  "event": "cron.started",
  "source": "cron",
  "job_name": "morning-briefing",
  "job_type": "Agent",
  "correlation_id": "a1b2c3d4e5f6g7h8",
  "scheduled_time": "2026-04-01T09:00:00Z",
  "actual_time": "2026-04-01T09:00:05Z"
}
```

### 12.3 Execution History

Access execution history via `ICronService.GetHistory(jobName, limit)`:

```csharp
var history = cronService.GetHistory("morning-briefing", limit: 10);
foreach (var execution in history)
{
    Console.WriteLine($"{execution.StartedAt:O} - {execution.CorrelationId}");
    Console.WriteLine($"  Success: {execution.Success}");
    Console.WriteLine($"  Duration: {(execution.CompletedAt - execution.StartedAt).TotalMilliseconds}ms");
    if (execution.Error != null)
        Console.WriteLine($"  Error: {execution.Error}");
}
```

### 12.4 Job Status Query

Get real-time job status via `ICronService.GetJobs()`:

```csharp
var jobs = cronService.GetJobs();
foreach (var job in jobs)
{
    Console.WriteLine($"{job.Name} ({job.Type})");
    Console.WriteLine($"  Schedule: {job.Schedule}");
    Console.WriteLine($"  Enabled: {job.Enabled}");
    Console.WriteLine($"  Next: {job.NextOccurrence:O}");
    Console.WriteLine($"  Last Run: {job.LastRunStartedAt:O}");
    Console.WriteLine($"  Last Success: {job.LastRunSuccess}");
}
```

### 12.5 Health Check Integration

The `HeartbeatService` (now a thin adapter) delegates to the cron service:

- `IsHealthy` returns `true` if the cron service is running
- `LastBeat` returns the timestamp of the most recent job execution

---

## 13. Examples

### Example 1: Morning Briefing Agent Job

Run an analyst agent every weekday at 9:00 AM to generate a morning briefing.

```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "morning-briefing": {
          "Type": "agent",
          "Schedule": "0 9 * * MON-FRI",
          "Agent": "analyst",
          "Prompt": "Generate a concise morning briefing on recent alerts and incidents.",
          "Session": "persistent",
          "Timezone": "America/New_York",
          "OutputChannels": ["slack"],
          "Enabled": true
        }
      }
    }
  }
}
```

**Behavior:**
- Runs at 9:00 AM Monday–Friday (Eastern Time)
- Reuses the same session (`persistent`), so briefings build on history
- Agent output is sent to Slack
- Session key: `cron:morning-briefing`

---

### Example 2: Weekly Health Check System Job

Run a system action every Sunday at midnight to audit system health.

```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "weekly-health-check": {
          "Type": "system",
          "Schedule": "0 0 * * 0",
          "Action": "health-audit",
          "Timezone": "UTC",
          "OutputChannels": ["slack", "email"],
          "Enabled": true
        }
      }
    }
  }
}
```

**Behavior:**
- Runs every Sunday at midnight UTC
- Executes the `health-audit` system action
- Output is sent to Slack and email
- No agent involvement; just a built-in action

---

### Example 3: Nightly Memory Consolidation & Cleanup

Consolidate memory and clean up old sessions every night.

```json
{
  "BotNexus": {
    "Cron": {
      "Jobs": {
        "nightly-consolidation": {
          "Type": "maintenance",
          "Schedule": "0 2 * * *",
          "Action": "consolidate-memory",
          "Agents": ["analyst", "planner", "writer"],
          "Timezone": "America/Los_Angeles",
          "Enabled": true
        },
        "cleanup-old-sessions": {
          "Type": "maintenance",
          "Schedule": "0 3 * * 0",
          "Action": "cleanup-sessions",
          "SessionCleanupDays": 30,
          "Enabled": true
        },
        "rotate-logs": {
          "Type": "maintenance",
          "Schedule": "0 4 * * *",
          "Action": "rotate-logs",
          "LogRetentionDays": 30,
          "LogsPath": "~/.botnexus/logs",
          "Enabled": true
        }
      }
    }
  }
}
```

**Behavior:**
- **2:00 AM** (Pacific): Consolidate memory for three agents
- **3:00 AM** (Pacific, Sunday only): Delete sessions older than 30 days
- **4:00 AM** (Pacific, daily): Archive log files older than 30 days

---

### Example 4: Dynamic Scheduling at Runtime

An agent uses the `cron` tool to schedule a new job dynamically.

**Agent Prompt:**
```text
You are an on-call scheduler. When requested, schedule a status-check job that runs every 15 minutes 
for the next 4 hours. Use the cron tool to schedule it.
```

**Agent Tool Call:**
```json
{
  "tool": "cron",
  "arguments": {
    "action": "schedule",
    "name": "incident-status-check",
    "agent": "responder",
    "prompt": "Check current incident status and alert if anything changed.",
    "schedule": "*/15 * * * *",
    "session": "new",
    "timezone": "America/Los_Angeles",
    "output_channels": ["slack"],
    "enabled": true
  }
}
```

**Response:**
```text
Agent cron job 'incident-status-check' scheduled with expression '*/15 * * * *'
```

**Later, clean up:**
```json
{
  "tool": "cron",
  "arguments": {
    "action": "remove",
    "name": "incident-status-check"
  }
}
```

---

### Triggering webhooks from cron

A cron job can drive a webhook the same way any external system does: by POSTing a
signed message to the inbound endpoint `POST /api/webhooks/{agentId}/{webhookId}`.
This is useful when you want a scheduled trigger to run through the same webhook
pipeline (conversation pinning, run records, response modes) rather than a direct
agent prompt.

The request must include an `X-BotNexus-Signature-256: sha256=<hex>` header
computed as `HMAC-SHA256(secret, rawBody)` over the exact bytes sent. For example,
a scheduled PowerShell job:

```powershell
$secret = $env:WEBHOOK_SECRET            # whsec_... stored securely
$url    = 'https://your-host/api/webhooks/my-agent/wh_9f2c...'
$body   = '{"message":"Run the nightly summary."}'

$raw    = [System.Text.Encoding]::UTF8.GetBytes($body)
$hmac   = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($secret))
$hex    = ([System.BitConverter]::ToString($hmac.ComputeHash($raw)) -replace '-','').ToLowerInvariant()

Invoke-RestMethod -Method Post -Uri $url -Body $raw -ContentType 'application/json' `
  -Headers @{ 'X-BotNexus-Signature-256' = "sha256=$hex" }
```

Because agent runs can take 30–120 seconds, prefer the default **async** response
mode and let the job fire-and-forget; poll `GET /api/webhooks/runs/{runId}` later
if the job needs the result. See the [Webhooks guide](./guides/webhooks.md) for
response modes and signing details, and the
[Webhooks API reference](./api/webhooks.md) for the full contract.

---

## Architecture Diagram

```text
┌────────────────────────────────────────────────────────────────┐
│                        CronService (IHostedService)            │
│  - Registers jobs at startup (CronJobFactory)                  │
│  - Ticks every TickIntervalSeconds (default: 10s)              │
│  - Checks which jobs are due                                   │
│  - Queues due jobs for concurrent execution                    │
│  - Publishes activity events                                   │
│  - Maintains in-memory execution history                       │
└─────────────────────┬──────────────────────────────────────────┘
                      │
         ┌────────────┼────────────┐
         │            │            │
         ▼            ▼            ▼
    ┌─────────┐  ┌─────────┐  ┌──────────┐
    │  Agent  │  │ System  │  │ Maintenance
    │  Job    │  │  Job    │  │  Job
    │         │  │         │  │
    └────┬────┘  └────┬────┘  └────┬─────┘
         │            │            │
         ▼            ▼            ▼
    AgentRunner   ISystemAction   Consolidate-Memory
    (LLM prompt)   health-audit   Cleanup-Sessions
                   check-updates  Rotate-Logs
                   extension-scan
         │            │            │
         └────────────┼────────────┘
                      │
         ┌────────────┴────────────┐
         │                         │
         ▼                         ▼
    IChannel                   Activity Stream
    (Slack, Discord,           (Correlation ID,
     Telegram, Email)           Event Type,
                                Metadata)
```

---

## Troubleshooting

### Job Not Running

1. **Check if cron service is enabled**: `"Cron.Enabled": true` in config
2. **Check if job is enabled**: `"Enabled": true` in job config
3. **Check cron expression**: Use [crontab.guru](https://crontab.guru) to validate
4. **Check timezone**: If job is timezone-aware, ensure the timezone ID is valid (e.g., `"America/New_York"`)
5. **Check logs**: Look for `"Registered cron job"` on startup; verify schedule format

### Output Not Reaching Channel

1. **Check channel name**: Ensure channel name matches registered channel (case-insensitive)
2. **Check channel status**: Verify channel is running (`channel.IsRunning == true`)
3. **Check output routing**: Job config has non-empty `OutputChannels` list
4. **Check job output**: Verify the job produced output (not empty/null)
5. **Check logs**: Look for warnings like `"channel '{ChannelName}' was not found"`

### Job Failing

1. **Check error logs**: Look for `"Cron job '{JobName}' failed"` with exception details
2. **Check execution history**: `ICronService.GetHistory(jobName)` shows recent failures
3. **Check correlation ID**: Use correlation ID to trace through activity stream
4. **For agent jobs**: Check if agent is configured and available
5. **For system jobs**: Verify system action is registered (e.g., `health-audit`)

---

## See Also

- [Configuration Guide](./configuration.md) — Full configuration reference
- [Architecture Overview](./architecture/overview.md) — System architecture and component interactions
- [Extension Development](./extension-development.md) — Creating custom system actions and channels
- [Workspace and Memory](./development/workspace-and-memory.md) — Agent memory consolidation details

