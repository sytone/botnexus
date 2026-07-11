using './teams-to-inbound.bicep'

// Service Bus namespace BotNexus uses (must be in this resource group).
param serviceBusNamespaceName = 'botnexus-sbus'
param inboundQueueName = 'botnexus-inbound'

// The Team + channel to listen to.
param teamId = '<team-group-id>'
param channelId = '<channel-id>'

// Human-readable names -> conversationId = 'Teams - {teamName} - {channelName}'.
param teamName = 'My Team'
param channelName = 'General'

// BotNexus agent that handles these messages.
param agentId = 'keel'
