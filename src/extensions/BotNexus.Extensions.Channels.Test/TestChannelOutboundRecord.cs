namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// An outbound message the gateway delivered to the test channel, captured verbatim for assertion.
/// </summary>
/// <param name="Address">The channel address the message was delivered to.</param>
/// <param name="Content">The delivered content.</param>
/// <param name="SessionId">Session the message belongs to, when the gateway supplied one.</param>
/// <param name="ConversationId">Conversation the message belongs to, when the gateway supplied one.</param>
/// <param name="BindingId">Binding the delivery was fanned out to, when known.</param>
/// <param name="Role">
/// The role the gateway asked the surface to render this under, or <c>null</c> when it applied no
/// override. Preserved as a plain string so the test client needs no domain reference.
/// </param>
/// <param name="Kind">Resolved presentation/delivery kind (#2149).</param>
/// <param name="IsStreamDelta">
/// <c>true</c> when this entry is a streaming delta rather than a complete message. Deltas are
/// recorded separately so a test can assert on the consolidated message without having to filter
/// partial text out by hand.
/// </param>
/// <param name="Sequence">
/// Monotonic per-adapter sequence number. Lets a test assert relative ordering across addresses,
/// which a per-address list alone cannot express.
/// </param>
/// <param name="TimestampUtc">When the adapter captured the delivery.</param>
public sealed record TestChannelOutboundRecord(
    string Address,
    string Content,
    string? SessionId,
    string? ConversationId,
    string? BindingId,
    string? Role,
    string Kind,
    bool IsStreamDelta,
    long Sequence,
    DateTimeOffset TimestampUtc);
