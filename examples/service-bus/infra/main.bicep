targetScope = 'resourceGroup'

// Generic Azure Service Bus deployment for the BotNexus Service Bus channel.
//
// Provisions ONLY the messaging infrastructure the channel needs:
//   - A Standard-tier Service Bus namespace with local (SAS) auth DISABLED.
//   - An inbound queue BotNexus listens on and an outbound/reply queue.
//   - Least-privilege RBAC role assignments scoped to the individual queues
//     (not the whole namespace) for the identity that runs BotNexus.
//
// Authentication is managed-identity only. No SAS keys are ever created or
// used: `disableLocalAuth: true` makes the namespace reject connection-string
// auth outright. BotNexus authenticates with DefaultAzureCredential against
// `<namespace>.servicebus.windows.net`.

@minLength(6)
@maxLength(50)
@description('Globally-unique Service Bus namespace name (lowercase letters, numbers, and hyphens).')
param namespaceName string

@description('Azure region for the namespace.')
param location string = resourceGroup().location

@description('Inbound queue name — BotNexus listens on this queue for incoming messages.')
param inboundQueueName string = 'botnexus-inbound'

@description('Outbound/reply queue name — BotNexus sends agent replies here by default.')
param outboundQueueName string = 'botnexus-outbound'

@description('Object (principal) ID of the managed identity or service principal that runs BotNexus. This principal is granted Data Receiver on the inbound queue and Data Sender on the outbound queue. Leave empty to skip role assignment (assign manually later).')
param botNexusPrincipalId string = ''

@allowed([
  'ServicePrincipal'
  'User'
  'Group'
])
@description('Principal type for the role assignments. Use ServicePrincipal for managed identities and app registrations.')
param principalType string = 'ServicePrincipal'

// Built-in Azure Service Bus data-plane roles.
// Azure Service Bus Data Receiver — receive/peek/complete messages.
var dataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
// Azure Service Bus Data Sender — send messages.
var dataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')

var assignRoles = !empty(botNexusPrincipalId)

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    // Managed-identity only. Reject SAS / connection-string auth entirely.
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource inboundQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: inboundQueueName
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    deadLetteringOnMessageExpiration: true
  }
}

resource outboundQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: outboundQueueName
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    deadLetteringOnMessageExpiration: true
  }
}

// BotNexus RECEIVES from the inbound queue.
resource inboundReceiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRoles) {
  name: guid(inboundQueue.id, botNexusPrincipalId, 'data-receiver')
  scope: inboundQueue
  properties: {
    roleDefinitionId: dataReceiverRoleId
    principalId: botNexusPrincipalId
    principalType: principalType
  }
}

// BotNexus SENDS to the outbound/reply queue.
resource outboundSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRoles) {
  name: guid(outboundQueue.id, botNexusPrincipalId, 'data-sender')
  scope: outboundQueue
  properties: {
    roleDefinitionId: dataSenderRoleId
    principalId: botNexusPrincipalId
    principalType: principalType
  }
}

@description('Fully-qualified namespace to place in BotNexus config as fullyQualifiedNamespace.')
output fullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'

@description('Inbound queue name for InboundQueueName.')
output inboundQueueName string = inboundQueue.name

@description('Outbound queue name for DefaultReplyQueueName.')
output outboundQueueName string = outboundQueue.name
