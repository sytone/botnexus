namespace BotNexus.Core.Models;

/// <summary>Represents a message to be sent to a channel.</summary>
public record OutboundMessage(
    string Channel,
    string ChatId,
    string Content,
    IReadOnlyDictionary<string, object>? Metadata = null);
