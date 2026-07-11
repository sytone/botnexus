# Teams ↔ BotNexus Logic Apps (Service Bus channel)

Two Azure Logic Apps that bridge Microsoft Teams and a BotNexus agent through
the [Service Bus channel](../../../docs/user-guide/channels/service-bus.md).
Deploy them into the **same resource group** as the Service Bus namespace from
the [parent example](../README.md).

| Template | Direction | What it does |
|----------|-----------|--------------|
| [`outbound-to-teams.bicep`](./outbound-to-teams.bicep) | BotNexus → Teams | Listens on the **outbound** queue and posts each agent reply into Teams (as the Flow bot) — a 1:1 personal chat by default, or a channel. |
| [`teams-to-inbound.bicep`](./teams-to-inbound.bicep) | Teams → BotNexus | Listens to a Teams channel and publishes each new human message onto the **inbound** queue, with `conversationId = 'Teams - {Team Name - Channel Name}'`. |

## Security model

- **Service Bus: managed identity only.** Each Logic App gets a system-assigned
  managed identity, granted a single queue-scoped role — Data Receiver on the
  outbound queue (outbound app) / Data Sender on the inbound queue (inbound
  app). The namespace has `disableLocalAuth: true`, so no SAS keys exist. No
  secrets in either template.
- **Teams: interactive OAuth.** The Teams API connection **must be authorized
  once in the portal** as you, after deployment. Bicep cannot perform the OAuth
  consent — the connection deploys `Unauthenticated` and stays inert until you
  click **Authorize**.

## Loop protection

The inbound app skips messages authored by the Flow bot (and messages with no
human `from.user.id`). Without this, a reply that the outbound app posts back
into the same channel would re-trigger the inbound app → infinite loop. **Keep
the outbound target and the inbound listen-scope distinct** (e.g. outbound to a
personal chat, inbound from a channel) for a clean, loop-free topology.

## Prerequisites

- The parent [`service-bus`](../README.md) namespace + queues already deployed.
- For the inbound app: the **Team (group) id** and **channel id** to watch.
  - `az rest --method get --url "https://graph.microsoft.com/v1.0/me/joinedTeams" --query "value[].{name:displayName,id:id}"`
  - `az rest --method get --url "https://graph.microsoft.com/v1.0/teams/<teamId>/channels" --query "value[].{name:displayName,id:id}"`
- For the outbound app (Chat mode): the recipient UPN or AAD object id.

## Deploy

Outbound (BotNexus → Teams personal chat):

```bash
az deployment group create \
  --resource-group <rg> \
  --template-file outbound-to-teams.bicep \
  --parameters outbound-to-teams.bicepparam
```

Inbound (Teams channel → BotNexus / keel):

```bash
az deployment group create \
  --resource-group <rg> \
  --template-file teams-to-inbound.bicep \
  --parameters teams-to-inbound.bicepparam
```

## Post-deploy: authorize the Teams connections (required, one-time)

For **each** Logic App, open its Teams API connection in the portal and click
**Authorize**, then **Save**:

```
Portal → Resource groups → <rg> → <logicAppName>-teams (API Connection)
        → Edit API connection → Authorize → sign in as yourself → Save
```

Until this is done the workflows will fail at the Teams step. The Service Bus
connections need no interaction — they use managed identity.

## Parameter reference

### `outbound-to-teams.bicep`

| Param | Default | Notes |
|-------|---------|-------|
| `serviceBusNamespaceName` | — | Namespace in this RG (e.g. `botnexus-sbus`). |
| `outboundQueueName` | `botnexus-outbound` | Queue BotNexus writes replies to. |
| `postTarget` | `Chat` | `Chat` (1:1 personal chat) or `Channel`. |
| `recipientUser` | `''` | UPN/object id — required for `Chat`. |
| `teamId` / `channelId` | `''` | Required for `Channel`. |

### `teams-to-inbound.bicep`

| Param | Default | Notes |
|-------|---------|-------|
| `serviceBusNamespaceName` | — | Namespace in this RG. |
| `inboundQueueName` | `botnexus-inbound` | Queue BotNexus listens on. |
| `teamId` / `channelId` | — | The Team + channel to watch. |
| `teamName` / `channelName` | — | Build `conversationId = 'Teams - {teamName} - {channelName}'`. |
| `agentId` | `keel` | Target BotNexus agent. |

## See also

- [Service Bus channel configuration](../../../docs/user-guide/channels/service-bus.md)
- [Service Bus envelope reference](../../../docs/user-guide/channels/service-bus-envelope.md) — the exact JSON these apps produce/consume.
