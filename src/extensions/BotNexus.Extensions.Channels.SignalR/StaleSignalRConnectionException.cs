using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;

namespace BotNexus.Extensions.Channels.SignalR;

/// <summary>
/// Thrown by <see cref="SignalRChannelAdapter"/> when a send fails because the target
/// SignalR connection no longer exists. Inherits <see cref="StaleChannelConnectionException"/>
/// so GatewayHost's fan-out loop can catch the base type to demote the binding to Muted.
/// </summary>
public sealed class StaleSignalRConnectionException : StaleChannelConnectionException
{
    /// <summary>
    /// Initialises a new instance describing a stale SignalR connection for a specific binding.
    /// </summary>
    public StaleSignalRConnectionException(BindingId bindingId, ConversationId conversationId, Exception? inner = null)
        : base(bindingId, conversationId, inner)
    {
    }
}
