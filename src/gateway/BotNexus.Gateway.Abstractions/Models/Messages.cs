namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A message received from a channel adapter, ready for routing to an agent.
/// </summary>
public sealed record InboundMessage
{
    /// <summary>The channel this message arrived from (e.g., "websocket", "telegram").</summary>
    public required string ChannelType { get; init; }

    /// <summary>Identifier of the sender within the channel.</summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Conversation identifier within the channel (e.g., chat ID, thread ID).
    /// Combined with <see cref="ChannelType"/> to derive a session key.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>The message text content.</summary>
    public required string Content { get; init; }

    /// <summary>When the message was received.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional explicit agent target. If set, bypasses default routing.
    /// </summary>
    public string? TargetAgentId { get; init; }

    /// <summary>
    /// Optional explicit session ID. If set, resumes an existing session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>Extensible metadata from the channel adapter.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
}

/// <summary>
/// A message to send back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>The channel to send through.</summary>
    public required string ChannelType { get; init; }

    /// <summary>Target conversation identifier within the channel.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The message content.</summary>
    public required string Content { get; init; }

    /// <summary>The session this message belongs to.</summary>
    public string? SessionId { get; init; }

    /// <summary>Extensible metadata for the channel adapter.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
}
