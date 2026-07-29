using './outbound-to-teams.bicep'

// Service Bus namespace BotNexus uses (must be in this resource group).
param serviceBusNamespaceName = 'botnexus-sbus'
param outboundQueueName = 'botnexus-outbound'

// Post agent replies to a 1:1 Flow-bot personal chat with this user.
param postTarget = 'Chat'
param recipientUser = 'you@example.com'

// For postTarget = 'Channel' instead, set these and clear recipientUser:
// param teamId = '<team-group-id>'
// param channelId = '<channel-id>'
