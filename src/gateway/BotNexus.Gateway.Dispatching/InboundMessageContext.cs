using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Captures all inbound dispatch inputs before conversation/session resolution so
/// transports can pass a single immutable contract into dispatch orchestration.
/// </summary>
/// <param name="AgentId">Agent selected by upstream routing.</param>
/// <param name="Message">Inbound payload received from the transport.</param>
/// <param name="Source">Normalized channel source identity for the inbound payload.</param>
/// <param name="RequestedConversationId">
/// Optional explicit conversation target from the transport when the client already knows
/// its active conversation. Lifted from the legacy <see cref="InboundMessage.ConversationId"/>
/// override by <see cref="FromInboundMessage"/>; consumers must read this typed property
/// rather than re-parsing the raw string field.
/// </param>
/// <param name="RequestedAgentId">
/// Optional explicit agent target supplied by the transport. Lifted from the legacy
/// <see cref="InboundMessage.TargetAgentId"/> override by <see cref="FromInboundMessage"/>.
/// </param>
/// <param name="RequestedSessionId">
/// Optional explicit session resume target supplied by the transport. Lifted from the legacy
/// <see cref="InboundMessage.SessionId"/> override by <see cref="FromInboundMessage"/>.
/// </param>
public sealed record InboundMessageContext(
    AgentId AgentId,
    InboundMessage Message,
    ChannelSource Source,
    ConversationId? RequestedConversationId = null,
    AgentId? RequestedAgentId = null,
    SessionId? RequestedSessionId = null)
{
    /// <summary>
    /// Creates a dispatch context directly from an inbound transport payload, lifting the
    /// legacy stringly-typed routing overrides on <see cref="InboundMessage"/> into the
    /// strongly-typed <see cref="RequestedConversationId"/> / <see cref="RequestedAgentId"/>
    /// / <see cref="RequestedSessionId"/> properties on the context.
    /// </summary>
    /// <remarks>
    /// This is the only legitimate site that reads <see cref="InboundMessage.TargetAgentId"/>,
    /// <see cref="InboundMessage.SessionId"/>, and <see cref="InboundMessage.ConversationId"/>
    /// — the architecture fence in <c>InboundMessageOverrideFenceTests</c> bans every other
    /// consumer from re-parsing those raw fields. Null / empty / whitespace inputs are
    /// gracefully normalised to a <c>null</c> override (the Vogen factories would otherwise
    /// throw on whitespace and crash the inbound pipeline).
    /// </remarks>
    /// <param name="agentId">Agent selected for the message.</param>
    /// <param name="message">Inbound transport payload.</param>
    /// <returns>Normalized context ready for dispatching.</returns>
    public static InboundMessageContext FromInboundMessage(AgentId agentId, InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var source = new ChannelSource(
            message.ChannelType,
            message.ChannelAddress,
            message.SenderId,
            message.BindingId);
        return new InboundMessageContext(
            agentId,
            message,
            source,
            RequestedConversationId: TryLiftConversationId(message.ConversationId),
            RequestedAgentId: TryLiftAgentId(message.TargetAgentId),
            RequestedSessionId: TryLiftSessionId(message.SessionId));
    }

    private static ConversationId? TryLiftConversationId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : ConversationId.From(raw);

    private static AgentId? TryLiftAgentId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : AgentId.From(raw);

    private static SessionId? TryLiftSessionId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : SessionId.From(raw);
}
