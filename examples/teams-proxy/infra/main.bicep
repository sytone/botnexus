targetScope = 'resourceGroup'

@minLength(3)
@maxLength(20)
@description('Short lowercase deployment name used as the base for Azure resource names. Use only lowercase letters and numbers for globally named resources.')
param baseName string

@description('Azure region for regional resources.')
param location string = resourceGroup().location

@description('Display name shown for the Azure Bot resource.')
param botDisplayName string = '${baseName}-bot'

@allowed([
  'F0'
  'S1'
])
@description('Azure Bot Service SKU.')
param botSku string = 'F0'

@description('App Service plan SKU.')
param appServiceSkuName string = 'B1'

@description('Inbound queue name. Azure Bot messages are published here for BotNexus.')
param inboundQueueName string = 'botnexus-inbound'

@description('Outbound queue name. BotNexus responses are read from here and sent to Teams.')
param outboundQueueName string = 'botnexus-outbound'

var identityName = '${baseName}-uami'
var appServicePlanName = '${baseName}-plan'
var webAppName = '${baseName}-app'
var serviceBusNamespaceName = '${baseName}bus'
var botName = '${baseName}-bot'
var serviceBusDataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var serviceBusDataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')

resource userAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
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
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: appServiceSkuName
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${userAssignedIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      appSettings: [
        {
          name: 'TeamsProxy__BotClientId'
          value: userAssignedIdentity.properties.clientId
        }
        {
          name: 'TeamsProxy__ManagedIdentityClientId'
          value: userAssignedIdentity.properties.clientId
        }
        {
          name: 'TeamsProxy__ServiceBusFullyQualifiedNamespace'
          value: '${serviceBusNamespace.name}.servicebus.windows.net'
        }
        {
          name: 'TeamsProxy__InboundQueueName'
          value: inboundQueue.name
        }
        {
          name: 'TeamsProxy__OutboundQueueName'
          value: outboundQueue.name
        }
        {
          name: 'TeamsProxy__AllowedServiceUrlHosts__0'
          value: 'smba.trafficmanager.net'
        }
        {
          name: 'TeamsProxy__AllowedServiceUrlHosts__1'
          value: 'webchat.botframework.com'
        }
        {
          name: 'TeamsProxy__SkipOutboundServiceUrlHosts__0'
          value: 'webchat.botframework.com'
        }
      ]
    }
  }
}

resource botService 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  kind: 'azurebot'
  sku: {
    name: botSku
  }
  properties: {
    displayName: botDisplayName
    endpoint: 'https://${webApp.properties.defaultHostName}/api/messages'
    msaAppId: userAssignedIdentity.properties.clientId
    msaAppMSIResourceId: userAssignedIdentity.id
    msaAppTenantId: tenant().tenantId
    msaAppType: 'UserAssignedMSI'
    publicNetworkAccess: 'Enabled'
  }
}

resource teamsChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: botService
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

resource serviceBusSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, userAssignedIdentity.id, 'sender')
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: serviceBusDataSenderRoleId
    principalId: userAssignedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource serviceBusReceiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, userAssignedIdentity.id, 'receiver')
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: serviceBusDataReceiverRoleId
    principalId: userAssignedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output botClientId string = userAssignedIdentity.properties.clientId
output messagingEndpoint string = 'https://${webApp.properties.defaultHostName}/api/messages'
output serviceBusNamespace string = serviceBusNamespace.name
output inboundQueue string = inboundQueue.name
output outboundQueue string = outboundQueue.name
