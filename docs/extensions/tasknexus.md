# TaskNexus

The TaskNexus extension keeps an external TaskNexus instance synchronized with BotNexus agent webhook registrations. When BotNexus creates, updates, or removes an agent webhook binding, the extension sends the corresponding binding change to TaskNexus.

## Prerequisites

- A reachable TaskNexus deployment
- Agent webhook provisioning enabled in BotNexus
- An externally reachable BotNexus gateway origin if TaskNexus cannot call the gateway's relative webhook path directly

The extension loads with the standard extension set but is inert until `baseUrl` is configured. An unconfigured installation makes no TaskNexus requests.

## Configuration

TaskNexus settings are extension-owned raw configuration. They are not represented by the typed platform configuration model, so `botnexus config set` currently refuses these paths. On a JSON-backed installation, add the section directly to `config.json`:

```json
{
  "extensions": {
    "tasknexus": {
      "baseUrl": "https://tasks.example.com",
      "callbackOrigin": "https://botnexus.example.com"
    }
  }
}
```

| Key | Required | Description |
|---|---|---|
| `baseUrl` | Yes to enable delivery | Base URL of the TaskNexus instance. BotNexus posts bindings to `<baseUrl>/api/botnexus/agents` and roster diagnostics to `<baseUrl>/api/botnexus/roster`. Omit it to disable all outbound calls. |
| `callbackOrigin` | No | Externally reachable BotNexus gateway origin. When set, it is prepended to the generated `/api/webhooks/{agentId}/{webhookId}` inbound path. When omitted, TaskNexus receives the relative path. |

A SQLite-only configuration home cannot author this extension-owned subtree through the current typed CLI surface. Keep the JSON compatibility source available for this setting until the configuration model exposes it.

## Synchronization behavior

For each current agent binding, BotNexus sends:

```json
{
  "agentId": "researcher",
  "displayName": "Researcher",
  "webhookId": "01J...",
  "url": "https://botnexus.example.com/api/webhooks/researcher/01J...",
  "secret": "<generated-secret>"
}
```

The provisioner invokes the extension when a binding is created or refreshed. Removing an agent sends an idempotent `DELETE` to:

```text
<baseUrl>/api/botnexus/agents/{agentId}/{webhookId}
```

Both identifiers are included so a delayed deletion for an old binding cannot erase a newer binding for a recreated agent with the same id.

After startup reconciliation and each create, rename, or removal reaches its terminal state, BotNexus also posts a full diagnostic roster to `/api/botnexus/roster`. A successful heartbeat contains only `status`, `observedAt`, and the canonical `agentId` plus `displayName` for every persistent agent. An empty successful roster is valid and marks all previously observed agents absent. Duplicate identifiers collapse deterministically.

If startup reconciliation fails, BotNexus sends a failure heartbeat containing only `status`, `observedAt`, and the bounded machine code `roster_reconciliation_failed`. Failure heartbeats do not include or change roster membership. Neither heartbeat includes webhook identifiers, callback URLs, secrets, exception text, tokens, or stack traces.

## Failure and recovery

- Non-success responses and network failures are logged as warnings and dropped.
- The extension has no retry queue or outbox.
- Startup reconciliation re-sends bindings for agents currently in the registry, then sends the complete diagnostic roster. It can repair a missed creation or refresh for an agent that is still registered; the full roster marks absent agents without replaying their old binding deletion.
- Removal deletes the local webhook registration before notifying TaskNexus. If that outbound `DELETE` fails, there is no retained deletion record for the next startup to replay. A stale TaskNexus binding can require manual or external cleanup using the removed agent id and webhook id; do not delete a recreated agent's newer binding.
- A missing `baseUrl` is a supported disabled state, not an error.

## Known limitations

- TaskNexus configuration is not yet writable through the typed `botnexus config set` surface.
- Delivery is best-effort. Restart reconciliation covers current bindings and the complete diagnostic roster, not guaranteed replay of every missed per-binding lifecycle notification.
- The extension synchronizes webhook bindings and emits roster health; it does not expose an agent-callable tool.
