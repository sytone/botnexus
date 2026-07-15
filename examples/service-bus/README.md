# BotNexus Service Bus Channel — Azure Deployment

A minimal, secure Azure Service Bus deployment for the BotNexus
[Service Bus channel](../../docs/user-guide/channels/service-bus.md). It
provisions **only** the messaging infrastructure the channel needs and wires it
for **managed-identity authentication** — no SAS keys, no connection strings.

Unlike the [`teams-proxy`](../teams-proxy/) example (which bundles an App
Service, Azure Bot, and Teams channel), this template is a standalone Service
Bus namespace you can point any BotNexus gateway at.

## What gets deployed

| Resource | Purpose |
|----------|---------|
| Service Bus namespace (Standard) | Message broker. **Local/SAS auth is disabled** (`disableLocalAuth: true`) — only Entra ID (managed identity) auth is accepted. TLS 1.2 minimum. |
| `botnexus-inbound` queue | BotNexus listens here for incoming messages. Duplicate detection on, dead-lettering on expiration. |
| `botnexus-outbound` queue | BotNexus sends agent replies here by default. |
| Role assignment: **Data Receiver** on inbound | Least-privilege — scoped to the inbound queue only. |
| Role assignment: **Data Sender** on outbound | Least-privilege — scoped to the outbound queue only. |

> Roles are scoped to the individual **queues**, not the whole namespace, so the
> BotNexus identity can receive from inbound and send to outbound — and nothing
> else.

## Why managed identity only

Connection strings (SAS keys) are long-lived shared secrets that leak through
config files, logs, and env dumps, and must be rotated manually. This template
sets `disableLocalAuth: true`, so the namespace **rejects** connection-string
auth outright. BotNexus authenticates with
[`DefaultAzureCredential`](https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential),
which resolves the App Service / Container Apps / AKS / VM managed identity (or
your `az login` / Visual Studio identity during local development) with no
secret material on disk.

## Prerequisites

- Azure CLI (`az`) 2.50+ with the Bicep tooling (`az bicep install`).
- An existing resource group.
- The **object (principal) ID** of the identity that runs BotNexus:
  - Managed identity: `az identity show -g <rg> -n <name> --query principalId -o tsv`
  - App registration / SP: `az ad sp show --id <appId> --query id -o tsv`

## Deploy

1. Edit [`infra/main.bicepparam`](./infra/main.bicepparam):
   - Set `namespaceName` to a globally-unique value.
   - Set `botNexusPrincipalId` to the principal ID above (or leave empty to
     assign roles manually later).

2. Deploy into your resource group:

   ```bash
   az deployment group create \
     --resource-group <your-rg> \
     --parameters infra/main.bicepparam
   ```

3. Note the outputs — you'll need `fullyQualifiedNamespace` for the BotNexus
   config:

   ```bash
   az deployment group show \
     --resource-group <your-rg> \
     --name main \
     --query properties.outputs
   ```

### Assigning roles manually (if you left `botNexusPrincipalId` empty)

```bash
NS_ID=$(az servicebus namespace show -g <rg> -n <namespace> --query id -o tsv)

# Data Receiver on the inbound queue
az role assignment create \
  --assignee <principalId> \
  --role "Azure Service Bus Data Receiver" \
  --scope "$NS_ID/queues/botnexus-inbound"

# Data Sender on the outbound queue
az role assignment create \
  --assignee <principalId> \
  --role "Azure Service Bus Data Sender" \
  --scope "$NS_ID/queues/botnexus-outbound"
```

## Configure BotNexus (secure)

Because local auth is disabled, **do not** set a `connectionString`. Configure
the channel with the fully-qualified namespace and let managed identity handle
auth. In `~/.botnexus/config.json`:

```json
{
  "channels": {
    "servicebus": {
      "fullyQualifiedNamespace": "<namespace>.servicebus.windows.net",
      "inboundQueueName": "botnexus-inbound",
      "defaultReplyQueueName": "botnexus-outbound",
      "maxConcurrentCalls": 1,
      "allowedSenderIds": []
    }
  }
}
```

Notes:

- **No secrets in config.** With `fullyQualifiedNamespace` set and
  `connectionString` omitted, BotNexus uses `DefaultAzureCredential`.
- **Restrict senders.** Populate `allowedSenderIds` with the identifiers you
  expect on the wire to reject unknown callers at the channel boundary.
- **Local development.** `az login` (or a Visual Studio / VS Code Azure sign-in)
  provides a `DefaultAzureCredential` identity. Grant your dev user the same two
  queue-scoped roles to test end-to-end without a deployed identity.

## See also

- [Service Bus channel configuration](../../docs/user-guide/channels/service-bus.md) — full options reference and capability flags.
- [Service Bus envelope reference](../../docs/user-guide/channels/service-bus-envelope.md) — JSON message schema for clients.
- [Azure Service Bus authentication with Entra ID](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-authentication-and-authorization).

