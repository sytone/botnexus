targetScope = 'resourceGroup'

// Logic App: Teams message --> BotNexus inbound queue (routed to an agent).
//
// Listens to a Teams channel (or chat) and, for each new human message,
// publishes a BotNexus inbound envelope onto the Service Bus INBOUND queue.
// The conversationId is set to 'Teams - {Team Name - Channel Name}' so all
// messages from the same channel land in one BotNexus conversation.
//
// Loop guard: messages authored by the Flow bot / the outbound Logic App are
// skipped, so a reply posted back into the same channel does not re-trigger
// this workflow.
//
// Auth: the Logic App's system-assigned managed identity is granted
// Azure Service Bus Data Sender on the inbound queue (least-privilege,
// queue-scoped). Namespace local auth is disabled, so MI is the only option.
// The Teams connection uses interactive OAuth and must be authorized once in
// the portal after deployment (see README).

@description('Name for the Logic App workflow.')
param logicAppName string = 'botnexus-teams-to-inbound'

@description('Azure region.')
param location string = resourceGroup().location

@description('Service Bus namespace name (in this resource group) that BotNexus uses.')
param serviceBusNamespaceName string

@description('Inbound queue name BotNexus listens on.')
param inboundQueueName string = 'botnexus-inbound'

@description('Target Team (group) id to listen to.')
param teamId string

@description('Target channel id within the Team to listen to.')
param channelId string

@description('Human-readable Team name, used to build the conversation id.')
param teamName string

@description('Human-readable channel name, used to build the conversation id.')
param channelName string

@description('Target BotNexus agent id (e.g. keel). Left blank routes to the default agent.')
param agentId string = 'keel'

// Azure Service Bus Data Sender role.
var dataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')

// conversationId = 'Teams - {Team - Channel}'
var conversationId = 'Teams - ${teamName} - ${channelName}'

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource inboundQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' existing = {
  parent: serviceBusNamespace
  name: inboundQueueName
}

resource serviceBusConnection 'Microsoft.Web/connections@2016-06-01' = {
  name: '${logicAppName}-servicebus'
  location: location
  #disable-next-line BCP187
  kind: 'V1'
  properties: {
    displayName: '${logicAppName}-servicebus'
    #disable-next-line BCP037
    parameterValueType: 'Alternative' // managed-identity auth
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', location, 'servicebus')
    }
    customParameterValues: {}
    #disable-next-line BCP037
    alternativeParameterValues: {
      namespaceEndpoint: 'sb://${serviceBusNamespaceName}.servicebus.windows.net/'
    }
  }
}

resource teamsConnection 'Microsoft.Web/connections@2016-06-01' = {
  name: '${logicAppName}-teams'
  location: location
  #disable-next-line BCP187
  kind: 'V1'
  properties: {
    displayName: '${logicAppName}-teams'
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', location, 'teams')
    }
  }
}

resource logicApp 'Microsoft.Logic/workflows@2019-05-01' = {
  name: logicAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      parameters: {
        '$connections': {
          type: 'Object'
          defaultValue: {}
        }
      }
      triggers: {
        When_a_new_channel_message_is_added: {
          type: 'ApiConnection'
          recurrence: {
            frequency: 'Minute'
            interval: 1
          }
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'teams\'][\'connectionId\']'
              }
            }
            method: 'get'
            path: '/beta/teams/@{encodeURIComponent(\'${teamId}\')}/channels/@{encodeURIComponent(\'${channelId}\')}/messages/delta'
          }
        }
      }
      actions: {
        Skip_if_from_bot: {
          type: 'If'
          runAfter: {}
          expression: {
            and: [
              {
                not: {
                  equals: [
                    '@toLower(coalesce(triggerBody()?[\'from\']?[\'application\']?[\'displayName\'], \'\'))'
                    'flow bot'
                  ]
                }
              }
              {
                equals: [
                  '@empty(coalesce(triggerBody()?[\'from\']?[\'user\']?[\'id\'], \'\'))'
                  false
                ]
              }
            ]
          }
          actions: {
            Send_to_inbound_queue: {
              type: 'ApiConnection'
              inputs: {
                host: {
                  connection: {
                    name: '@parameters(\'$connections\')[\'servicebus\'][\'connectionId\']'
                  }
                }
                method: 'post'
                path: '/@{encodeURIComponent(encodeURIComponent(\'${inboundQueueName}\'))}/messages'
                body: {
                  ContentData: '@{base64(json(concat(\'{"content":\', string(coalesce(triggerBody()?[\'body\']?[\'content\'], \'\')), \',"conversationId":"${conversationId}","agentId":"${agentId}","senderId":"\', coalesce(triggerBody()?[\'from\']?[\'user\']?[\'displayName\'], \'teams-user\'), \'","role":"user"}\')))}'
                  ContentType: 'application/json'
                }
              }
            }
          }
          else: {
            actions: {}
          }
        }
      }
      outputs: {}
    }
    parameters: {
      '$connections': {
        value: {
          servicebus: {
            connectionId: serviceBusConnection.id
            connectionName: serviceBusConnection.name
            id: subscriptionResourceId('Microsoft.Web/locations/managedApis', location, 'servicebus')
            connectionProperties: {
              authentication: {
                type: 'ManagedServiceIdentity'
              }
            }
          }
          teams: {
            connectionId: teamsConnection.id
            connectionName: teamsConnection.name
            id: subscriptionResourceId('Microsoft.Web/locations/managedApis', location, 'teams')
          }
        }
      }
    }
  }
}

// Grant the Logic App MI send rights on the inbound queue only.
resource inboundSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(inboundQueue.id, logicApp.id, 'data-sender')
  scope: inboundQueue
  properties: {
    roleDefinitionId: dataSenderRoleId
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Logic App managed identity principal id.')
output logicAppPrincipalId string = logicApp.identity.principalId

@description('Teams API connection resource id — authorize this once in the portal.')
output teamsConnectionId string = teamsConnection.id

@description('The conversation id all messages from this channel will use.')
output conversationId string = conversationId
