using System.Text.Json.Serialization;

namespace BotNexus.Core.Models;

/// <summary>Types of system activity events.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityEventType
{
    /// <summary>A message was received from a channel.</summary>
    MessageReceived,

    /// <summary>A response was sent to a channel.</summary>
    ResponseSent,

    /// <summary>A streaming delta was sent to a channel.</summary>
    DeltaSent,

    /// <summary>An agent started processing a message.</summary>
    AgentProcessing,

    /// <summary>An agent finished processing a message.</summary>
    AgentCompleted,

    /// <summary>An error occurred during processing.</summary>
    Error,

    /// <summary>A system-level notification or message.</summary>
    SystemMessage
}

/// <summary>Represents a system-wide activity event broadcast to all subscribers.</summary>
public record ActivityEvent(
    ActivityEventType EventType,
    string Channel,
    string SessionKey,
    string? ChatId,
    string? SenderId,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object>? Metadata = null)
{
    /// <summary>Unique identifier for this event.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
