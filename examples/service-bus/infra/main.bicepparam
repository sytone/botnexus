using './main.bicep'

// Globally-unique namespace name. Change this to something unique to your tenant.
param namespaceName = 'botnexus-sbus'

// Optional: object (principal) ID of the managed identity / service principal
// that runs BotNexus. When set, the deployment grants least-privilege
// Data Receiver (inbound) + Data Sender (outbound) roles automatically.
// Leave empty ('') to assign roles manually after deployment.
param botNexusPrincipalId = ''

// Queue names — must match your BotNexus channel config.
param inboundQueueName = 'botnexus-inbound'
param outboundQueueName = 'botnexus-outbound'
