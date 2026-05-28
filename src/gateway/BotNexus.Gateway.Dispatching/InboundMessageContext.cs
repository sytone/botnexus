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
/// its active conversation. Lifted from <see cref="InboundMessage.RoutingHints"/> by
/// <see cref="FromInboundMessage"/>.
/// </param>
/// <param name="RequestedAgentId">
/// Optional explicit agent target supplied by the transport. Lifted from
/// <see cref="InboundMessage.RoutingHints"/> by <see cref="FromInboundMessage"/>.
/// </param>
/// <param name="RequestedSessionId">
/// Optional explicit session resume target supplied by the transport. Lifted from
/// <see cref="InboundMessage.RoutingHints"/> by <see cref="FromInboundMessage"/>.
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
    /// Creates a dispatch context directly from an inbound transport payload, projecting
    /// the typed <see cref="InboundMessage.RoutingHints"/> payload (if any) into the
    /// strongly-typed <see cref="RequestedConversationId"/> / <see cref="RequestedAgentId"/>
    /// / <see cref="RequestedSessionId"/> properties on the context.
    /// </summary>
    /// <remarks>
    /// Delegates the read to <see cref="InboundMessageRoutingHints.FromMessage"/>, which
    /// returns <see cref="InboundMessageRoutingHints.Empty"/> when the message has no
    /// routing hints — callers never have to null-check.
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
        var hints = InboundMessageRoutingHints.FromMessage(message);
        return new InboundMessageContext(
            agentId,
            message,
            source,
            RequestedConversationId: hints.RequestedConversationId,
            RequestedAgentId: hints.RequestedAgentId,
            RequestedSessionId: hints.RequestedSessionId);
    }
}
