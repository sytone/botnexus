# Teams ↔ BotNexus Logic Apps (Service Bus channel)

Two Azure Logic Apps that bridge Microsoft Teams and a BotNexus agent through
the [Service Bus channel](../../../docs/user-guide/channels/service-bus.md).
Deploy them into the **same resource group** as the Service Bus namespace from
the [parent example](../README.md).

| Template | Direction | What it does |
|----------|-----------|--------------|
| [`outbound-to-teams.bicep`](./outbound-to-teams.bicep) | BotNexus → Teams | Listens on the **outbound** queue and posts each agent reply into Teams (as the Flow bot) — a 1:1 personal chat by default, or a channel. |
| [`teams-to-inbound.bicep`](./teams-to-inbound.bicep) | Teams → BotNexus | Uses the **"When a new chat message is added"** webhook trigger (`chatmessagetrigger`) — fires on any new chat message (1:1 + group) the authorizing user is in. **Chats only** (team channels need a channel-bound trigger). Publishes each new human message onto the **inbound** queue, with `conversationId` derived at runtime as `'Teams - {chat topic or id}'`. |

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

The inbound app skips messages authored by an application/bot (no human
`from.user.id`, or a non-empty `from.application.id`). Without this, a reply that
the outbound app posts back into a chat/channel would re-trigger the inbound app
→ infinite loop. Because the inbound trigger is **global**, this bot-author
filter is the primary loop protection: the Flow-bot reply from the outbound app
is authored by an application and is skipped.

### Run history: `Cancelled` vs `Succeeded`

The inbound app's forwarding is gated by a single `If` conditional (human
author + not a bot + operator mention present). When that condition is **false**
— nothing is pushed to the queue — the flow ends with a `Terminate` action set
to `runStatus: Cancelled`. This makes the run history self-documenting:

- **Succeeded** = a message was forwarded to the inbound queue.
- **Cancelled** = the message was filtered out (bot/app author, no human author,
  or the operator mention was absent) and nothing was sent.

> **Convention:** whenever a conditional is the *last* step in a Logic App and
> its false branch sends nothing, terminate that branch as `Cancelled` rather
> than letting the run report `Succeeded` with no effect. It keeps the run
> history an accurate ledger of which flows actually delivered.

## Prerequisites

- The parent [`service-bus`](../README.md) namespace + queues already deployed.
- The inbound app's `chatmessagetrigger` webhook is **global across chats** —
  no Team/channel ids needed. It fires for any 1:1 or group **chat** message
  visible to the user who authorizes the Teams connection (scope is controlled
  by *whose* account authorizes). It does **not** cover team channel posts —
  add a separate channel-bound trigger app if you need those.
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
| `agentId` | `keel` | Target BotNexus agent. |

> The `chatmessagetrigger` webhook is global across chats (any 1:1 or group
> chat the authorizing user is in); there are no team/channel binding params,
> and team channel posts are not covered. `conversationId` is derived at
> runtime from each chat payload as `'Teams - {topic or chat id}'`. Group
> chats usually expose a `topic`; 1:1 chats fall back to the chat id —
> validate against the first live message and tune the `Set_conversation_label`
> `coalesce` expression if the label comes through as a raw id.

## See also

- [Service Bus channel configuration](../../../docs/user-guide/channels/service-bus.md)
- [Service Bus envelope reference](../../../docs/user-guide/channels/service-bus-envelope.md) — the exact JSON these apps produce/consume.
