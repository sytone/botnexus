targetScope = 'resourceGroup'

// Logic App: BotNexus outbound (reply) queue --> Teams message.
//
// Listens on the Service Bus OUTBOUND queue (the queue BotNexus writes agent
// replies to) and posts each reply into Teams as the Flow bot. Default target
// is a 1:1 ("personal") chat with the recipient; can also post to a channel.
//
// Auth: the Logic App's system-assigned managed identity is granted
// Azure Service Bus Data Receiver on the outbound queue (least-privilege,
// queue-scoped). The namespace has local auth disabled, so MI is the only
// option. The Teams connection uses interactive OAuth and must be authorized
// once in the portal after deployment (see README).

@description('Name for the Logic App workflow.')
param logicAppName string = 'botnexus-outbound-to-teams'

@description('Azure region.')
param location string = resourceGroup().location

@description('Service Bus namespace name (in this resource group) that BotNexus uses.')
param serviceBusNamespaceName string

@description('Outbound/reply queue name BotNexus writes agent replies to.')
param outboundQueueName string = 'botnexus-outbound'

@allowed([
  'Chat'
  'Channel'
])
@description('Where to post the reply. Chat = 1:1 personal chat (recipient param); Channel = a Team channel (teamId/channelId params).')
param postTarget string = 'Chat'

@description('For postTarget=Chat: the recipient user (UPN or AAD object id) to receive the reply as a Flow-bot personal chat message.')
param recipientUser string = ''

@description('For postTarget=Channel: the target Team (group) id.')
param teamId string = ''

@description('For postTarget=Channel: the target channel id.')
param channelId string = ''

// Azure Service Bus Data Receiver role.
var dataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource outboundQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' existing = {
  parent: serviceBusNamespace
  name: outboundQueueName
}

// Managed-identity Service Bus API connection (no secrets; MI auth).
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

// Interactive-OAuth Teams API connection (authorize once in portal after deploy).
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
        When_a_message_is_received_in_a_queue: {
          type: 'ApiConnection'
          recurrence: {
            frequency: 'Minute'
            interval: 1
          }
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'servicebus\'][\'connectionId\']'
              }
            }
            method: 'get'
            path: '/@{encodeURIComponent(encodeURIComponent(\'${outboundQueueName}\'))}/messages/head'
            queries: {
              queueType: 'Main'
            }
          }
        }
      }
      actions: {
        Parse_reply_envelope: {
          type: 'ParseJson'
          runAfter: {}
          inputs: {
            content: '@base64ToString(triggerBody()?[\'ContentData\'])'
            schema: {
              type: 'object'
              properties: {
                content: { type: 'string' }
                conversationId: { type: 'string' }
                agentId: { type: 'string' }
                sessionId: { type: 'string' }
                correlationId: { type: 'string' }
              }
            }
          }
        }
        Post_reply_to_Teams: {
          type: 'ApiConnection'
          runAfter: {
            Parse_reply_envelope: [ 'Succeeded' ]
          }
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'teams\'][\'connectionId\']'
              }
            }
            method: 'post'
            path: postTarget == 'Channel' ? '/v1.0/teams/@{encodeURIComponent(\'${teamId}\')}/channels/@{encodeURIComponent(\'${channelId}\')}/messages' : '/beta/chatWithUser'
            body: postTarget == 'Channel' ? {
              messageBody: '@{body(\'Parse_reply_envelope\')?[\'content\']}'
            } : {
              recipient: recipientUser
              messageBody: '@{body(\'Parse_reply_envelope\')?[\'content\']}'
            }
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

// Grant the Logic App MI receive rights on the outbound queue only.
resource outboundReceiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(outboundQueue.id, logicApp.id, 'data-receiver')
  scope: outboundQueue
  properties: {
    roleDefinitionId: dataReceiverRoleId
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Logic App managed identity principal id.')
output logicAppPrincipalId string = logicApp.identity.principalId

@description('Teams API connection resource id — authorize this once in the portal.')
output teamsConnectionId string = teamsConnection.id
