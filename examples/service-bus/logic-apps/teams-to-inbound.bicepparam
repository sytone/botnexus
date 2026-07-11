using './teams-to-inbound.bicep'

// Service Bus namespace BotNexus uses (must be in this resource group).
param serviceBusNamespaceName = 'botnexus-sbus'
param inboundQueueName = 'botnexus-inbound'

// BotNexus agent that handles these messages.
param agentId = 'keel'

// NOTE: the "new message added to a chat or channel" webhook trigger is global
// (any chat/channel the authorizing user can see) -- there are no team/channel
// binding params. conversationId is derived at runtime from each message:
//   'Teams - {team - channel}'  (channel messages)
//   'Teams - {chat topic/id}'   (chats)
