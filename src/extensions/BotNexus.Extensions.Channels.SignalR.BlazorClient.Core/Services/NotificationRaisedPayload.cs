using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// A notification pushed over SignalR.
/// </summary>
/// <remarks>
/// Mirrors the REST shape field for field, including kind and severity as NAMES, so a client parses
/// one shape whichever way a notification reached it.
/// </remarks>
public sealed class NotificationRaisedPayload
{
    /// <summary>Stable identifier.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>What the notification is about.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

    /// <summary>How much attention it wants.</summary>
    [JsonPropertyName("severity")] public string Severity { get; set; } = string.Empty;

    /// <summary>One-line summary.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    /// <summary>Optional detail.</summary>
    [JsonPropertyName("body")] public string? Body { get; set; }

    /// <summary>Agent concerned, when any.</summary>
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }

    /// <summary>Conversation concerned, when any.</summary>
    [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }

    /// <summary>Where to go to act on it.</summary>
    [JsonPropertyName("link")] public string? Link { get; set; }

    /// <summary>When it was raised.</summary>
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Projects the push onto the shape the notification list renders.</summary>
    public NotificationDto ToDto() => new()
    {
        Id = Id,
        Kind = Kind,
        Severity = Severity,
        Title = Title,
        Body = Body,
        AgentId = AgentId,
        ConversationId = ConversationId,
        Link = Link,
        CreatedAtUtc = CreatedAtUtc,
        ReadAtUtc = null,
    };
}
