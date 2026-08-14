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
6. [Output Routing](#output-routing)
7. [Built-in Actions](#built-in-actions)
8. [Action Reference](#action-reference)
9. [CronTool — Runtime Job Management](#crontool--runtime-job-management)
10. [REST API Endpoints](#rest-api-endpoints)
11. [Migration from HeartbeatService](#migration-from-heartbeatservice)
11a. [Failure Alerts](#11a-failure-alerts-2557)
12. [Observability](#observability)
13. [Examples](#examples)

---

## 1. Overview

BotNexus provides a **centralized cron service** (`CronScheduler`) that schedules and executes jobs on a fixed tick interval. Unlike per-agent scheduling (legacy `AgentConfig.CronJobs`), all jobs are registered in a single, global configuration section: the `cron` section of `config.json`.

### Key Characteristics

- **Centralized**: All config-defined jobs in one place (`cron.jobs`), merged at runtime with jobs created through the `cron` tool and the REST API
- **One field selects the behaviour**: a job's `actionType` decides what happens when it fires (see [Job Types](#job-types))
- **Tickless evaluation**: Service wakes every N seconds (default 60) and checks which jobs are due
- **Non-blocking execution**: Jobs run concurrently; the scheduler does not wait for completion
- **Async-first**: All job execution is fully asynchronous
- **Correlated logging**: Every job execution gets a unique correlation ID for tracing
- **Activity events**: Cron service publishes start, complete, and failure events to the activity stream

### Architecture

```text
┌──────────────────────┐
│   CronScheduler      │
│  (Background Svc)    │
└──────┬───────────────┘
       │ (every tickIntervalSeconds)
       ▼
   Check due jobs
   │
   └─→ Dispatch to the ICronAction whose ActionType matches the job's actionType
```

Each action is a registered `ICronAction` implementation (see
`CronServiceCollectionExtensions.AddBotNexusCron`). Adding a new action type means registering a
new `ICronAction`; there is no separate system/maintenance registry.

---

## 2. Job Types

A job's behaviour is selected by a single `actionType` field. There is no `type` field and no
separate agent/system/maintenance job taxonomy - every job is dispatched to the registered
`ICronAction` whose `ActionType` matches.

| `actionType` | What it does when it fires | Required fields | Cost |
|---|---|---|---|
| `agent-prompt` (default) | Sends a prompt to the target agent through the agent runner | `agentId` + (`message` **or** `templateName`) | One full model turn per fire |
| `command` | Runs `shellCommand` as a script | `shellCommand` | No tokens unless the script escalates |
| `webhook` | POSTs to `webhookUrl` | `webhookUrl` | None |
| `memory-dreaming` | LLM memory consolidation pass (see [8.4](#_8-4-memory-dreaming)) | `agentId` | One model turn per fire |
| `skill-review` | LLM skill-review pass (see [8.7](#_8-7-skill-review)) | `agentId` | One model turn per fire |
| `agent-converse` | Scheduled agent-to-agent conversation (see [8.6](#_8-6-agent-converse)) | `agentId` + metadata | One or more model turns |
| `heartbeat` | System-provisioned agent heartbeat | `agentId` | One model turn per fire |

`heartbeat` and `skill-review` jobs are **auto-provisioned** by the gateway per user-defined agent
and are marked `system` - they are hidden from `cron` tool `list` output unless `includeSystem` is
set.

### 2.1 Agent prompt jobs (`actionType: "agent-prompt"`)

Execute a prompt through the agent runner pipeline. This is the default when `actionType` is
omitted.

**Example:**
```json
{
  "cron": {
    "jobs": {
      "morning-briefing": {
        "name": "Morning briefing",
        "schedule": "0 9 * * *",
        "actionType": "agent-prompt",
        "agentId": "analyst",
        "message": "Generate a morning briefing on recent alerts.",
        "timeZone": "America/New_York",
        "enabled": true
      }
    }
  }
}
```

Instead of a literal `message`, a job may name a prompt template declared under
`cron.promptTemplates` and supply `templateParameters`.

### 2.2 Command jobs (`actionType: "command"`)

Run a shell script with no model turn.

```json
{
  "cron": {
    "jobs": {
      "disk-space-check": {
        "name": "Disk space check",
        "schedule": "0 * * * *",
        "actionType": "command",
        "shellCommand": "pwsh -NoProfile -File ./scripts/check-disk.ps1"
      }
    }
  }
}
```

`shellCommand` is an arbitrary-execution surface. Firing is gated through the `exec` tool policy,
and authoring a command job through the `cron` tool is authorized at create/update time as well as
at fire time - see [Shell Execution](./features/shell-execution.md).

### 2.3 Webhook jobs (`actionType: "webhook"`)

POST to an external URL on a schedule.

```json
{
  "cron": {
    "jobs": {
      "ping-monitor": {
        "name": "Ping monitor",
        "schedule": "*/15 * * * *",
        "actionType": "webhook",
        "webhookUrl": "https://example.com/hooks/botnexus"
      }
    }
  }
}
```

`webhookUrl` is validated at a single shared boundary (`CronWebhookUrl`) on both the config-declared
and API paths. It must be an **absolute `http` or `https` URL carrying no embedded credentials** -
`file:`, `ftp:` and every other scheme are rejected, as is `https://user:pass@host/hook`. A rejected
URL fails the request with
`WebhookUrl must be an absolute http or https URL and must not contain embedded credentials.`
and leaves no row in the store.

---

## 3. Configuration

### 3.1 Top-Level Cron Config

**Section:** `cron` (in `config.json`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `enabled` | bool | `true` | Enable/disable the cron service globally |
| `tickIntervalSeconds` | int | `60` | How often the scheduler wakes to evaluate due jobs (seconds). This is the single normative default; it is set in `src/gateway/BotNexus.Cron/CronOptions.cs` and mirrored by `CronConfig` in `src/gateway/BotNexus.Gateway.Configuration/PlatformConfig.cs`. Values `<= 0` are rejected by config validation, and the scheduler additionally floors the effective delay at 1 second. |
| `defaultJobTimeoutSeconds` | int | `3600` | Timeout applied to a run when the job declares none. A job can override it - or opt out of it entirely - via `metadata.timeoutSeconds`, see [Per-job timeout](#_3-2-1-per-job-timeout-timeoutseconds). |
| `orphanedRunThresholdSeconds` | int | `86400` | How far a run's `started_at` may deviate from now (in **either** direction) before the scheduler treats a still-`running` row as orphaned and stamps it as an error (#2410). The bound is symmetric, so a clock skew forward widens the reap window rather than nulling live runs. |
| `maxConcurrentJobs` | int | `5` | Aggregate cap on how many due jobs the scheduler executes concurrently on a single tick (#2670). Jobs beyond the cap **queue and run as slots free** - none are dropped. A value of `0` or less degrades to the default of `5` rather than to unbounded fan-out. Independent of the per-job lock, which separately prevents two runs of the *same* job from overlapping. |
| `activeRunCancellationGraceSeconds` | int | `30` | How long a delete or disable waits for a cancelled in-flight run to actually observe its cancellation before archiving the conversation and sweeping the run's sessions (#3160). A grace period, not a guarantee: it **fails open**, so an action that swallows its cancellation token can never make its job permanently undeletable. `0` or less skips the wait entirely. |
| `jobs` | dict | `{}` | Config-defined job registry (key → job descriptor, see §3.2) |

> Only `enabled`, `tickIntervalSeconds` and `jobs` are copied out of `config.json`'s `cron`
> section into the scheduler options; `defaultJobTimeoutSeconds`, `orphanedRunThresholdSeconds`
> and `maxConcurrentJobs` are scheduler options bound in code and are documented here because
> they govern observable scheduler behaviour.

### 3.2 Per-Job Configuration

**Type:** `CronJobConfig` (each entry under `cron.jobs`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `name` | string | (inferred) | Display name for the job |
| `schedule` | string | (required) | Cron expression (5- or 6-field) |
| `actionType` | string | `agent-prompt` | What the job does when it fires — e.g. `agent-prompt`, `webhook`, `command` |
| `agentId` | string | (agent jobs only) | Agent to run the prompt against |
| `message` | string | (agent jobs only) | Prompt message sent to the agent |
| `templateName` | string | `null` | Named prompt template to use instead of a literal `message` |
| `templateParameters` | dict | `{}` | Parameter values for `templateName` |
| `model` | string | `null` | Model override for agent-prompt jobs |
| `webhookUrl` | string | (webhook jobs only) | URL invoked by a webhook job. Must be an absolute `http`/`https` URL with no embedded credentials - see [2.3](#_2-3-webhook-jobs-actiontype-webhook). |
| `shellCommand` | string | (command jobs only) | Script run by a `command` job. Firing is gated through the `exec` tool policy — see [Shell Execution](./features/shell-execution.md). |
| `enabled` | bool | `true` | Whether the job is active |
| `system` | bool | `false` | Marks a job as system-provisioned (e.g. `heartbeat`, `skill-review`). System jobs are hidden from `cron` tool `list` output unless `includeSystem` is set. |
| `timeZone` | string | `null` | IANA timezone the schedule is evaluated in (UTC when omitted) |
| `createdBy` | string | `null` | Provenance marker for who created the job |
| `metadata` | dict | `{}` | Free-form metadata carried with the job. The key `timeoutSeconds` overrides `defaultJobTimeoutSeconds` for this job - see [Per-job timeout](#_3-2-1-per-job-timeout-timeoutseconds). |
| `deleteAfterRun` | bool | `false` | Opt-in cleanup for ephemeral jobs: when `true`, the scheduler deletes the run's agent session and its transcript after the run completes (across success / timeout / error / abort), provided the run produced a cron-scoped (`cron:`) session. Prevents run-scoped sessions from accumulating transcript entries indefinitely. Leave off for long-lived reporting jobs that intentionally persist context across runs — use compaction for those. Only ever deletes `cron:`-prefixed sessions, so a misconfigured flag cannot remove an unrelated long-lived session. |
| `deleteJobAfterRun` | bool | `false` | Opt-in **job-level** one-shot disposition: when `true`, the scheduler deletes the **job itself** after its first terminal run - success, timeout, error, or host abort alike - from the same post-run teardown that already owns the run. Deliberately **not** `deleteAfterRun`, which removes the run's ephemeral *session* and leaves the job scheduled forever; the two compose. Use this instead of writing "delete this cron job after running" into the prompt: a prompt instruction has no enforcement and no retry if the turn ends early, this flag does. Off by default, and rows written before the column existed read `false`, so nothing is ever removed without an explicit opt-in. |
| `executionClass` | bool | `false` | Marks the job as **execution-class**: its contract is to *perform work*, so a run that completes having made **zero tool calls** records `no_tool_calls` instead of `ok` (see [Zero-tool-call runs](#11b-zero-tool-call-runs-2985)). Off by default, and rows written before the column existed read `false`, so an unmarked job is completely unaffected and may legitimately finish with no tool call. Only meaningful for `agent-prompt` jobs -- `command` and `webhook` actions report no tool count at all and can never reach the outcome. |
| `expiresAt` | string (ISO-8601) | `null` | Optional hard expiry instant. Once `now >= expiresAt` the job **stops executing**: the scheduler suppresses the fire and never invokes the action. Expiry **suppresses only** - it does not delete or disable the row, so the job stays visible with its history intact for a human to inspect and extend. Checked at both schedule time (cheap early-out) and fire time (the authoritative gate, so a past-due job or a manual run cannot leak through). `null` means no expiry - exactly today's behaviour. Pair with `deleteJobAfterRun` if removal is actually wanted. |

### 3.2.1 Per-Job Timeout (`metadata.timeoutSeconds`)

A job overrides the scheduler's `defaultJobTimeoutSeconds` by carrying a `timeoutSeconds` value in
its `metadata`. `command` jobs use the same key, with their own lower default of **120 seconds**.

| Value | Meaning |
|-------|---------|
| absent / `null` | Use the default (`defaultJobTimeoutSeconds`, or `120` for `command` jobs). |
| positive integer | Cancel the run after that many seconds. |
| **`0`** | **Unlimited** - no timeout is armed at all (#2904). |
| negative / unparseable | Invalid. A **warning** is logged naming the job id and the offending value, and the default is used. |

`0` is an **explicit sentinel**, deliberately distinguished from "unset": it is how an operator says
"this job legitimately runs long". Before this existed the only way to express it was an arbitrary
large magic number, and a `0` was silently discarded.

**Unlimited removes the per-job cap, not all cancellation.** An unlimited run is still bounded by
the ambient cancellation token, so gateway shutdown, scheduler stop, and an explicit cancel all
still end it promptly. It will not, however, be stopped by anything else - use it only for jobs
whose runtime is genuinely unbounded, and prefer a generous positive value where you can name one.

The value is accepted in every shape job metadata can carry it in - JSON number, JSON string, `int`,
`long`, `double`, `string` - so the sentinel behaves identically whether the job came from
`config.json`, the `cron` tool, or the API.

```json
"long-running-watch": {
  "name": "Long running watch",
  "schedule": "0 7 * * *",
  "actionType": "command",
  "shellCommand": "pwsh -NoProfile -File ./scripts/watch.ps1",
  "metadata": { "timeoutSeconds": 0 }
}
```

### 3.3 Complete Configuration Example

```json
{
  "cron": {
    "enabled": true,
    "tickIntervalSeconds": 60,
    "jobs": {
      "morning-briefing": {
        "name": "Morning briefing",
        "schedule": "0 9 * * *",
        "actionType": "agent-prompt",
        "agentId": "analyst",
        "message": "Generate a morning briefing.",
        "timeZone": "America/New_York",
        "enabled": true
      },
      "disk-space-check": {
        "name": "Disk space check",
        "schedule": "0 * * * *",
        "actionType": "command",
        "shellCommand": "pwsh -NoProfile -File ./scripts/check-disk.ps1",
        "enabled": true
      },
      "nightly-dreaming": {
        "name": "Nightly memory consolidation",
        "schedule": "0 2 * * *",
        "actionType": "memory-dreaming",
        "agentId": "analyst",
        "metadata": {
          "lookbackDays": "14"
        },
        "enabled": true
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

When `timeZone` is set on a job, the cron expression is evaluated in that timezone. For example:

```json
{
  "schedule": "0 9 * * *",
  "timeZone": "America/New_York"
}
```

This job runs at 9:00 AM **Eastern Time**, not UTC. Omitting `timeZone` (or setting it to `UTC`) evaluates
the expression in UTC.

**Id resolution accepts either family.** A timezone id is resolved through a single canonical resolver
(`CronTimeZoneResolver`, issue #2748), which tries the id as given and then converts between the Windows
and IANA spellings and retries. `America/New_York` and `Eastern Standard Time` therefore both resolve on
either a Linux or a Windows host, and the scheduler's next-run computation, the `cron` tool's validation
and the missed-run detector all agree on the result - before #2748 three independent implementations
disagreed, so a job could fire at the wrong hour while the action that ran it reported the right one.

**An unresolvable id degrades to UTC rather than throwing**, because resolution runs inside the scheduler
loop and a throw would stop all scheduling. This is fail-safe, not silent-correct: check the job's reported
next-run time after setting an unusual id, since a typo lands you in UTC instead of erroring.

---

## 5. Agent Session Modes

Agent-prompt jobs run in a session derived from the job, not from a configurable `session` field
(there is no such field on a cron job). The scheduler resolves the job's session and conversation
deterministically:

- Sessions created for a job are **`cron:`-prefixed** and carry the job id slug
  (`cron:{jobIdSlug}:...`), which is what lets the scheduler re-bind a job's prior sessions onto its
  canonical conversation and what scopes cleanup.
- A job's canonical conversation is titled `cron:{jobId}`; every `cron:*` session for that agent is
  rebound onto it, so a job's history accumulates in one place across runs.
- Setting `deleteAfterRun` to `true` deletes the run's session and transcript after the run
  completes (success, timeout, error or abort), but **only** when the session id begins with
  `cron:` - so a misconfigured flag cannot remove an unrelated long-lived session.

Leave `deleteAfterRun` off for long-lived reporting jobs that intentionally accumulate context
across runs; use compaction for those instead.

---

## 6. Output Routing

There is no `outputChannels` field on a cron job. A job's output goes wherever its action sends it:

- **`agent-prompt`** - the agent's response is delivered through the agent's own channel bindings
  and appears in the job's canonical `cron:{jobId}` conversation.
- **`webhook`** - the payload is POSTed to `webhookUrl`.
- **`command`** - stdout/stderr are captured into the run record; nothing is sent to a channel.

Run output and errors are always recorded on the run record regardless of action type, and are
readable via `GET /api/cron/{jobId}/runs` and the `cron` tool's `history` action.

---

## 7. Built-in Actions

Every action is a registered `ICronAction`. The complete set registered by
`AddBotNexusCron` is:

| `actionType` | Implementation | Notes |
|---|---|---|
| `agent-prompt` | `AgentPromptAction` | Default action; sends a prompt to the target agent |
| `command` | `CommandCronAction` | Shell script; authorized through the `exec` tool policy |
| `webhook` | `WebhookAction` | POSTs to the validated `webhookUrl` |
| `heartbeat` | `HeartbeatAction` | Auto-provisioned per agent (system job) |
| `memory-dreaming` | `MemoryDreamingCronAction` | See [8.4](#_8-4-memory-dreaming) |
| `skill-review` | `SkillReviewCronAction` | Auto-provisioned per agent (system job); see [8.7](#_8-7-skill-review) |
| `agent-converse` | `AgentConverseCronAction` | See [8.6](#_8-6-agent-converse) |

Two background services run alongside the scheduler and are **not** cron jobs - they need no job
definition and cannot be scheduled:

- **`CronRunRetentionHostedService`** - purges completed/failed/timed-out run records older than
  `RetentionDays` (default 30), checking every `CheckInterval` (default 1 hour). Bound from
  `CronRunRetentionOptions`. Prevents unbounded growth of `cron.sqlite`.
- **`MissedRunDetectionService`** - see [8.8](#_8-8-missed-run-detection).

---

## 8. Action Reference

### 8.1 `agent-prompt`

**Name:** `agent-prompt`
**Description:** The default action. Sends a prompt to the target agent through the agent runner.

**Job fields:**
- `agentId`: Target agent (required)
- `message`: Prompt text, **or** `templateName` + `templateParameters`
- `model`: Optional model override, validated against the model registry at create/update time

### 8.2 `command`

**Name:** `command`
**Description:** Runs `shellCommand` as a script, costing no model turn.

**Job fields:**
- `shellCommand`: Script to run (required)

Authorization runs at **both** authoring time (create/update through the `cron` tool) and firing
time, both routed through the same `exec` tool policy via `ICommandCronAuthorizer`, so a job that
would be refused at fire time cannot be stored silently.

### 8.3 `webhook`

**Name:** `webhook`
**Description:** POSTs to `webhookUrl` on the schedule.

**Job fields:**
- `webhookUrl`: Absolute `http`/`https` URL with no embedded credentials (required) - see
  [2.3](#_2-3-webhook-jobs-actiontype-webhook)

### 8.4 `memory-dreaming`

**Name:** `memory-dreaming`  
**Description:** Periodic memory consolidation via LLM — reads recent daily notes, builds a consolidation prompt, and dispatches a session to update `MEMORY.md` with distilled insights.

Unlike a mechanical file merge, memory dreaming uses an LLM to extract patterns, decisions, and knowledge from daily notes and weave them into the agent's long-term memory.

**Job fields:**
- `actionType`: `"memory-dreaming"`
- `agentId`: Agent whose memory is consolidated (required)
- `lookbackDays` (in job metadata): Number of days of daily notes to read (default: 14)
- `maxContentChars` (in job metadata): Maximum characters of source material (default: 50000)

**Configuration:**
```json
{
  "actionType": "memory-dreaming",
  "schedule": "0 3 * * 0",
  "agentId": "my-agent",
  "metadata": {
    "lookbackDays": "14",
    "maxContentChars": "50000"
  },
  "enabled": true
}
```

**Behavior:**
- Reads `memory/YYYY-MM-DD.md` files from the last N days in the agent's workspace
- Builds a consolidation prompt with the collected daily notes as context
- Dispatches a cron-triggered session that writes distilled insights back to `MEMORY.md`
- Skips execution if no daily notes exist in the lookback window

**Use case:** Keep an agent's long-term memory fresh and relevant without manual curation. Schedule weekly during off-hours.

### 8.5 Cron run retention (background service, not a job)

**Type:** `CronRunRetentionHostedService` / `CronRunRetentionOptions`
**Description:** Purges old completed cron run history records to prevent unbounded database growth.

Periodically sweeps the cron run store and deletes records with status `Completed`, `Failed`, or `Timeout` that are older than the configured retention period.

This is a **hosted background service**, not a cron action - it runs automatically and has no job
definition, no `actionType` and no schedule. It is configured from `CronRunRetentionOptions`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RetentionDays` | int | `30` | Number of days to retain completed/failed run records |
| `CheckInterval` | TimeSpan | `01:00:00` | How often the service checks for expired runs |

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
  "actionType": "agent-converse",
  "schedule": "0 9 * * 1-5",
  "agentId": "coordinator",
  "metadata": {
    "targetAgentId": "reporter",
    "message": "Generate the morning status report.",
    "objective": "Get daily status",
    "maxTurns": "5"
  }
}
```

**Behavior:**
- Subject to exchange budget enforcement — skipped if pair is in cooldown or at daily cap
- The initiating agent is the one specified in the job's `agentId` field
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
  "actionType": "skill-review",
  "schedule": "0 4 * * *",
  "agentId": "my-agent",
  "system": true,
  "enabled": true,
  "metadata": {
    "enabled": "true",
    "minToolCalls": "5",
    "lookbackHours": "24",
    "maxSessions": "50"
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

Agents can create, update, delete, run, list, and inspect cron jobs at runtime using the
**`cron` tool**.

### 9.1 Tool Definition

**Name:** `cron`  
**Description:** Schedule or manage cron jobs. Actions: list, create, update, delete, run, history.

A job created through the tool is one of two action types:

| `actionType` | What it does on each fire | Required fields | Cost |
|---|---|---|---|
| `agent-prompt` (default) | Sends a prompt to the target agent | `message` **or** `templateName` | One full model turn per fire |
| `command` | Runs `shellCommand` as a script | `shellCommand` | No tokens unless the script escalates |

`actionType` is optional and defaults to `agent-prompt`, so callers that omit it behave
exactly as before. Validation is per action type: an `agent-prompt` job still requires a
prompt source, and a `command` job still requires a non-empty `shellCommand` - neither is
allowed to be created with nothing to do.

`shellCommand` is an arbitrary-execution surface, so it carries the same authorization
posture as the `exec` path. See [Command job authorization](#command-job-authorization).

#### Command Job Authorization

Command jobs are gated by `ICommandCronAuthorizer` (issue #2462). The default implementation,
`ToolPolicyCommandCronAuthorizer`, does **not** define its own policy language: it delegates to
the existing tool-boundary policy surface - `IToolPolicyProvider`, `ToolRiskLevel` and
`ToolApprovalFallback` - classifying the command under the **`exec`** tool name. There is one
policy vocabulary, not two, so setting `askFallback: deny` for an agent closes the interactive
`exec` path and the unattended cron command path with a single switch. **No new configuration
keys are introduced by command-job authorization.**

Both lifecycle phases are gated, deliberately and distinctly:

| Phase | Where | What happens on denial |
|---|---|---|
| **Authoring** - `create`/`update` carrying a `shellCommand` via the `cron` tool | `CronTool` | `UnauthorizedAccessException` with the reason. **Nothing is written to the store**, so a denied command never becomes a job. |
| **Firing** - every scheduled or manual execution | `CommandCronAction`, immediately before `Process.Start()` | The reason is logged at `Error`, the run is recorded with status `error` carrying the reason, and **no subprocess is started**. |

Firing is re-evaluated independently of authoring rather than trusting the stored job, because
policy can be tightened after a job is created, and jobs can arrive through paths other than the
`cron` tool (for example `POST /api/cron` or an imported store).

The decision order, identical in both phases:

1. Extract the leading executable token from the command. If none can be extracted - the command
   opens with a shell operator, substitution, or is empty - it is **unclassifiable** and is
   **denied**. This is fail-closed by design, matching the posture of `ChannelFailureClassifier`
   (#2447): the gate never guesses.
2. Resolve `IToolPolicyProvider`. If it is unavailable the policy cannot be evaluated, so the
   command is **denied**. An unregistered dependency therefore fails closed rather than silently
   disabling the gate.
3. If the `exec` tool does not require approval, **allow**.
4. If it does require approval, consult the agent's approval fallback. A cron firing is
   unattended, so no approval workflow can ever service the request: `deny` refuses,
   `allow` (the platform default, per #2391) permits with an audit log entry.

Because `ToolApprovalFallback.Allow` remains the default, out of the box a command job still runs
and simply records an audit line; blocking is opt-in via `askFallback: deny`. The unclassifiable
and missing-provider paths deny unconditionally regardless of that setting.

`agent-prompt` jobs are entirely unaffected: the gate is only consulted for `command` jobs.

The action set is defined by the tool's input schema in
`src/gateway/BotNexus.Cron/Tools/CronTool.cs` and is exactly:
`list`, `create`, `update`, `delete`, `run`, `history`. There is no `schedule` action and no
`remove` action — use `create` and `delete`. Jobs are addressed by `jobId` (the server-generated
identifier returned by `create` and listed by `list`), not by name.

### 9.2 Actions

#### `create`

Creates a new job.

**Arguments:**
- `action` = `"create"`
- `name`: Job name (required)
- `schedule`: Standard 5-field cron expression (required)
- `actionType`: `"agent-prompt"` (default) or `"command"` (optional)
- `message`: Prompt text - `agent-prompt` jobs only
- `templateName` / `templateParameters`: Named prompt template - `agent-prompt` jobs only
- `shellCommand`: Script to run - required for `command` jobs
- `timeZone`: IANA timezone the schedule is evaluated in (optional; defaults to UTC)
- `agentId`: Target agent (optional; defaults to the calling agent)
- `model`: Model override - `agent-prompt` jobs only (optional)
- `enabled`: Whether the job is enabled (optional; default `true`)
- `deleteAfterRun`: Delete the run's ephemeral cron-scoped **session** and transcript after each run (optional; default `false`)
- `deleteJobAfterRun`: One-shot lifecycle - the scheduler deletes the **job itself** after its first terminal run (optional; default `false`)
- `expiresAt`: ISO-8601 instant (e.g. `"2026-12-31T00:00:00Z"`) after which the job stops firing (optional; omit for no expiry)
- `failureAlertsEnabled`: Master opt-in for this job's failure alerts (optional; default `false`)
- `failureAlertConversationId`: Conversation the alert is delivered to (optional). Must resolve to an existing conversation - an unresolvable target is refused at the tool seam by the same shared validator the REST API uses, because an alert that could never deliver is worse than none.

::: warning Alerting needs BOTH fields
`failureAlertsEnabled: true` with no `failureAlertConversationId` delivers nothing - there is
deliberately no implicit fallback conversation. Set both, or the job stays silently unalertable.
:::

::: tip Prefer `deleteJobAfterRun` over a self-delete prompt
Writing "delete this cron job after running" into a job's prompt is a request with no
enforcement: if the turn ends early the job survives and keeps firing forever. Set
`deleteJobAfterRun: true` instead - the scheduler performs the deletion itself from the
post-run teardown, so it happens on every terminal outcome including timeout and abort.
:::

**Example - a zero-token command job:**
```json
{
  "action": "create",
  "name": "disk-space-check",
  "schedule": "0 * * * *",
  "actionType": "command",
  "shellCommand": "pwsh -NoProfile -File ./scripts/check-disk.ps1"
}
```

Returns the created job serialized as JSON, including its generated `id`.

#### `update`

Updates an existing job. Every field except `jobId` is optional; an omitted field keeps its
current value.

**Arguments:**
- `action` = `"update"`
- `jobId`: Job identifier (required)
- Any of the `create` fields above

Passing an **empty string** for `expiresAt` clears an existing expiry; omitting the field
leaves the current expiry untouched. The same rule applies to `failureAlertConversationId`:
an empty string clears the alert target, an omitted field leaves it (and `failureAlertsEnabled`)
exactly as stored, so an unrelated edit can never silently un-alert a job.

Updating prompt-irrelevant fields (`schedule`, `timeZone`, `name`, `enabled`) on a
`command` job does **not** require a `message` or `templateName`, and preserves the
existing `shellCommand`.

Supplying a different `actionType` switches the job and clears the fields belonging to the
other action type, so a job is never left as a `command` job holding a stale prompt (or an
`agent-prompt` job holding an orphaned `shellCommand`). A switch must satisfy the new type's
validation in the same call - e.g. switching to `agent-prompt` requires a `message` or
`templateName`.

**Example:**
```json
{
  "action": "update",
  "jobId": "3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c",
  "schedule": "0 9 * * MON-FRI",
  "enabled": false
}
```

Returns the saved job serialized as JSON.

#### `delete`

Deletes a cron job.

**Arguments:**
- `action` = `"delete"`
- `jobId`: Job identifier (required)

**Example:**
```json
{
  "action": "delete",
  "jobId": "3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c"
}
```

**Response:**
```text
Deleted cron job '3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c'.
```

#### `run`

Triggers an immediate, out-of-schedule execution of a job.

**Arguments:**
- `action` = `"run"`
- `jobId`: Job identifier (required)

Returns the resulting run record serialized as JSON.

#### `history`

Retrieves execution history for one job, or recent runs across every job the caller may manage.

**Arguments:**
- `action` = `"history"`
- `jobId`: Job identifier (**optional**). Omit it to query across jobs.
- `limit`: Maximum entries to return (1–100, default: 20)
- `failedOnly`: Return only runs that did not succeed - `error`, `timed_out`, `no_tool_calls`, `delivery_failed`, and `missed` (optional; default `false`)

**Example - one job:**
```json
{
  "action": "history",
  "jobId": "3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c",
  "limit": 10
}
```

**Example - "which of my jobs have failed recently?":**
```json
{
  "action": "history",
  "failedOnly": true,
  "limit": 50
}
```

Without a `jobId` the query is scoped to the jobs the caller may manage, using the same
authorisation rule as the per-job path; an agent with no manageable jobs gets an empty result
rather than every job's history.

Returns the matching run records serialized as JSON, newest first.

#### `list`

Lists the cron jobs visible to the calling agent, serialized as JSON.

**Arguments:**
- `action` = `"list"`

**Example:**
```json
{
  "action": "list"
}
```

Returns the visible job definitions serialized as JSON.

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
  "jobId": "3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c",
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

- **Select** a job from a rich drop-down showing its name, schedule, action type,
  and owning agent.
- **View and edit on one page** — selecting a job loads its full properties into an
  inline editor. Operators can change the name, schedule, time zone, action payload,
  and enabled flag (`PUT /api/cron/{jobId}`). For `agent-prompt` jobs the model is
  chosen via **provider and model drop-downs** (populated from `/api/providers` and
  `/api/models`) rather than free text; command and webhook jobs edit their shell
  command or webhook URL.
- **Review execution history below the editor** — the same page lists each run from
  `GET /api/cron/{jobId}/runs`, including start time, duration, status, linked session,
  and errors. A related-conversation shortcut is shown when the job owns a pinned
  conversation.
- **Run now** — trigger an immediate out-of-schedule execution (`POST /api/cron/{jobId}/run`)
  and refresh the selected job's execution history in place.
- **Delete** — remove the selected job after a confirmation prompt
  (`DELETE /api/cron/{jobId}`); the job's pinned conversation is archived alongside
  the record.

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
  "cron": {
    "jobs": {
      "analyst-morning-briefing": {
        "name": "Morning briefing",
        "schedule": "0 9 * * *",
        "actionType": "agent-prompt",
        "agentId": "analyst",
        "message": "Generate a morning briefing.",
        "enabled": true
      }
    }
  }
}
```

### Migration Path

1. **Move all `AgentConfig.CronJobs` to `cron.jobs`**:
   - Flatten the per-agent structure into a centralized dictionary
   - Each key should be a unique job identifier (e.g., `{agent}-{job-type}`)

2. **Map the legacy properties onto the current ones**:
   - `Type: "agent"` -> `actionType: "agent-prompt"`
   - `Agent` -> `agentId`, `Prompt` -> `message`, `Timezone` -> `timeZone`
   - `Session` and `OutputChannels` have **no equivalent** - sessions are derived from the job
     (see [Agent Session Modes](#agent-session-modes)) and output routing follows the action
     (see [Output Routing](#output-routing))

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

## 11a. Failure Alerts (#2557)

A cron job that starts failing every night at 02:00 is otherwise invisible until somebody reads
the run history or the log. **Failure alerts** deliver a message to a configured conversation when
a run terminates as `error`.

### Opt-in setting

Failure alerts are **opt-in per job and off by default**. Existing jobs -- including rows written
before this feature existed -- read as disabled and behave exactly as they did before.

Two job fields control it:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `failureAlertsEnabled` | bool | `false` | Master opt-in for this job. |
| `failureAlertConversationId` | string \| null | `null` | Conversation the alert is delivered to. |

Both must be set: enabling alerts without a conversation id delivers nothing and logs a warning.
There is deliberately **no** implicit fallback to the job's own `conversationId`, so turning alerts
on can never accidentally retarget a job's long-lived run conversation.

Both fields are writable from **every** authoring surface: the config file, `POST`/`PUT /api/cron`,
and the agent-facing `cron` tool's `create` and `update` actions (#2838). Before #2838 the tool
declared neither parameter, so no agent-created job could ever be made alertable - silent cron
death was the default failure mode for the only surface agents have.

Configuration example:

```json
{
  "cron": {
    "jobs": {
      "nightly-report": {
        "name": "Nightly Report",
        "schedule": "0 2 * * *",
        "actionType": "agent-prompt",
        "agentId": "reporter",
        "message": "Produce the nightly report.",
        "failureAlertsEnabled": true,
        "failureAlertConversationId": "c_ops_alerts"
      }
    }
  }
}
```

### Backoff

Alerting on *every* failed run would turn a job failing each minute into the noise the alert was
meant to detect. Instead, alerts fire on the **first** failure of an error streak and thereafter
only at streak positions that are exact powers of two:

```
streak position: 1  2  3  4  5  6  7  8  9 ...
alert delivered: Y  Y  .  Y  .  .  .  Y  . ...
```

The streak is derived from the job's run history (consecutive `error` rows, newest first); a
non-error terminal outcome resets it. Concurrent in-flight (`running`) rows are skipped rather
than treated as a reset, so a parallel run cannot silently restart the backoff.

### Alert payload

| Field | Notes |
| --- | --- |
| `JobId` | Identifier of the failing job. |
| `JobName` | Human-readable job name. |
| `ScheduledRunTime` | **The occurrence the run was triggered for.** Without it the recipient cannot tell *which* occurrence broke -- this is the point of the alert. |
| `AttemptedAt` | Wall-clock instant the failure was observed. |
| `ConsecutiveErrorCount` | Length of the current error streak (1 on the first failure). |
| `Error` | Error text, passed through `CronExternalDeliveryRedactor.RedactSummary` before it leaves the box. |

Rendered message shape:

```
Cron job failed: Nightly Report (nightly-report)
Scheduled run time: 2026-07-31T02:00:00.0000000+00:00
Attempted at: 2026-07-31T02:00:04.1173920+00:00
Consecutive errors: 1
Error: <redacted error text>
```

### Delivery and failure semantics

Delivery reuses the existing conversation-message seam (`IConversationRouter` ->
`IInboundMessageOrchestrator`), the same path the `conversation` tool's `message` action takes.
Webhook delivery and per-channel / per-account routing are **out of scope**; so are recovery
("healthy again") notifications.

An alert-delivery failure **never fails the cron run**. The run's terminal state is persisted
before the alert is attempted, and every exception out of the delivery sink is caught and logged
at `Error` level.

---

## 11b. Zero-Tool-Call Runs (#2985)

A cron run that throws is visible and retried. A cron run that **completes having done nothing** was
not: before #2985 a job of action type `agent-prompt` whose turn produced one text reply and made no
tool calls recorded `status: ok`, `error: null` -- byte-identical to a healthy run.

That is not hypothetical. On 2026-08-11 the autonomous-maintenance job had **four consecutive runs**
of 9-11 seconds (healthy runs of the same job take 200-550s) that made zero tool calls, each recorded
as a success, each emitting a detailed report of PR rebases and dispatches that never happened. The
discrepancy was found days later only by hand-counting `session_history` rows. Because run status is
the input to the [failure-alert path](#11a-failure-alerts-2557), alerting could not fire on it either.

### The `executionClass` marker

The rule cannot be applied to every `agent-prompt` job: a genuine **reporting or classification** job
may legitimately answer from context without calling a tool, and demoting those would make the signal
worthless. So the operator declares the job's class and the scheduler enforces it:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `executionClass` | bool | `false` | When `true`, a run that completes with zero tool invocations is recorded as `no_tool_calls` rather than `ok`. |

**Off by default.** A job without the marker behaves exactly as it did before, and rows written
before the column existed read `false` -- an upgrade never retroactively reclassifies an existing
job's runs as failing.

```json
{
  "cron": {
    "jobs": {
      "autonomous-maintenance": {
        "name": "Autonomous Issue & PR Maintenance",
        "schedule": "0 * * * *",
        "actionType": "agent-prompt",
        "agentId": "farnsworth",
        "message": "Perform the hourly maintenance pass.",
        "executionClass": true,
        "failureAlertsEnabled": true,
        "failureAlertConversationId": "c_ops_alerts"
      }
    }
  }
}
```

The same flag is available on `POST /api/cron` and on the `cron` tool's `create` / `update` actions.
On update, omitting `executionClass` leaves the stored classification alone, so an unrelated edit
(rename, reschedule, enable/disable) can never silently un-mark an execution-class job.

### The `no_tool_calls` outcome

`no_tool_calls` is a **terminal, non-success** run status alongside `ok`, `error`, and `timed_out`:

| Property | Behaviour |
| --- | --- |
| Run history | Written as a normal terminal row, with an `error` reason naming the zero-tool-call condition. Not `null`. |
| `lastRunStatus` | Set on the job, so the portal's run-status badge distinguishes it without anyone reading `session_history`. |
| Failure alerts | Participates in the existing `failureAlertConversationId` path with the same power-of-two backoff. There is deliberately **no** second notification channel. |
| Streak counting | Counts toward the alert streak alongside `error`, so a job stuck in the do-nothing state does not alert on every single run. |
| Retention | Purged by `PurgeRunsOlderThanAsync` like any other terminal status -- it is not immune to cleanup. |

### When the rule does *not* fire

Two conditions must both hold, and each guards a distinct way of getting this wrong:

1. **The job is marked `executionClass`.** An unmarked job is untouched (a reporting job may
   legitimately make no tool call).
2. **The action actually reported a tool count.** `command` and `webhook` actions have no tool
   concept and report nothing; an interrupted turn already carries its own terminal outcome. That
   silence means *not applicable*, and is never read as "zero tools" -- otherwise every shell job on
   the platform would be classified as a do-nothing run.

A run that invokes one or more tools continues to record `ok` exactly as before.

## 11b. Delivery failure (`delivery_failed`)

A cron run has two halves: the action does the work, and that work is **delivered** to the job's
destination conversation. Before #3161 only the first half decided the run status, so a run whose
output reached nobody was recorded as `ok` with a `null` error -- byte-identical to a run that
delivered perfectly. A destination that had been archived or deleted therefore produced an unbroken
streak of successful-looking runs indefinitely, and the consecutive-error backoff could not help
because no error was ever recorded.

### The `delivery_failed` outcome

`delivery_failed` is a **terminal, non-success** run status alongside `ok`, `error`, `timed_out`,
and `no_tool_calls`:

| Property | Behaviour |
| --- | --- |
| Run history | Written as a normal terminal row, with an `error` reason naming the delivery failure and quoting the underlying cause. Not `null`. |
| `lastRunStatus` | Set on the job, so the portal's run-status badge shows the run did not succeed. |
| Failure alerts | Participates in the existing `failureAlertConversationId` path with the same power-of-two backoff. There is deliberately **no** second notification channel. |
| Streak counting | Counts toward the alert streak alongside `error` and `no_tool_calls`, so a job whose destination is permanently gone does not alert on every single run. |
| Retention | Purged by `PurgeRunsOlderThanAsync` like any other terminal status. |
| `failedOnly` history | Included in the `cron` tool's failed-only view. |

It is a **separate status rather than a reuse of `error`** because the two demand different operator
responses: `error` means the job is broken, `delivery_failed` means the job works and its
*destination* is broken. Collapsing them would send an operator debugging a healthy job.

### When `delivery_failed` is recorded

Only when an action reported a delivery failure. Today that is the `agent-prompt` path, when the
job's pinned destination conversation no longer resolves: the run still executes and its output
still lands somewhere (a freshly created conversation, which the scheduler's CAS then reconciles),
but because that is not where the operator is reading, the run is recorded as undelivered rather
than as a success.

An action that never performs a conversation delivery at all -- `command` and `webhook` have no
delivery concept -- reports nothing, and that silence is *not applicable*, never "delivery failed".
A silent action can never reach this status.

When both conditions apply to one run, **`delivery_failed` outranks `no_tool_calls`**: if the output
reached nobody, that is the actionable problem to fix first.

### Fail-closed when the alert itself cannot be delivered

Alert delivery still **never** fails or alters a cron run -- a broken alert channel must not convert
one failure into a second, different one. But it is no longer silently discarded either. When the
failure alert cannot be delivered and no alternate destination is configured, the reason is appended
to the run's recorded error (and to the job's `lastRunError`) behind the marker:

```text
Failure alert could not be delivered: <reason>
```

The run's *status* is left exactly as it was decided. Only the error text grows. This makes the
double failure -- the run failed **and** nobody could be told -- discoverable from run history
instead of existing only as one log line that nothing queries and no operator reads.

## 11c. Operator abort (`aborted`)

Deleting or disabling a cron job used to do nothing at all to a run that was **already executing**
(#3160). `DeleteJobAsync` removed the row and returned; the in-flight action kept burning a model
turn and tool budget, kept writing into the conversation that had just been archived, raced the
session sweep that was concurrently deleting the rows it was writing, and could still fire a failure
alert for a job the operator had deliberately removed. The scheduler simply kept no handle to signal
-- `_jobLocks` is a serialisation mutex, and the per-run timeout source was a method local.

The scheduler now keeps a registry of in-flight runs keyed by run id, each holding the cancellation
source the run executes under. Both operator paths -- delete, and the `enabled: true -> false`
transition of an update -- cancel that source before anything is torn down.

### The `aborted` outcome

`aborted` is a **terminal** run status, and deliberately **not a failure**:

| Property | Behaviour |
| --- | --- |
| Run history | Written as a normal terminal row, with a reason naming the operator delete/disable. |
| `lastRunStatus` | Set on the job (when the job still exists -- a delete removes the row shortly after). |
| Failure alerts | **None.** An operator who killed the job does not need to be alarmed about it. |
| Streak counting | Excluded from the alert streak, so a deliberate stop cannot distort the backoff position of the next genuine failure. |
| Retention | Purged by `PurgeRunsOlderThanAsync` like any other terminal status. |
| `failedOnly` history | **Excluded** -- it is not a failure, and the operator already knows. |

It is a separate status rather than a reuse of `error` or `timed_out` because those two mean
"something went wrong" and this means "someone meant to stop it". An operator scanning history must
be able to tell the three apart.

### Host cancellation is *not* an operator abort

A gateway shutdown or scheduler stop keeps its pre-#3160 shape verbatim: recorded as `error`, and
the `OperationCanceledException` still propagates to the caller. Only a delete or disable produces
`aborted`, and that call does **not** throw -- the caller asked for the run to stop, so reporting
their successful request as a fault would be wrong.

### Ordering and the grace period

Cancellation is issued **before** the conversation archive and the run-session sweep, and the delete
then waits for each cancelled run to actually *observe* the cancellation and leave its action body.
That ordering is the substance of the fix: archiving or sweeping while the action is still writing
is what resurrected archived conversations and destroyed session rows mid-write.

The wait is bounded by `activeRunCancellationGraceSeconds` (default 30) and **fails open**. An
action that swallows its cancellation token must never be able to make its job permanently
undeletable, so when the grace elapses the scheduler logs a warning and the delete proceeds anyway.
The operator's removal always wins.

A job with no run in flight skips the wait entirely.

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
  "cron": {
    "jobs": {
      "morning-briefing": {
        "name": "Morning briefing",
        "schedule": "0 9 * * MON-FRI",
        "actionType": "agent-prompt",
        "agentId": "analyst",
        "message": "Generate a concise morning briefing on recent alerts and incidents.",
        "timeZone": "America/New_York",
        "enabled": true
      }
    }
  }
}
```

**Behavior:**
- Runs at 9:00 AM Monday-Friday (Eastern Time)
- Runs in the job's canonical `cron:morning-briefing` conversation, so briefings build on history
- Agent output is delivered through the agent's own channel bindings

---

### Example 2: Zero-Token Command Job

Run a shell script every hour with no model turn.

```json
{
  "cron": {
    "jobs": {
      "disk-space-check": {
        "name": "Disk space check",
        "schedule": "0 * * * *",
        "actionType": "command",
        "shellCommand": "pwsh -NoProfile -File ./scripts/check-disk.ps1",
        "timeZone": "UTC",
        "enabled": true
      }
    }
  }
}
```

**Behavior:**
- Runs hourly on the hour, UTC
- Costs no tokens; stdout/stderr are captured on the run record
- Authorized through the `exec` tool policy at both authoring and firing time

---

### Example 3: Nightly Memory Consolidation

Run an LLM memory-consolidation pass every night.

```json
{
  "cron": {
    "jobs": {
      "nightly-dreaming": {
        "name": "Nightly memory consolidation",
        "schedule": "0 2 * * *",
        "actionType": "memory-dreaming",
        "agentId": "analyst",
        "timeZone": "America/Los_Angeles",
        "metadata": {
          "lookbackDays": "14",
          "maxContentChars": "50000"
        },
        "enabled": true
      }
    }
  }
}
```

**Behavior:**
- **2:00 AM** (Pacific, daily): reads the agent's last 14 days of daily notes and writes distilled
  insights back to `MEMORY.md`
- Skips execution if no daily notes exist in the lookback window
- Old run records are purged automatically by the cron run retention service - no cleanup job needed

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
    "action": "create",
    "name": "incident-status-check",
    "agentId": "responder",
    "message": "Check current incident status and alert if anything changed.",
    "schedule": "*/15 * * * *",
    "timeZone": "America/Los_Angeles",
    "enabled": true
  }
}
```

**Response:** the created job as JSON, including the generated `id` used for later
`update`, `run`, `history`, and `delete` calls.

**Later, clean up** (jobs are deleted by `jobId`, not by name):
```json
{
  "tool": "cron",
  "arguments": {
    "action": "delete",
    "jobId": "3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c"
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
│  - Ticks every TickIntervalSeconds (see §3.1 for the default)  │
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
    │ agent-  │  │ command │  │ webhook  │
    │ prompt  │  │         │  │          │
    │         │  │         │  │          │
    └────┬────┘  └────┬────┘  └────┬─────┘
         │            │            │
         ▼            ▼            ▼
    AgentRunner    Shell script   HTTP POST
    (LLM prompt)   (exec policy)  (webhookUrl)

    plus memory-dreaming / skill-review /
    agent-converse / heartbeat actions
         │            │            │
         └────────────┼────────────┘
                      │
         ┌────────────┴────────────┐
         │                         │
         ▼                         ▼
    Run record                 Activity Stream
    (output, error,            (Correlation ID,
     status)                    Event Type,
                                Metadata)
```

---

## Troubleshooting

### Job Not Running

1. **Check if cron service is enabled**: `cron.enabled` is `true` in `config.json`
2. **Check if job is enabled**: `enabled` is `true` in the job definition
3. **Check cron expression**: Use [crontab.guru](https://crontab.guru) to validate
4. **Check timezone**: If the job sets `timeZone`, ensure the IANA id is valid (e.g., `"America/New_York"`)
5. **Check logs**: Look for `"Registered cron job"` on startup; verify schedule format

### Output Not Where Expected

1. **Check the action type**: output routing follows the action - see [Output Routing](#output-routing)
2. **For `agent-prompt`**: the response lands in the job's `cron:{jobId}` conversation and the agent's own channel bindings
3. **For `command`**: stdout/stderr are on the run record, not on a channel
4. **For `webhook`**: confirm `webhookUrl` passed validation (absolute `http`/`https`, no credentials)
5. **Check the run record**: `GET /api/cron/{jobId}/runs` or the `cron` tool's `history` action

### Job Failing

1. **Check error logs**: Look for `"Cron job '{JobName}' failed"` with exception details
2. **Check execution history**: `GET /api/cron/{jobId}/runs` shows recent failures
3. **Check correlation ID**: Use correlation ID to trace through activity stream
4. **For `agent-prompt` jobs**: Check if the agent is configured and available
5. **For `command` jobs**: Verify the command is permitted by the `exec` tool policy

---

## See Also

- [Configuration Guide](./configuration.md) — Full configuration reference
- [Architecture Overview](./architecture/overview.md) — System architecture and component interactions
- [Extension Development](./extension-development.md) — Creating custom system actions and channels
- [Workspace and Memory](./development/workspace-and-memory.md) — Agent memory consolidation details

