using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A message received from a channel adapter, ready for routing to an agent.
/// </summary>
public sealed record InboundMessage
{
    /// <summary>The channel this message arrived from (e.g., "signalr", "telegram").</summary>
    public required ChannelKey ChannelType { get; init; }

    /// <summary>
    /// Channel-native wire token for the sender (connection id, user id from the
    /// underlying transport, envelope sender id, etc.). Used for logging, allow-listing,
    /// fan-out exclusion, and channel-level audit. NOT a typed domain identity —
    /// for that use <see cref="Sender"/>.
    /// </summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Typed domain identity of the citizen that produced this message, resolved at
    /// the channel boundary. Always populated; never <c>default</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Channel adapters resolve <see cref="Sender"/> from their wire-level
    /// <see cref="SenderId"/> (typically <c>CitizenId.Of(UserId.From(wireToken))</c>).
    /// Synthetic producers (e.g. the conversation tool, sub-agent wake-ups) populate
    /// it directly with the originating agent (<c>CitizenId.Of(agentId)</c>).
    /// </para>
    /// <para>
    /// <see cref="SenderId"/> and <see cref="Sender"/> may differ in shape: for
    /// example the sub-agent wake-up has <c>SenderId = "subagent:&lt;id&gt;"</c> for
    /// audit while <see cref="Sender"/> carries the typed child agent id. Downstream
    /// participant tracking, conversation ownership and species classification must
    /// use <see cref="Sender"/>, never re-parse <see cref="SenderId"/>.
    /// </para>
    /// </remarks>
    public required CitizenId Sender { get; init; }

    /// <summary>
    /// Conversation identifier within the channel (e.g., chat ID, thread ID).
    /// Combined with <see cref="ChannelType"/> to derive a session key.
    /// </summary>
    public required ChannelAddress ChannelAddress { get; init; }

    /// <summary>The message text content.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional multi-modal content parts. When present, the message includes
    /// non-text content (audio, images, etc.) alongside or instead of <see cref="Content"/>.
    /// </summary>
    public IReadOnlyList<MessageContentPart>? ContentParts { get; init; }

    /// <summary>When the message was received.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Extensible metadata from the channel adapter.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// The channel binding ID this message arrived on, if known.
    /// Used by fan-out to exclude the originating binding from echo.
    /// </summary>
    public BindingId? BindingId { get; init; }

    /// <summary>
    /// Optional strongly-typed routing hints supplied by the channel adapter
    /// — the agent, session, and/or conversation the client wants to talk to.
    /// Null when the channel has no override (the default for most inbound
    /// traffic; the router resolves the destination from the binding instead).
    /// </summary>
    /// <remarks>
    /// Prior to sub-PR 6.3 (<c>#586</c>) these hints lived on
    /// <see cref="InboundMessage"/> as three weakly-typed <c>string?</c>
    /// fields (<c>TargetAgentId</c>, <c>SessionId</c>, <c>ConversationId</c>).
    /// They were collapsed into a single typed record so every downstream
    /// consumer reads Vogen-typed values and so writers cannot accidentally
    /// supply one override without the others. Read via
    /// <see cref="InboundMessageRoutingHints.FromMessage(InboundMessage)"/>
    /// when the consumer needs to default null to <see cref="InboundMessageRoutingHints.Empty"/>.
    /// </remarks>
    public InboundMessageRoutingHints? RoutingHints { get; init; }
}

/// <summary>
/// A message to send back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>The channel to send through.</summary>
    public required ChannelKey ChannelType { get; init; }

    /// <summary>Target conversation identifier within the channel.</summary>
    public required ChannelAddress ChannelAddress { get; init; }

    /// <summary>The message content.</summary>
    public required string Content { get; init; }

    /// <summary>The session this message belongs to.</summary>
    public string? SessionId { get; init; }

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
