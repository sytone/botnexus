namespace BotNexus.TeamsProxy.Models;

/// <summary>
/// Teams routing data captured from an inbound activity and used to send the
/// BotNexus reply back to the originating Teams conversation via the Bot Connector REST API.
/// Stored in <see cref="Services.ConversationContextStore"/> indexed by <see cref="ConversationId"/>.
/// </summary>
public sealed record TeamsConversationContext(
    string ConversationId,
    string ServiceUrl,
    string? ChannelId,
    string? ActivityId,
    ChannelAccount? From,
    ChannelAccount? Recipient,
    ConversationAccount? Conversation);
