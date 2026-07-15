using './teams-to-inbound.bicep'

// Service Bus namespace BotNexus uses (must be in this resource group).
param serviceBusNamespaceName = 'botnexus-sbus'
param inboundQueueName = 'botnexus-inbound'

// BotNexus agent that handles these messages. Using the sandbox 'assistant'
// agent during initial validation to avoid noise on a live agent.
param agentId = 'assistant'
// Initial-validation targeting: only forward messages that @-mention the
// operator so the existing busy chats don't flood the agent. Set to '' later
// to open up once we design proper per-channel handling.
param requiredMention = '@Jon Bullen'

// NOTE: the "new message added to a chat or channel" webhook trigger is global
// (any chat/channel the authorizing user can see) -- there are no team/channel
// binding params. conversationId is derived at runtime from each message:
//   'Teams - {team - channel}'  (channel messages)
//   'Teams - {chat topic/id}'   (chats)
