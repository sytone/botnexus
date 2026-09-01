# Azure Service Bus Channel

The Azure Service Bus channel adapter lets BotNexus receive messages from — and send replies to — Azure Service Bus queues. It is designed for server-to-server and system-integration scenarios where a human or automated process needs to interact with a BotNexus agent over a durable, reliable message queue rather than a real-time channel such as SignalR or Telegram.

## When to use this channel

| Scenario | Fit |
|----------|-----|
| Backend service or Azure Function sends a task to BotNexus and needs the agent reply asynchronously | ✅ Ideal |
| Decoupled microservice architecture where ordering and at-least-once delivery matters | ✅ Ideal |
| High-throughput fan-out where many concurrent requests must be processed | ✅ Good (tune `MaxConcurrentCalls`) |
| Real-time interactive chat with a human end-user | ❌ Use SignalR or Telegram instead |
| Streaming token-by-token responses | ❌ Not supported (see [capabilities](#channel-capabilities)) |

---

## Prerequisites

Before enabling this channel you need:

1. **An Azure Service Bus namespace** (Standard or Premium tier — Basic tier does not support topics, but queues are sufficient for this adapter).
2. **Two queues** in that namespace:
   - An *inbound* queue that BotNexus listens on (default name: `botnexus-inbound`).
   - An *outbound/reply* queue that BotNexus sends replies to (default name: `botnexus-outbound`). This is the default; individual messages can override the reply queue via the `replyTo` envelope field.
3. A **connection string** with `Listen` + `Send` rights on both queues, **or** a managed identity / service principal with the `Azure Service Bus Data Receiver` role on the inbound queue and `Azure Service Bus Data Sender` role on the outbound queue.

---

## Enabling the extension

The Azure Service Bus channel is deployed via the BotNexus CLI. Configure it by editing your `~/.botnexus/config.json`:

```json
{
  "channels": {
    "servicebus": {
      "connectionString": "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=<policy>;SharedAccessKey=<key>",
      "inboundQueueName": "botnexus-inbound",
      "defaultReplyQueueName": "botnexus-outbound",
      "maxConcurrentCalls": 1,
      "maxAutoLockRenewalMinutes": 30,
      "allowedSenderIds": []
    }
  }
}
```

### Secure configuration: Environment variables

To keep secrets out of `config.json`, set the connection string in the environment instead. The
variable name is the configuration path with `__` between levels and **no prefix** - see
[Environment Variable Overrides](../../configuration.md#environment-variable-overrides).

On Linux / macOS:
```bash
export channels__servicebus__connectionString="Endpoint=sb://..."
```

On Windows (PowerShell):
```powershell
$env:channels__servicebus__connectionString = "Endpoint=sb://..."
```

> **Leave `connectionString` out of `config.json` entirely for this to take effect.** `config.json`
> is added to the configuration pipeline *after* the environment source, so where a key is present in
> both, the file wins and the environment variable is ignored.

Alternatively, for Azure deployments, use **managed identity** with `DefaultAzureCredential` (see [Managed identity / Azure Key Vault](#managed-identity--azure-key-vault) below).

---

## Configuration reference

All options are configured via `~/.botnexus/config.json` under the `channels.servicebus` section. An
option can instead be supplied as an environment variable named `channels__servicebus__<property>`,
which is read only when that key is absent from `config.json`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ConnectionString` | `string` | *(required unless using custom factory)* | Azure Service Bus connection string using a Shared Access Policy with Listen and Send rights. Takes precedence over `FullyQualifiedNamespace` when both are set. Not required when a custom `IServiceBusAdapterClientFactory` is registered. |
| `FullyQualifiedNamespace` | `string` | *(none)* | Fully-qualified Service Bus namespace (e.g. `myns.servicebus.windows.net`) to authenticate against with managed identity via `DefaultAzureCredential`. The recommended keyless setup; required for namespaces created with `disableLocalAuth: true`. Used only when `ConnectionString` is empty. |
| `InboundQueueName` | `string` | `botnexus-inbound` | Name of the queue BotNexus **listens on** for incoming messages. |
| `DefaultReplyQueueName` | `string` | `botnexus-outbound` | Name of the queue BotNexus **sends replies to** by default. Individual messages can override this via the `replyTo` envelope field or application property. **Must not equal `InboundQueueName`** — see [Self-send loop guard](#self-send-loop-guard). |
| `MaxConcurrentCalls` | `int` | `1` | Maximum number of messages processed in parallel. Increase for higher throughput; keep at `1` if strict ordering within the inbound queue is required. |
| `MaxAutoLockRenewalMinutes` | `int` | `30` | Maximum wall-clock duration, in minutes, for which the processor keeps automatically renewing a message lock while the agent turn is still running. The Azure SDK default is five minutes, which is shorter than many agent turns; when it lapses the completion call fails with a lock-lost error and Service Bus redelivers work that already succeeded. Valid range `0`–`1440`; set to `0` to disable automatic renewal entirely (not recommended). |
| `AllowedSenderIds` | `string[]` | `[]` *(empty — all allowed)* | Optional allow-list of **end-user** sender identifiers, compared against the envelope's `senderId`. When non-empty, messages whose `senderId` is not in this list are abandoned without processing, and the block is logged at `Warning`. This does **not** filter which service or proxy may publish — use Service Bus RBAC for that. |

---

## Self-send loop guard

The adapter never publishes a reply into the queue its own processor consumes from. Doing so would
make every reply re-enter the gateway as fresh inbound work, looping until Service Bus
`maxDeliveryCount` dead-letters the message — after the throughput and dead-letter capacity have
already been spent.

Two guards enforce this:

1. **At startup**, if `DefaultReplyQueueName` equals `InboundQueueName` (case-insensitively — Service
   Bus entity names are not case-sensitive), the adapter logs a `Warning` naming both values. It
   still starts, so a bad configuration reload cannot take a running gateway down.
2. **At send time**, any reply whose resolved reply queue is the inbound queue is refused with an
   error naming the queue and the resolution source. This covers all three resolution branches —
   outbound metadata, the per-message value carried on the inbound envelope's `replyTo` field or its
   `applicationProperties["replyTo"]` entry, and the configured default — because a per-message
   `replyTo` *overrides* the configured default and arrives from an untrusted producer.

`AllowedSenderIds` is **not** a mitigation for this: it filters end-user identities, and a hostile or
mistaken `replyTo` rides in on a message from a perfectly legitimate user.

---

## Managed identity / Azure Key Vault

When running on Azure (App Service, Container Apps, AKS, Azure Functions, etc.) you should prefer **managed identity** and **Azure Key Vault** over embedding connection strings.

To use a managed identity:

1. Assign the service principal running BotNexus the `Azure Service Bus Data Receiver` role on the inbound queue and `Azure Service Bus Data Sender` role on the outbound queue.
2. Omit or leave empty the `connectionString` field in your config — BotNexus will use `DefaultAzureCredential` to authenticate automatically.

```json
{
  "channels": {
    "servicebus": {
      "inboundQueueName": "botnexus-inbound",
      "defaultReplyQueueName": "botnexus-outbound",
      "fullyQualifiedNamespace": "myns.servicebus.windows.net"
    }
  }
}
```

Alternatively, store your connection string in **Azure Key Vault** and reference it via an environment variable:

---

## Channel capabilities

The Service Bus channel is an **asynchronous request/reply** channel. The following capability flags reflect its nature:

| Capability | Supported | Notes |
|------------|-----------|-------|
| `SupportsStreaming` | `false` | Replies are sent as a single complete message after the agent finishes. |
| `SupportsSteering` | `false` | Mid-flight steering commands are not forwarded to the agent. |
| `SupportsFollowUp` | `false` | Proactive follow-up messages are not delivered via this channel. |
| `SupportsThinkingDisplay` | `false` | Internal reasoning tokens are not forwarded to the reply queue. |
| `SupportsToolDisplay` | `false` | Tool call / result details are not included in the reply envelope. |

---

## See also

- [Service Bus envelope reference](./service-bus-envelope.md) — JSON schema, field reference, and integration examples for developers building clients.
- [Service Bus deployment example](https://github.com/Sytone/botnexus/tree/main/examples/service-bus) — generic Bicep template that provisions a namespace, queues, and managed-identity RBAC for this channel.
- [Azure Service Bus documentation](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-messaging-overview)

