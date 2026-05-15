# BotNexus Teams Proxy

> Location: `examples/teams-proxy`  
> This project lives with other runnable examples under `examples/`.

Queue-backed Azure Bot bridge for Teams. Azure Bot posts Teams activities to `/api/messages`; this service validates the Bot Connector token, publishes a normalized message to Azure Service Bus, then reads BotNexus responses from an outbound queue and sends them back to the Teams conversation through the Bot Connector REST API.

## Flow

1. Teams user messages the Azure Bot.
2. Azure Bot Service sends a Bot Connector `message` activity to `POST /api/messages`.
3. The proxy validates the bearer token and publishes a `ServiceBusInboundEnvelope` on `botnexus-inbound`. Teams routing data (`serviceUrl`, `activityId`, `from`, `recipient`) is also saved in an in-memory `ConversationContextStore` keyed by `conversationId`.
4. BotNexus reads the inbound queue, routes the message to the configured agent, and writes a `ServiceBusOutboundEnvelope` to `botnexus-outbound`.
5. The proxy reads the outbound queue, looks up Teams routing context by `conversationId`, and posts the response to `{serviceUrl}/v3/conversations/{conversationId}/activities/{replyToActivityId}`.

Only asynchronous `message` activities are supported in v1. Teams `invoke` activities require a synchronous HTTP response and are rejected with `501`.

## Configuration

Settings are under the `TeamsProxy` configuration section.

| Setting | Purpose |
| --- | --- |
| `AgentId` | Target BotNexus agent identifier sent in the inbound envelope `agentId` field. Leave empty to use BotNexus default agent routing. |
| `BotClientId` | Client ID of the user-assigned managed identity configured on the Azure Bot. |
| `ManagedIdentityClientId` | Client ID used by the App Service to access Service Bus and Bot Connector. |
| `ServiceBusFullyQualifiedNamespace` | Service Bus namespace host, for example `myservicebus.servicebus.windows.net`. |
| `InboundQueueName` | Queue for Teams messages to BotNexus. Sent as `replyTo` in the inbound envelope. |
| `OutboundQueueName` | Queue for BotNexus replies to Teams. |
| `AllowedServiceUrlHosts` | Host allow-list for outbound Bot Connector calls. Defaults to Teams `smba.trafficmanager.net` and Azure Bot web chat `webchat.botframework.com`. |
| `SkipOutboundServiceUrlHosts` | Hosts that should not receive delayed Connector sends. Defaults to Azure Bot web chat because the portal test surface is inbound-only for this queue bridge. |
| `OutboundWorkerEnabled` | Enables the background worker that reads BotNexus replies and sends them to Teams. Disabled in local Development settings. |

`AllowUnauthenticatedRequests` is accepted only in `Development`.

## Queue contracts

### Inbound queue (proxy → BotNexus)

Uses the `ServiceBusInboundEnvelope` shape from the BotNexus Service Bus channel extension (PR #215):

```json
{
  "messageId": "activity-id",
  "correlationId": "generated-guid",
  "agentId": "configured-agent-id",
  "conversationId": "teams-conversation-id",
  "sessionId": null,
  "senderId": "teams-user-id",
  "role": "user",
  "content": "hello",
  "replyTo": "botnexus-outbound",
  "timestamp": "2026-05-12T20:00:00Z",
  "metadata": {
    "teams.serviceUrl": "https://smba.trafficmanager.net/amer/",
    "teams.activityId": "activity-id",
    "teams.channelId": "msteams",
    "teams.tenantId": "tenant-id",
    "teams.from.id": "user-aad-id",
    "teams.from.name": "User Name",
    "teams.recipient.id": "bot-id",
    "teams.recipient.name": "BotNexus"
  }
}
```

### Outbound queue (BotNexus → proxy)

Uses the `ServiceBusOutboundEnvelope` shape written by the BotNexus Service Bus channel adapter:

```json
{
  "messageId": "botnexus-generated-id",
  "correlationId": "original-correlation-id",
  "agentId": "configured-agent-id",
  "conversationId": "teams-conversation-id",
  "sessionId": "botnexus-session-id",
  "role": "assistant",
  "content": "Agent response text",
  "timestamp": "2026-05-12T20:00:10Z",
  "metadata": {}
}
```

The proxy uses `conversationId` to look up Teams routing data from its in-memory `ConversationContextStore`. The store is populated on each inbound publish. If the proxy restarts between inbound and outbound processing, the context will be missing and the outbound message is dead-lettered with reason `MissingConversationContext`.

## Local queue agent

Use `scripts\Invoke-BotNexusQueueAgent.ps1` to manually simulate BotNexus — it receives inbound envelopes, calls Copilot CLI, and publishes outbound envelopes. This lets you validate the full roundtrip without a running BotNexus gateway:

```powershell
.\scripts\Invoke-BotNexusQueueAgent.ps1 -WhatIf
.\scripts\Invoke-BotNexusQueueAgent.ps1 -Verbose
.\scripts\Invoke-BotNexusQueueAgent.ps1 -Wait -Continuous -MaxMessages 100 -Verbose
```

The script reads the new inbound envelope (`content`, `messageId`, `conversationId`), calls `agency copilot --yolo -p "<prompt>"`, and publishes the new outbound envelope (`role: assistant`, `content`, `correlationId`, `conversationId`). The proxy outbound worker then reads this and sends the reply to Teams.

Azure Bot **Test in Web Chat** is useful to validate inbound queueing, but it is configured as inbound-only for this bridge. Use the Teams app package to validate the full outbound Connector roundtrip.

## Infrastructure

`infra/main.bicep` creates:

- User-assigned managed identity
- Azure Service Bus namespace with inbound/outbound queues
- Linux App Service configured with the managed identity
- Azure Bot resource using `UserAssignedMSI`
- Microsoft Teams channel for the bot
- Service Bus sender/receiver role assignments for the App Service identity

Deploy:

```powershell
az deployment group create `
  --resource-group <resource-group> `
  --template-file .\infra\main.bicep `
  --parameters baseName=<unique-lowercase-name>
```

Use only lowercase letters and numbers for `baseName`. The template appends suffixes for globally named resources.

After deployment, publish the app and install the Teams app package from `teams-app/`.
