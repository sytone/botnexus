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
    /// Delegates the legacy-field lift to <see cref="InboundMessageRoutingHints.FromMessage"/>
    /// — that helper is the architecture-fence-sanctioned single reader of the legacy
    /// override fields (see <c>InboundMessageOverrideFenceTests</c>). Null / empty /
    /// whitespace inputs are gracefully normalised to a <c>null</c> override (the Vogen
    /// factories would otherwise throw on whitespace and crash the inbound pipeline).
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
