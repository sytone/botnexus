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
ordered by `createdAt` descending.

Returns `200 OK` with a JSON array of `CronJob` objects.

### `GET /api/cron/{jobId}`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `jobId` | path | string | The job identifier. |

Returns `200 OK` with the `CronJob`, or `404 Not Found` when it does not exist.

### `POST /api/cron`

Creates a cron job from the request body (a `CronJob`). If `createdAt` is omitted it
is set server-side to the current UTC time. The `actionType` value `agent-chat` is
normalised to `agent-prompt`.

Validation:

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

Returns `200 OK` with the updated job, or `404 Not Found` when the job does not exist.

### `DELETE /api/cron/{jobId}`

Deletes a cron job through the scheduler, which also archives the job's pinned
conversation. Returns `204 No Content`.

### `POST /api/cron/{jobId}/run`

Triggers an immediate execution of the job. Returns `202 Accepted` with a `CronRun`
describing the started run, or `404 Not Found` when the job does not exist.

### `GET /api/cron/{jobId}/runs`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `jobId` | path | string | The job identifier. |
| `limit` | query | int | Maximum runs to return. Defaults to `20`. |

Returns `200 OK` with a JSON array of `CronRun` objects (most recent first), or
`404 Not Found` when the job does not exist.

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
