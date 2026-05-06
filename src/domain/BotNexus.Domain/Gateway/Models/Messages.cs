using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A message received from a channel adapter, ready for routing to an agent.
/// </summary>
public sealed record InboundMessage
{
    /// <summary>The channel this message arrived from (e.g., "signalr", "telegram").</summary>
    public required ChannelKey ChannelType { get; init; }

    /// <summary>Identifier of the sender within the channel.</summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Conversation identifier within the channel (e.g., chat ID, thread ID).
    /// Combined with <see cref="ChannelType"/> to derive a session key.
    /// </summary>
    public required string ChannelAddress { get; init; }

    /// <summary>The message text content.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional multi-modal content parts. When present, the message includes
    /// non-text content (audio, images, etc.) alongside or instead of <see cref="Content"/>.
    /// </summary>
    public IReadOnlyList<MessageContentPart>? ContentParts { get; init; }

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

    /// <summary>
    /// Native thread or topic identifier within the channel address.
    /// For Telegram group topics, Signal threads, Teams threads, etc.
    /// When non-null, used to route into a thread-specific conversation binding.
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// The channel binding ID this message arrived on, if known.
    /// Used by fan-out to exclude the originating binding from echo.
    /// </summary>
    public BindingId? BindingId { get; init; }

    /// <summary>
    /// When set, routes directly to this conversation, bypassing binding lookup.
    /// Used by portal clients that know which conversation they are viewing.
    /// This avoids the thread-binding hack that caused duplicate bindings and double fan-out.
    /// </summary>
    public string? ConversationId { get; init; }
}

/// <summary>
/// A message to send back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>The channel to send through.</summary>
    public required ChannelKey ChannelType { get; init; }

    /// <summary>Target conversation identifier within the channel.</summary>
    public required string ChannelAddress { get; init; }

    /// <summary>The message content.</summary>
    public required string Content { get; init; }

    /// <summary>The session this message belongs to.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Native thread or topic identifier for the target channel.
    /// When non-null, adapters should deliver the message into the specified thread
    /// (e.g. Telegram <c>message_thread_id</c>, Teams thread id).
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// Stable identifier of the channel binding that this message is being sent to.
    /// Populated during fan-out to allow adapters and logging to correlate delivery.
    /// </summary>
    public BindingId? BindingId { get; init; }

    /// <summary>
    /// Optional display prefix prepended to outbound content when
    /// <see cref="BotNexus.Gateway.Abstractions.Models.ThreadingMode.Prefix"/> is in use.
    /// Null when no prefix is needed.
    /// </summary>
    public string? DisplayPrefix { get; init; }

    /// <summary>Extensible metadata for the channel adapter.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
}
