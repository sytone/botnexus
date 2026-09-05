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
| `baseUrl` | Yes to enable delivery | Base URL of the TaskNexus instance. BotNexus posts bindings to `<baseUrl>/api/botnexus/agents`. Omit it to disable all outbound calls. |
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

## Failure and recovery

- Non-success responses and network failures are logged as warnings and dropped.
- The extension has no retry queue or outbox.
- Startup reconciliation re-sends the current bindings and is the recovery mechanism after a transient TaskNexus outage.
- A missing `baseUrl` is a supported disabled state, not an error.

## Known limitations

- TaskNexus configuration is not yet writable through the typed `botnexus config set` surface.
- Delivery is best-effort between startup reconciliation passes.
- The extension synchronizes webhook bindings only; it does not expose an agent-callable tool.
