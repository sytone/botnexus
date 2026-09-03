# Cron API Reference

Reference for the **Cron** endpoints — create, inspect, update, delete, and manually
trigger scheduled jobs, and read their run history.

All endpoints are served under the base route `api/cron` and require the gateway API
key (see [Authentication](README.md#authentication)).

Source: `src/gateway/BotNexus.Gateway.Api/Controllers/CronController.cs`.

---

## Endpoints

| Verb | Route | Purpose |
|------|-------|---------|
| GET | `/api/cron` | List cron jobs (persisted + configured). |
| GET | `/api/cron/{jobId}` | Get a single cron job. |
| POST | `/api/cron` | Create a cron job. |
| PUT | `/api/cron/{jobId}` | Update a cron job. |
| DELETE | `/api/cron/{jobId}` | Delete a cron job (and archive its pinned conversation). |
| POST | `/api/cron/{jobId}/run` | Trigger an immediate run. |
| GET | `/api/cron/{jobId}/runs` | List run history for a job. |

---

### `GET /api/cron`

Lists all cron jobs. The response merges jobs persisted in the cron store with jobs
declared in configuration (`cron.jobs`) that are not already persisted. Results are
ordered by `createdAt` descending, and **filtered to the jobs the caller may manage**
(see [Ownership](#ownership)).

Returns `200 OK` with a JSON array of `CronJob` objects.

### `GET /api/cron/{jobId}`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `jobId` | path | string | The job identifier. |

Returns `200 OK` with the `CronJob`, `404 Not Found` when it does not exist, or
`403 Forbidden` when it exists but is not manageable by the caller (see [Ownership](#ownership)).

### `POST /api/cron`

Creates a cron job from the request body. `id` is **optional**: when omitted the server
generates one, consistent with how `createdAt` already defaults to the current UTC time.
An explicitly supplied `id` is honoured unchanged. The `actionType` value `agent-chat` is
normalised to `agent-prompt`.

Validation:

- `name` is required, else `400 Bad Request`.
- `schedule` is required, else `400 Bad Request`.
- `nextRunAt`, when present, must fall between `1970-01-01` and `9000-01-01`, else
  `400 Bad Request`.
- `createdAt`, when present, must fall within the same range, else `400 Bad Request`.

Returns `201 Created` with a `Location` header pointing at `GET /api/cron/{jobId}` and
the created job in the body.

### `PUT /api/cron/{jobId}`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `jobId` | path | string | The job identifier. |

Updates an existing job from the request body. The route `jobId` wins over any `id`
in the body, and the original `createdAt` is preserved. `nextRunAt`, when present, is
range-validated as on create.

#### Omitted fields are preserved (#3808)

**A field absent from the request body leaves the stored value unchanged.** Only a field the
caller actually sends is written, so editing a job's schedule cannot silently clear policy the
request never mentioned. This is the same omitted-field rule the `cron` agent tool has applied
since #2634, and the two seams are asserted to agree.

This matters most for the six policy columns, because their CLR defaults are indistinguishable
from a deliberate "turn it off":

| Field | Omitted | Explicit value |
|-------|---------|----------------|
| `failureAlertsEnabled` | keeps stored value | `true`/`false` applied |
| `failureAlertConversationId` | keeps stored value | applied; `null` or `""` **clears** the target |
| `deleteJobAfterRun` | keeps stored value | `true`/`false` applied |
| `deleteAfterRun` | keeps stored value | `true`/`false` applied |
| `expiresAt` | keeps stored value | applied; `null` or `""` **clears** the expiry |
| `executionClass` | keeps stored value | `true`/`false` applied |

The same rule applies to the ordinary definition fields (`name`, `schedule`, `actionType`,
`message`, `templateName`, `templateParameters`, `model`, `webhookUrl`, `shellCommand`,
`enabled`, `system`, `timeZone`, `metadata`, `nextRunAt`).

A partial edit is therefore the recommended shape - send only what changes:

```http
PUT /api/cron/daily-briefing
Content-Type: application/json
X-Api-Key: <key>

{ "schedule": "0 9 * * *" }
```

The job's alert routing, one-shot disposition, expiry and execution class all survive that request.
Round-tripping the full record returned by `GET /api/cron/{jobId}` also remains correct, since
nothing is omitted.

Only a **supplied** `failureAlertConversationId` is validated against the conversation store. A
retained one is not re-checked, so a job whose alert conversation was later deleted stays editable
rather than becoming permanently un-saveable because of a field the caller never touched.

`agentId` and `createdBy` are **not** caller-authored on this route (#3575). `createdBy` is
server-stamped provenance and is always taken from the stored row; `agentId` moves only to an
agent the authenticated caller is itself scoped to, and otherwise keeps its stored value. This
mirrors the existing `scheduleActivatedAt` stripping (#2554) - any column the store writes must be
governed explicitly here. The scheduler-owned runtime bookkeeping (`lastRunAt`, `lastRunStatus`,
`lastRunError`, `backoffUntil`, `conversationId`) is likewise not accepted from this route (#2133).

Returns `200 OK` with the updated job, `404 Not Found` when the job does not exist,
`403 Forbidden` when the job exists but is not manageable by the caller, or `409 Conflict` when
the job's ownership changed between the authorization check and the write (#3573).

The `409` is distinct from the `403` on purpose. A `403` means the caller may not touch this job
at all; a `409` means the caller *was* authorized, but `createdBy`/`agentId` moved while the
request was in flight, so the commit was refused rather than applied against a stale owner. The
remedy differs accordingly: re-read the job and retry.

### `DELETE /api/cron/{jobId}`

Deletes a cron job through the scheduler, which also archives the job's pinned
conversation. Returns `204 No Content`, or `403 Forbidden` when the job exists but is not
manageable by the caller.

### Ownership

`GET`, `PUT` and `DELETE` all apply the same ownership rule as the `cron` agent tool, through the
shared `CronJobOwnership` predicate: a job is manageable when the caller is scoped to the agent that
created it or to the agent it targets. A caller whose API key carries no `allowedAgents` scope,
or which is marked `isAdmin`, is already trusted platform-wide by the gateway auth middleware and
is not further restricted here.

The read routes were unscoped until #3778: a caller limited to one agent could enumerate every job
definition on the platform (including `shellCommand` and `webhookUrl`), every run's `sessionId`, and
platform-wide cost rollups. The single-job routes now answer `403`; the collection routes (`GET
/api/cron` and `GET /api/cron/costs`) filter their result set instead, since there is no single
subject to refuse.

An unauthorized target answers `403 Forbidden`, not `404` - the caller has already learned the
job exists from the route's own `404` contract, so collapsing the two would trade a truthful
authorization answer for an existence-oracle defence this endpoint does not provide anyway.

### `POST /api/cron/{jobId}/run`

Triggers an immediate execution of the job. Returns `202 Accepted` with a `CronRun`
describing the started run, or `404 Not Found` when the job does not exist.

### `GET /api/cron/{jobId}/runs`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `jobId` | path | string | The job identifier. |
| `limit` | query | int | Maximum runs to return. Defaults to `20`. |

Returns `200 OK` with a JSON array of `CronRun` objects (most recent first),
`404 Not Found` when the job does not exist, or `403 Forbidden` when it exists but is not
manageable by the caller (see [Ownership](#ownership)). Run records carry the `sessionId` that
keys into the owning agent's transcript, so the check runs before the history is read.

Each `CronRun` carries a `cost` object with the per-run measurements recorded at run
finalization (#2641):

| Field | Type | Notes |
|-------|------|-------|
| `turnCount` | int? | Model turns the run consumed. |
| `toolCallCount` | int? | Tool invocations the run performed. |
| `durationMs` | long? | Wall-clock duration, stamped by the scheduler. |
| `promptTokens` | long? | Provider-reported prompt tokens summed across the run. |
| `completionTokens` | long? | Provider-reported completion tokens summed across the run. |
| `totalTokens` | long? | Derived sum; `null` when neither side was measured. |

**Every field is nullable and `null` means "not measured", never zero.** A `command` or
`webhook` job has no turn or token concept, and a run recorded before these columns existed
measured nothing. Rendering or aggregating a `null` as `0` would present an unmeasured run as
a free one and invert the cost ranking.

---

### `GET /api/cron/costs`

Per-job cost rollup derived from run history, ordered by **total** spend descending. The rollup is
built from the jobs the caller may manage (see [Ownership](#ownership)); a scoped caller that owns
no jobs receives an empty array rather than an unscoped query.

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `windowDays` | query | int | Days of run history to roll up. Defaults to `7`. Clamped to the configured run retention. |

Returns `200 OK` with a JSON array of rollups:

| Field | Type | Notes |
|-------|------|-------|
| `jobId` | string | The job. |
| `runCount` | int | Runs in the window, including unmeasured ones. |
| `measuredRunCount` | int | Runs that reported at least one token measurement. |
| `totalTokens` | long? | Total across measured runs; `null` when nothing was measured. |
| `totalToolCalls` / `totalTurns` / `totalDurationMs` | long? | Same nullable semantics. |
| `averageTokensPerRun` | double? | Divided by `measuredRunCount`, never by `runCount`. |
| `windowDays` | int | The **effective** window after retention clamping. |
| `windowTruncatedByRetention` | bool | `true` when the requested window exceeded retention. |

**Ranking is by total, not by per-run average**, because the two disagree and total is the
question worth asking: a job costing ~17k tokens per run that fires 193 times a week is a far
larger consumer than one costing ~65k per run that fires 8 times. Sorting on the per-run
figure alone reports the larger consumer as the cheaper job.

`windowTruncatedByRetention` exists because `CronRunRetentionOptions.RetentionDays` (default
30) has already purged older runs. Asking for a 90-day window silently yields a 30-day total
unless the clamp is surfaced — a truncated number that looks exactly like a complete one.

---

## Example

**Create a job**

```http
POST /api/cron
Content-Type: application/json
X-Api-Key: <key>

{
  "id": "daily-briefing",
  "name": "Daily briefing",
  "schedule": "0 8 * * *",
  "actionType": "agent-prompt",
  "agentId": "farnsworth",
  "message": "Give me my morning briefing.",
  "enabled": true
}
```

**Response**

```
201 Created
Location: /api/cron/daily-briefing
```

**Mark a job execution-class (#2985)**

Set `executionClass: true` on a job whose contract is to *perform work*. A run of such a job that
completes having made zero tool calls is recorded with status `no_tool_calls` instead of `ok`, and
drives the existing `failureAlertConversationId` path. The field is optional and defaults to
`false`, so an existing payload that omits it is unaffected. See
[Zero-tool-call runs](../cron-and-scheduling.md#11b-zero-tool-call-runs-2985).

```http
POST /api/cron
Content-Type: application/json
X-Api-Key: <key>

{
  "name": "Autonomous maintenance",
  "schedule": "0 * * * *",
  "actionType": "agent-prompt",
  "agentId": "farnsworth",
  "message": "Perform the hourly maintenance pass.",
  "executionClass": true,
  "failureAlertsEnabled": true,
  "failureAlertConversationId": "c_ops_alerts",
  "enabled": true
}
```

**Create a command (script) job without supplying an id**

```http
POST /api/cron
Content-Type: application/json
X-Api-Key: <key>

{
  "name": "Disk space check",
  "schedule": "0 * * * *",
  "actionType": "command",
  "shellCommand": "pwsh -NoProfile -File ./scripts/check-disk.ps1",
  "enabled": true
}
```

**Response**

```
201 Created
Location: /api/cron/3f1c8b0a9d2e4f5a8b7c6d5e4f3a2b1c
```
