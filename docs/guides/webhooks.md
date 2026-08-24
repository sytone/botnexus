# Webhooks Guide

Webhooks let external systems deliver a message to a BotNexus agent over plain
HTTP. A monitoring alert, a form submission, a CI pipeline, a cron job — anything
that can make an authenticated HTTP `POST` can drive an agent run and (optionally)
receive the agent's response.

This guide covers the concepts, the three response modes, request signing, pinning
to a conversation, store-only delivery, error handling, and polling for async runs.
For the exhaustive endpoint/field reference see the
[Webhooks API reference](../api/webhooks.md).

---

## Concepts

A **webhook registration** binds an inbound URL to a target agent. Creating a
registration returns:

- a stable `webhookId`,
- the inbound `url` external systems POST to, and
- a one-time **secret** used to sign requests.

Each inbound POST creates a **webhook run** — a record tracking the lifecycle of
that single delivery (`Pending` → `Running` → `Completed`/`Failed`/`Timeout`).

```
┌──────────────┐   POST (signed)   ┌───────────────────┐   dispatch   ┌────────┐
│ External sys │ ────────────────► │ /api/webhooks/...  │ ───────────► │ Agent  │
└──────────────┘                   └───────────────────┘              └────────┘
        ▲                                   │  202 + pollUrl                │
        └───────────── poll / callback ─────┴───────────────────────────────┘
```

All routes live under `api/webhooks`. Registration management is handled by the
registrations endpoints; external systems deliver messages to the inbound
endpoint `POST /api/webhooks/{agentId}/{webhookId}`.

---

## Automatic per-agent provisioning

Everything below describes registering a webhook **by hand**, which is still the right
path for an arbitrary external caller. Separately, BotNexus reconciles one
**platform-owned registration per agent** automatically from agent lifecycle, so a
downstream system that needs a callback for every agent does not have to be wired up
by a setup script that nobody remembers to re-run.

How it behaves:

- **On agent create and on agent update**, the gateway ensures a registration exists
  for that agent and pushes the binding (agent id, current display name, webhook id,
  inbound path, secret) to every configured delivery target.
- **On agent delete**, the registration is removed and the target is told the agent is
  gone. This step is best-effort: a downstream outage never blocks deleting an agent.
- **At startup**, every registered agent is reconciled again. That pass is deliberately
  the only recovery path — there is no retry queue or outbox for a failed push.

Provisioner-owned registrations carry the deterministic label
`agent-webhook:<agentId>`. Two consequences worth knowing:

- **Provisioning never rotates a secret.** When a labelled registration already exists
  the store is not written to at all and the existing secret is re-sent. A display-name
  change therefore cannot break a binding the downstream system already holds. (An
  agent id itself is immutable in BotNexus, so the label is stable by construction.)
- **A registration you created by hand is never touched**, because it does not carry
  that label. It survives agent deletion.

### Configuring a delivery target

Delivery targets ship as extensions; the gateway core names no downstream product. The
bundled TaskNexus target is inert until you configure a base URL:

| Key | Meaning |
|---|---|
| `extensions:tasknexus:baseUrl` | Base URL of the TaskNexus instance to notify. **Absent means disabled** — no outbound call is attempted at all. |
| `extensions:tasknexus:callbackOrigin` | Externally reachable gateway origin, prepended to the relative inbound path. The gateway does not reliably know its own public origin, so a target that needs an absolute URL composes one from this. |

With no target configured, startup completes normally and the provisioner still keeps
the registrations themselves in sync — there is simply nothing to push them to.

## Quick start

### 1. Register a webhook

This is the manual path, for an external caller that is not tied to an agent's
lifecycle. For the automatic per-agent binding, see
[Automatic per-agent provisioning](#automatic-per-agent-provisioning) above.

```bash
curl -X POST https://your-host/api/webhooks/registrations \
  -H "Content-Type: application/json" \
  -d '{
        "agentId": "my-agent",
        "label": "Alertmanager bridge",
        "defaultResponseMode": "async"
      }'
```

The `201 Created` response includes the `secret` **exactly once**. Store it
securely — it is never returned again.

```json
{
  "webhookId": "wh_9f2c...",
  "label": "Alertmanager bridge",
  "agentId": "my-agent",
  "conversationId": null,
  "enabled": true,
  "defaultResponseMode": "async",
  "url": "https://your-host/api/webhooks/my-agent/wh_9f2c...",
  "secret": "whsec_3a1b...64hex...",
  "createdAt": "2026-07-06T12:00:02Z",
  "lastUsedAt": null
}
```

### 2. Send a signed message

Compute an `X-BotNexus-Signature-256` header over the **raw request body** (see
[Security](#security-hmac-sha256-signing)), then POST:

```bash
curl -X POST https://your-host/api/webhooks/my-agent/wh_9f2c... \
  -H "Content-Type: application/json" \
  -H "X-BotNexus-Signature-256: sha256=<hex>" \
  -d '{ "message": "Disk usage on db-01 is at 95%." }'
```

For async mode you get `202 Accepted` with a `Location` header and a poll URL:

```json
{
  "runId": "whr_51ab...",
  "pollUrl": "https://your-host/api/webhooks/runs/whr_51ab...",
  "conversationId": "conv_77de..."
}
```

### 3. Poll for the result

```bash
curl https://your-host/api/webhooks/runs/whr_51ab...
```

```json
{
  "runId": "whr_51ab...",
  "webhookId": "wh_9f2c...",
  "status": "Completed",
  "acceptedAt": "2026-07-06T12:00:02Z",
  "startedAt": "2026-07-06T12:00:02Z",
  "completedAt": "2026-07-06T12:00:41Z",
  "agentResponse": "I've acknowledged the db-01 disk alert and opened...",
  "error": null,
  "conversationId": "conv_77de...",
  "sessionId": "sess_..."
}
```

---

## Response modes

The response mode controls how/when the caller gets the agent's answer. Set it per
call via `responseMode` in the request body, or default it on the registration via
`defaultResponseMode`. If neither is set, **async** is used.

| Mode       | HTTP | Behaviour |
|------------|------|-----------|
| `async`    | 202  | Returns immediately with a `Location` poll URL; agent runs in the background. **Recommended default.** |
| `sync`     | 200  | Holds the connection open until the agent completes (up to 120s), returns the response inline. |
| `callback` | 202  | Returns immediately; POSTs the result to your `callbackUrl` when the run finishes. |

### async (default, recommended)

LLM runs commonly take 30–120 seconds, which exceeds the 3–10 second timeout most
external systems enforce. Async avoids that entirely: you get an immediate `202`
plus a `pollUrl` (also mirrored in the `Location` response header), then
[poll for completion](#polling-for-async-runs).

### sync

Sync holds the HTTP connection open until the agent finishes and returns
`200 OK` with the full agent response inline:

```json
{
  "runId": "whr_51ab...",
  "agentResponse": "Acknowledged. Opened incident INC-4821.",
  "conversationId": "conv_77de..."
}
```

Use sync only for internal or low-latency callers that tolerate long-held
connections. The server enforces a **120-second** ceiling; if the agent does not
complete in time the run is marked `Timeout` and the call downgrades to a `202`
with a `Location` poll URL — so a well-behaved client should still be prepared to
poll.

### callback

Callback returns `202` immediately and delivers the result later by POSTing JSON to
the `callbackUrl` you supply in the request body:

```bash
curl -X POST https://your-host/api/webhooks/my-agent/wh_9f2c... \
  -H "Content-Type: application/json" \
  -H "X-BotNexus-Signature-256: sha256=<hex>" \
  -d '{
        "message": "Summarise the overnight batch report.",
        "responseMode": "callback",
        "callbackUrl": "https://your-host/hooks/botnexus-result"
      }'
```

The callback POST body looks like:

```json
{
  "runId": "whr_51ab...",
  "webhookId": "wh_9f2c...",
  "status": "Completed",
  "agentResponse": "The overnight batch processed 12,400 records...",
  "conversationId": "conv_77de...",
  "completedAt": "2026-07-06T12:03:10Z"
}
```

`callbackUrl` is **required** when `responseMode` is `callback`. Callback URLs are
validated against SSRF rules before delivery; unsafe targets (loopback, private
ranges, etc.) are blocked and the callback is silently skipped. The run itself
still completes and remains available via the [poll endpoint](#polling-for-async-runs),
so treat the callback as best-effort and keep polling as a fallback.

---

## Security: HMAC-SHA256 signing

Every inbound POST **must** carry a valid signature header:

```
X-BotNexus-Signature-256: sha256=<lowercase-hex>
```

The value is `sha256=` followed by the lowercase hex encoding of
`HMAC-SHA256(key = secret_utf8_bytes, message = raw_request_body_bytes)`. This is
the same convention GitHub and Stripe use. The gateway recomputes the signature
and compares it in constant time; a mismatch or missing header returns:

```json
HTTP/1.1 401 Unauthorized
{ "error": "Invalid signature." }
```

Key points:

- The secret has the form `whsec_<64 hex chars>` and is shown **once** at
  registration.
- Sign the **exact raw bytes** you send. If you re-serialize the JSON after
  signing (changing whitespace, key order, or encoding) the signature will not
  match.
- The secret is stored plaintext in the gateway's SQLite database, protected by
  OS-level file permissions — the same posture as the gateway API token in
  `config.json`. Rotate a leaked secret by updating (or recreating) the
  registration.

### Worked example (Python)

```python
import hashlib
import hmac
import json
import requests

secret = "whsec_3a1b...64hex..."          # from registration (shown once)
url = "https://your-host/api/webhooks/my-agent/wh_9f2c..."

# Serialize ONCE and sign those exact bytes.
payload = {"message": "Disk usage on db-01 is at 95%."}
raw = json.dumps(payload).encode("utf-8")

digest = hmac.new(secret.encode("utf-8"), raw, hashlib.sha256).hexdigest()
signature = f"sha256={digest}"

resp = requests.post(
    url,
    data=raw,                              # send the SAME bytes we signed
    headers={
        "Content-Type": "application/json",
        "X-BotNexus-Signature-256": signature,
    },
)
print(resp.status_code, resp.json())
```

The produced header looks like:

```
X-BotNexus-Signature-256: sha256=9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08
```

> The hex above is illustrative — your value depends on your secret and body.

The verification the gateway performs is equivalent to:

```python
expected = "sha256=" + hmac.new(secret.encode(), raw, hashlib.sha256).hexdigest()
valid = hmac.compare_digest(expected, received_header)   # constant-time
```

For signing snippets in other languages (JavaScript/PowerShell/Python), see the
`examples/webhooks/` directory in the repository.

---

## Pinning to a conversation

By default, each registration lazily creates and pins a single conversation on
first delivery, so all messages through that webhook land in the **same**
conversation and share history. The chosen conversation is returned as
`conversationId` on every run.

To pin to a specific existing conversation up front, pass `conversationId` when
creating the registration:

```bash
curl -X POST https://your-host/api/webhooks/registrations \
  -H "Content-Type: application/json" \
  -d '{
        "agentId": "my-agent",
        "label": "Incident channel bridge",
        "conversationId": "conv_existing_123",
        "defaultResponseMode": "async"
      }'
```

Pinning is useful when you want webhook-delivered messages to join an ongoing
thread (for example, an incident conversation) rather than a fresh one. The
`agentId` and `secret` are immutable after creation; the pinned conversation is
established on the registration and reused for all subsequent runs.

---

## Concurrency policy

Webhook delivery follows one explicit rule:

> **The canonical conversation is the unit of isolation.**

A conversation owns its `active_session_id`, message history, pending `ask_user`
state, todo/canvas state and finalizer writes. Two agent turns running at once in
one conversation would stomp all of it, so the gateway serializes them instead.

What this means in practice:

- **One canonical conversation per registration.** Each registration lazily pins
  exactly one conversation and reuses it for every delivery.
- **One stable active session** per registration/conversation for as long as that
  session remains reusable. Each run records the session it resolved to.
- **Deliveries sharing a conversation are FIFO serialized** - including deliveries
  arriving through *different* registrations that are pinned to the same
  conversation. Ordering follows arrival order; turns never overlap.
- **A run is an execution record, not a conversation.** Every delivery gets its own
  run ID and status, but runs never materialize as sidebar conversations.
- **Parallelism requires separate conversations.** Deliveries to different
  conversations run concurrently. If you need true parallel processing, use
  separate conversations - never concurrent sessions over one conversation.

### How this applies per response mode

| Mode | Isolation |
| --- | --- |
| `async` | Serialized per conversation. The 202 returns immediately; the queued turn still waits its place in the conversation's FIFO order. |
| `sync` | Serialized per conversation. The connection is held while the turn waits its turn and then executes, so a busy conversation makes a sync call wait. |
| `callback` | Serialized per conversation, identical to `async`. The callback fires after that delivery's turn completes. |
| `agentAction: false` | No agent turn runs, so the delivery never enters the queue and is outside the isolation boundary entirely. |

> **Sync mode and busy conversations.** Because sync holds the connection while
> queued, a backlog on a shared conversation counts against the 120s sync timeout.
> Use `async` for high-volume registrations, or give them their own conversations.

---

## Store-only delivery (`agentAction: false`)

Set `agentAction` to `false` to record the message **without** triggering an agent
run. The message is appended to the conversation's session for audit or later
aggregation, but no LLM invocation happens.

```bash
curl -X POST https://your-host/api/webhooks/my-agent/wh_9f2c... \
  -H "Content-Type: application/json" \
  -H "X-BotNexus-Signature-256: sha256=<hex>" \
  -d '{ "message": "Heartbeat OK at 12:00Z", "agentAction": false }'
```

The response is `202 Accepted` and the run is immediately marked `Completed` with
a `null` `agentResponse` — there is nothing for the agent to say because it never
ran. `agentAction` defaults to `true`.

The message lands in the **same** session an `agentAction: true` delivery would
resolve, so successive store-only posts accumulate in one conversation transcript
and are readable from that conversation's history. The only difference between the
two modes is whether an agent turn runs, not where the message is stored.

If no session can be resolved for the target conversation the endpoint returns
`503 Service Unavailable` and the run is marked `Failed` — a store-only delivery
never reports `202` for a write that did not land, so a caller always has a signal
to retry.

Use this for high-volume signal capture (heartbeats, telemetry, log lines) that you
want on the record but do not want to spend an LLM run on. A later message with
`agentAction: true` (the default) can then ask the agent to summarise everything
accumulated in the conversation.

---

## Error handling

| Status | Meaning | Typical cause |
|--------|---------|---------------|
| `400 Bad Request` | `{"error":"Invalid JSON body."}` or `{"error":"message is required."}` | Malformed JSON, or empty/missing `message`. |
| `401 Unauthorized` | `{"error":"Invalid signature."}` | Missing/incorrect `X-BotNexus-Signature-256`, or body altered after signing. |
| `404 Not Found` | `{"error":"Webhook '...' not found or disabled."}` | Unknown `webhookId`, or the registration has `enabled: false`. |
| `503 Service Unavailable` | `{"error":"Agent run did not complete."}` | Sync mode where the agent finished abnormally. |

A run that fails during agent execution is not surfaced as an inbound HTTP error
(the POST was already accepted). Instead the run's `status` becomes `Failed` and
`error` carries the message — inspect it via the poll endpoint:

```json
{
  "runId": "whr_51ab...",
  "status": "Failed",
  "error": "The model provider returned 429 Too Many Requests.",
  "agentResponse": null
}
```

Recommended client behaviour:

- Treat any non-2xx from the inbound POST as a delivery failure and retry with the
  **same** signed body (or re-sign if you rebuild the body).
- For async/callback, always confirm final state via the poll endpoint — do not
  assume `202` means the agent succeeded.
- Disabled registrations return `404`; re-enable via the update endpoint rather
  than recreating (which would rotate the secret).

---

## Polling for async runs

After a `202`, poll `GET /api/webhooks/runs/{runId}` until `status` reaches a
terminal state (`Completed`, `Failed`, or `Timeout`). Use modest backoff — LLM
runs typically settle in 30–120 seconds.

```python
import time
import requests

def wait_for_run(base, run_id, interval=3, timeout=180):
    deadline = time.time() + timeout
    while time.time() < deadline:
        run = requests.get(f"{base}/api/webhooks/runs/{run_id}").json()
        status = run["status"]
        if status in ("Completed", "Failed", "Timeout"):
            return run
        time.sleep(interval)
    raise TimeoutError(f"Run {run_id} did not finish within {timeout}s")

run = wait_for_run("https://your-host", "whr_51ab...")
if run["status"] == "Completed":
    print(run["agentResponse"])
else:
    print("Run did not succeed:", run.get("error"))
```

Run statuses:

| Status | Terminal | Meaning |
|--------|----------|---------|
| `Pending`   | no  | Accepted, not yet started. |
| `Running`   | no  | Agent is executing. |
| `Completed` | yes | Finished; `agentResponse` populated (or `null` for store-only). |
| `Failed`    | yes | Agent execution errored; see `error`. |
| `Timeout`   | yes | Sync mode exceeded the 120s ceiling. |

You can also list recent runs for a registration via
`GET /api/webhooks/registrations/{webhookId}/runs?limit=20` (limit is clamped to
1–100).

---

## See also

- [Webhooks API reference](../api/webhooks.md) — every endpoint, field, and status
  code.
- [Cron & scheduling](../cron-and-scheduling.md) — triggering webhooks on a
  schedule.
- `examples/webhooks/` — ready-to-run signing/sending examples.
