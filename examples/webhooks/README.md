# BotNexus Webhook Examples

Runnable examples for sending **signed inbound webhooks** to a BotNexus gateway
in several languages. Each example signs the request body with HMAC-SHA256 and
posts it to the inbound delivery endpoint, then (in async mode) polls for the
agent's response.

| Language   | Directory                                          | Runtime            |
| ---------- | -------------------------------------------------- | ------------------ |
| JavaScript | [`javascript/`](./javascript/)                     | Node.js 18+        |
| PowerShell | [`powershell/`](./powershell/)                     | PowerShell 7+      |
| Python     | [`python/`](./python/)                             | Python 3.9+        |

> The Python example lives in [`python/`](./python/) and is maintained
> separately. If that directory is not present yet, it is being added under a
> companion change.

## How webhooks work

1. **Register** a webhook (`POST api/webhooks/registrations`). The response
   returns the inbound `url` and the signing `secret` **once** — save it. The
   secret has the format `whsec_<64 hex chars>`.
2. **Send** a signed request to
   `POST api/webhooks/{agentId}/{webhookId}` with the header
   `X-BotNexus-Signature-256: sha256=<hex>`, where `<hex>` is the lowercase
   hex HMAC-SHA256 of the **exact raw request body** using the secret.
3. **Get the result**, depending on the response mode:
   - **async** (default): returns `202` with `{ runId, pollUrl, conversationId }`
     and a `Location` header. Poll `GET api/webhooks/runs/{runId}` until the run
     completes (`agentResponse` is populated). Recommended — LLM runs take
     30–120s, longer than most callers' timeouts.
   - **sync**: holds the connection open and returns `200` with the full
     response inline. Only safe for low-latency internal callers.
   - **callback**: returns `202` immediately and POSTs the result to your
     `callbackUrl` when done.

### Request body

```json
{
  "message": "string (required)",
  "responseMode": "async | sync | callback | null",
  "agentAction": true,
  "callbackUrl": null
}
```

- `agentAction`: `false` records the message without running the agent.
- `callbackUrl`: required only when `responseMode` is `callback`.

### Signing rule (important)

Serialize the body **once**, sign that exact string, and send that exact
string. Re-serializing after signing (e.g. pretty-printing, key reordering)
changes the bytes and produces a `401 Invalid signature.`

## Prerequisites

- A running BotNexus gateway (default base URL `http://localhost:5000`).
- A registered webhook and its `agentId`, `webhookId`, and signing secret.
- The secret exported as an environment variable:

  ```bash
  export BOTNEXUS_WEBHOOK_SECRET='whsec_...'
  ```

  ```powershell
  $env:BOTNEXUS_WEBHOOK_SECRET = 'whsec_...'
  ```

## Quickstart

### JavaScript (Node.js 18+)

No dependencies to install — uses native `fetch` and `node:crypto`.

```bash
cd javascript
BOTNEXUS_WEBHOOK_SECRET='whsec_...' \
node webhook-sender.js \
  --base http://localhost:5000 \
  --agent my-agent \
  --webhook 1a2b3c \
  --message "Hello from the webhook example"
```

See [`javascript/webhook-sender.js`](./javascript/webhook-sender.js). A
browser-compatible Web Crypto (`SubtleCrypto`) signing variant is documented in
a comment near the top of that file.

### PowerShell (7+)

```powershell
$env:BOTNEXUS_WEBHOOK_SECRET = 'whsec_...'
./powershell/Send-BotNexusWebhook.ps1 `
  -AgentId my-agent `
  -WebhookId 1a2b3c `
  -Message 'Hello from the webhook example' `
  -Verbose
```

Or dot-source it to reuse the function:

```powershell
. ./powershell/Send-BotNexusWebhook.ps1
$run = Send-BotNexusWebhook -AgentId my-agent -WebhookId 1a2b3c -Message 'Hi'
$run.agentResponse
```

See [`powershell/Send-BotNexusWebhook.ps1`](./powershell/Send-BotNexusWebhook.ps1).

### Python (3.9+)

See [`python/`](./python/) for the standard-library (`hmac` + `urllib`)
example and its quickstart.
