using './teams-to-inbound.bicep'

// Service Bus namespace BotNexus uses (must be in this resource group).
param serviceBusNamespaceName = 'botnexus-sbus'
param inboundQueueName = 'botnexus-inbound'

// BotNexus agent that handles these messages. Using the sandbox 'assistant'
// agent during initial validation to avoid noise on a live agent.
param agentId = 'assistant'
// Initial-validation targeting: only forward messages that @-mention the
// operator so the existing busy chats don't flood the agent.
//
// PRIMARY (robust): match the operator's AAD object id in the message's
// `mentions` array -- stable no matter how the mention renders ("Jon",
// "Jon Bullen", a nickname). This is Jon's object id
// (az ad user show --id jobullen@microsoft.com --query id -o tsv).
param operatorAadObjectId = 'c9ece486-f597-488a-8612-d9d55daeeb02'
// FALLBACK: text-substring match, used only when a message has no `mentions`
// array. Set BOTH to '' later to open up once we design per-channel handling.
param requiredMention = '@Jon Bullen'

// NOTE: the "new message added to a chat or channel" webhook trigger is global
// (any chat/channel the authorizing user can see) -- there are no team/channel
// binding params. conversationId is derived at runtime from each message:
//   'Teams - {team - channel}'  (channel messages)
//   'Teams - {chat topic/id}'   (chats)
