namespace BotNexus.Core.Models;

/// <summary>
/// Represents a system-level message broadcast to all connected clients.
/// Used for platform events like authentication requirements, provider status changes, etc.
/// </summary>
public record SystemMessage(
    string Type,
    string Title,
    string Content,
    Dictionary<string, string>? Data = null,
    DateTimeOffset? Timestamp = null)
{
    /// <summary>The timestamp when this message was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; } = Timestamp ?? DateTimeOffset.UtcNow;

    /// <summary>Unique identifier for this message.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
