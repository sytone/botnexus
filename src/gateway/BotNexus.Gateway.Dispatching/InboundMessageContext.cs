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
/// Optional explicit conversation target from the transport when the client already knows its active conversation.
/// </param>
public sealed record InboundMessageContext(
    AgentId AgentId,
    InboundMessage Message,
    ChannelSource Source,
    string? RequestedConversationId = null)
{
    /// <summary>
    /// Creates a dispatch context directly from an inbound transport payload.
    /// This is the default adapter path for existing gateway callers.
    /// </summary>
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
            message.ThreadId,
            message.BindingId);
        return new InboundMessageContext(agentId, message, source, message.ConversationId);
    }
}
