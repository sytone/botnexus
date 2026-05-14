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

### 1. Add the NuGet package

```shell
dotnet add package BotNexus.Channels.ServiceBus
```

### 2. Register the channel in your host

```csharp
// Program.cs / Startup.cs
builder.Services.AddBotNexusServiceBusChannel(builder.Configuration);
```

### 3. Configure `botnexus.yaml`

Add a `serviceBusChannel` section under `channels`:

```yaml
channels:
  serviceBusChannel:
    connectionString: "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=<policy>;SharedAccessKey=<key>"
    inboundQueueName: botnexus-inbound
    defaultReplyQueueName: botnexus-outbound
    maxConcurrentCalls: 1
    allowedSenderIds: []   # empty = all senders permitted
```

> **Tip — keep secrets out of YAML.** Use an environment variable or Azure Key Vault reference instead of embedding the connection string directly:
> ```yaml
> connectionString: "${SERVICE_BUS_CONNECTION_STRING}"
> ```

---

## Configuration reference

All options map to the `ServiceBusChannelOptions` class and can be set via `botnexus.yaml`, `appsettings.json`, or environment variables (prefix: `BOTNEXUS_SERVICEBUS__`).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ConnectionString` | `string` | *(required unless using custom factory)* | Azure Service Bus connection string using a Shared Access Policy with Listen and Send rights. Not required when a custom `IServiceBusAdapterClientFactory` is registered. |
| `InboundQueueName` | `string` | `botnexus-inbound` | Name of the queue BotNexus **listens on** for incoming messages. |
| `DefaultReplyQueueName` | `string` | `botnexus-outbound` | Name of the queue BotNexus **sends replies to** by default. Individual messages can override this via the `replyTo` envelope field or application property. |
| `MaxConcurrentCalls` | `int` | `1` | Maximum number of messages processed in parallel. Increase for higher throughput; keep at `1` if strict ordering within the inbound queue is required. |
| `AllowedSenderIds` | `string[]` | `[]` *(empty — all allowed)* | Optional allow-list of sender identifiers. When non-empty, messages whose `senderId` is not in this list are abandoned without processing. |

---

## Managed identity / custom credentials

When running on Azure (App Service, Container Apps, AKS, Azure Functions, etc.) you should prefer **managed identity** over a connection string.

Register a custom implementation of `IServiceBusAdapterClientFactory` **before** calling `AddBotNexusServiceBusChannel`:

```csharp
// Register a custom factory that uses DefaultAzureCredential
builder.Services.AddSingleton<IServiceBusAdapterClientFactory>(sp =>
    new ManagedIdentityServiceBusClientFactory(
        fullyQualifiedNamespace: "myns.servicebus.windows.net",
        credential: new DefaultAzureCredential()));

builder.Services.AddBotNexusServiceBusChannel(builder.Configuration);
```

When a custom factory is registered, `ConnectionString` is ignored. The factory is responsible for creating both the `ServiceBusClient` (for sending) and the `ServiceBusProcessor` (for receiving).

A reference implementation `ManagedIdentityServiceBusClientFactory` is shipped in the `BotNexus.Channels.ServiceBus` package.

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
