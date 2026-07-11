targetScope = 'resourceGroup'

// Logic App: Teams message --> BotNexus inbound queue (routed to an agent).
//
// Uses the Teams "When a new message is added to a chat or channel" WEBHOOK
// trigger (ApiConnectionWebhook / newmessagetrigger), so it fires on ANY new
// message across chats and channels the authorizing user can see -- it is NOT
// bound to a single team/channel.
//
// Because the trigger is global, the conversationId is derived AT RUNTIME from
// each message payload:
//   conversationId = 'Teams - {chat topic OR team name - channel name}'
// falling back to the raw conversation/thread id when a friendly name is not
// present on the payload.
//
// Loop guard: messages authored by the Flow bot / an application (no human
// from.user.id) are skipped, so a reply posted back into a chat/channel does
// not re-trigger this workflow.
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

@description('Target BotNexus agent id (e.g. keel). Left blank routes to the default agent.')
param agentId string = 'keel'

// Azure Service Bus Data Sender role.
var dataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')

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
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', location, 'servicebus')
    }
    #disable-next-line BCP089
    parameterValueSet: {
      name: 'managedIdentityAuth'
      values: {
        namespaceEndpoint: {
          value: 'sb://${serviceBusNamespaceName}.servicebus.windows.net/'
        }
      }
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
        // "When a new message is added to a chat or channel" (any thread type).
        When_a_new_message_is_added: {
          type: 'ApiConnectionWebhook'
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'teams\'][\'connectionId\']'
              }
            }
            body: {
              notificationUrl: '@{listCallbackUrl()}'
            }
            path: '/beta/subscriptions/newmessagetrigger/threadType/@{encodeURIComponent(\'\')}'
          }
        }
      }
      actions: {
        // Derive a friendly conversationId from whatever the payload carries.
        // Channel messages tend to expose channelIdentity + team/channel names;
        // chats tend to expose a topic. Fall back to the raw conversation id.
        Set_conversation_label: {
          type: 'Compose'
          runAfter: {}
          inputs: '@coalesce(triggerBody()?[\'channelIdentity\']?[\'channelDisplayName\'], triggerBody()?[\'channelData\']?[\'channel\']?[\'name\'], triggerBody()?[\'topic\'], triggerBody()?[\'subject\'], triggerBody()?[\'chatId\'], triggerBody()?[\'conversationId\'], triggerBody()?[\'channelIdentity\']?[\'channelId\'], \'Unknown\')'
        }
        Set_team_label: {
          type: 'Compose'
          runAfter: {
            Set_conversation_label: [ 'Succeeded' ]
          }
          inputs: '@coalesce(triggerBody()?[\'teamDisplayName\'], triggerBody()?[\'channelData\']?[\'team\']?[\'name\'], \'\')'
        }
        Skip_if_from_bot: {
          type: 'If'
          runAfter: {
            Set_team_label: [ 'Succeeded' ]
          }
          expression: {
            and: [
              // Must have a human author.
              {
                equals: [
                  '@empty(coalesce(triggerBody()?[\'from\']?[\'user\']?[\'id\'], \'\'))'
                  false
                ]
              }
              // Must NOT be an application/bot author (loop guard).
              {
                equals: [
                  '@empty(coalesce(triggerBody()?[\'from\']?[\'application\']?[\'id\'], \'\'))'
                  true
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
                  // conversationId = 'Teams - {team - }conversationLabel'
                  ContentData: '@{base64(string(json(concat(\'{"content":\', string(coalesce(triggerBody()?[\'body\']?[\'content\'], triggerBody()?[\'content\'], \'\')), \',"conversationId":"Teams - \', if(empty(outputs(\'Set_team_label\')), \'\', concat(outputs(\'Set_team_label\'), \' - \')), outputs(\'Set_conversation_label\'), \'","agentId":"${agentId}","senderId":"\', coalesce(triggerBody()?[\'from\']?[\'user\']?[\'displayName\'], \'teams-user\'), \'","role":"user"}\'))))}'
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
