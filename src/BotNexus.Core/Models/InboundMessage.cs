namespace BotNexus.Core.Models;

/// <summary>Represents a message received from a channel.</summary>
public record InboundMessage(
    string Channel,
    string SenderId,
    string ChatId,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> Media,
    IReadOnlyDictionary<string, object> Metadata,
    string? SessionKeyOverride = null)
{
    /// <summary>Unique key identifying the session this message belongs to.</summary>
    public string SessionKey => SessionKeyOverride ?? $"{Channel}:{ChatId}";
}
