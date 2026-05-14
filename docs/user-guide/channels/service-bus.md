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
    "serviceBusChannel": {
      "connectionString": "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=<policy>;SharedAccessKey=<key>",
      "inboundQueueName": "botnexus-inbound",
      "defaultReplyQueueName": "botnexus-outbound",
      "maxConcurrentCalls": 1,
      "allowedSenderIds": []
    }
  }
}
```

### Secure configuration: Environment variables

To keep secrets out of `config.json`, set the `BOTNEXUS_CHANNELS__SERVICEBUSCHANNEL__CONNECTIONSTRING` environment variable instead. BotNexus will use this value and override any connection string in your config file.

On Linux / macOS:
```bash
export BOTNEXUS_CHANNELS__SERVICEBUSCHANNEL__CONNECTIONSTRING="Endpoint=sb://..."
```

On Windows (PowerShell):
```powershell
$env:BOTNEXUS_CHANNELS__SERVICEBUSCHANNEL__CONNECTIONSTRING = "Endpoint=sb://..."
```

Alternatively, for Azure deployments, use **managed identity** with `DefaultAzureCredential` (see [Managed identity / Azure Key Vault](#managed-identity--azure-key-vault) below).

---

## Configuration reference

All options are configured via `~/.botnexus/config.json` under the `channels.serviceBusChannel` section. Options can also be overridden via environment variables with the prefix `BOTNEXUS_CHANNELS__SERVICEBUSCHANNEL__`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ConnectionString` | `string` | *(required unless using custom factory)* | Azure Service Bus connection string using a Shared Access Policy with Listen and Send rights. Not required when a custom `IServiceBusAdapterClientFactory` is registered. |
| `InboundQueueName` | `string` | `botnexus-inbound` | Name of the queue BotNexus **listens on** for incoming messages. |
| `DefaultReplyQueueName` | `string` | `botnexus-outbound` | Name of the queue BotNexus **sends replies to** by default. Individual messages can override this via the `replyTo` envelope field or application property. |
| `MaxConcurrentCalls` | `int` | `1` | Maximum number of messages processed in parallel. Increase for higher throughput; keep at `1` if strict ordering within the inbound queue is required. |
| `AllowedSenderIds` | `string[]` | `[]` *(empty — all allowed)* | Optional allow-list of sender identifiers. When non-empty, messages whose `senderId` is not in this list are abandoned without processing. |

---

## Managed identity / Azure Key Vault

When running on Azure (App Service, Container Apps, AKS, Azure Functions, etc.) you should prefer **managed identity** and **Azure Key Vault** over embedding connection strings.

To use a managed identity:

1. Assign the service principal running BotNexus the `Azure Service Bus Data Receiver` role on the inbound queue and `Azure Service Bus Data Sender` role on the outbound queue.
2. Omit or leave empty the `connectionString` field in your config — BotNexus will use `DefaultAzureCredential` to authenticate automatically.

```json
{
  "channels": {
    "serviceBusChannel": {
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
- [Azure Service Bus documentation](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-messaging-overview)
