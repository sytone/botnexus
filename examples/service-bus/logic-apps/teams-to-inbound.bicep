targetScope = 'resourceGroup'

// Logic App: Teams message --> BotNexus inbound queue (routed to an agent).
//
// Uses the Teams "When a new chat message is added" WEBHOOK trigger
// (chatmessagetrigger), so it fires on ANY new chat message (1:1 + group) the
// authorizing user is in -- no thread binding. Chats only; team channels are
// NOT covered by this trigger (they need a channel-bound trigger).
//
// The conversationId is derived AT RUNTIME from each chat payload:
//   conversationId = 'Teams - {chat topic OR chat id}'
// group chats usually expose a topic; 1:1 chats fall back to the chat id.
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
        // "When a new chat message is added" (chatmessagetrigger) -- fires for
        // ALL chats (1:1 + group) the authorizing user is in. No thread
        // binding, real-time webhook. Chats only (no team channels).
        When_a_new_chat_message_is_added: {
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
            path: '/beta/subscriptions/chatmessagetrigger'
          }
        }
      }
      actions: {
        // The chatmessagetrigger delivers only a Graph change-notification
        // (pointers: conversationId + messageId, encryptedContent null). It has
        // NO message body and NO sender. We fetch the full message via the
        // Teams connector's generic Graph passthrough action ('/httprequest'
        // with headers.Uri/Method/ContentType) before reading content/author.
        // NOTE: '/httprequest' is the proven passthrough op (verified against a
        // working in-tenant flow); the swagger 'GetMessageDetails' op needs a
        // designer-built dynamic body and is not code-friendly.
        // PITFALL 1: the Uri must be RESOURCE-first ('me'/'teams'/'users'),
        // then object ('chats'). A leading 'beta/' or leading 'chats/' -> 400
        // 'Allowed values: teams,me,users'. Correct form: 'me/chats/{id}/...'.
        // PITFALL 2: use the RAW conversationId ('19:...@thread.v2') exactly as
        // delivered. Do NOT encodeURIComponent(): percent-encoding ':'/'@'
        // breaks the proxy path tokenizer -> 400 'invalid resource/object'.
        Get_message_details: {
          type: 'ApiConnection'
          runAfter: {}
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'teams\'][\'connectionId\']'
              }
            }
            method: 'post'
            path: '/httprequest'
            headers: {
              Uri: 'me/chats/@{triggerBody()?[\'value\'][0]?[\'conversationId\']}/messages/@{triggerBody()?[\'value\'][0]?[\'messageId\']}'
              Method: 'GET'
              ContentType: 'application/json'
            }
          }
        }
        // Best-effort chat topic for the conversation label (group chats have a
        // topic; 1:1 chats return null -> we fall back to the chat id).
        Get_chat_details: {
          type: 'ApiConnection'
          runAfter: {
            Get_message_details: [ 'Succeeded' ]
          }
          inputs: {
            host: {
              connection: {
                name: '@parameters(\'$connections\')[\'teams\'][\'connectionId\']'
              }
            }
            method: 'post'
            path: '/httprequest'
            headers: {
              Uri: 'me/chats/@{triggerBody()?[\'value\'][0]?[\'conversationId\']}'
              Method: 'GET'
              ContentType: 'application/json'
            }
          }
        }
        Set_conversation_label: {
          type: 'Compose'
          runAfter: {
            Get_chat_details: [ 'Succeeded' ]
          }
          inputs: '@coalesce(body(\'Get_chat_details\')?[\'topic\'], triggerBody()?[\'value\'][0]?[\'conversationId\'], \'Chat\')'
        }
        Skip_if_from_bot: {
          type: 'If'
          runAfter: {
            Set_conversation_label: [ 'Succeeded' ]
          }
          expression: {
            and: [
              // Must have a human author on the FETCHED message.
              {
                equals: [
                  '@empty(coalesce(body(\'Get_message_details\')?[\'from\']?[\'user\']?[\'id\'], \'\'))'
                  false
                ]
              }
              // Must NOT be an application/bot author (loop guard).
              {
                equals: [
                  '@empty(coalesce(body(\'Get_message_details\')?[\'from\']?[\'application\']?[\'id\'], \'\'))'
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
                  // conversationId = 'Teams - {chat topic or id}'
                  ContentData: '@{base64(string(json(concat(\'{"content":\', string(coalesce(body(\'Get_message_details\')?[\'body\']?[\'content\'], \'\')), \',"conversationId":"Teams - \', outputs(\'Set_conversation_label\'), \'","agentId":"${agentId}","senderId":"\', coalesce(body(\'Get_message_details\')?[\'from\']?[\'user\']?[\'displayName\'], \'teams-user\'), \'","role":"user"}\'))))}'
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
