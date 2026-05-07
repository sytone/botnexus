namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Coordinates inbound conversation/session resolution for transport adapters.
/// Implementations isolate routing policy from concrete hosts such as SignalR hubs or GatewayHost.
/// </summary>
public interface IConversationDispatcher
{
    /// <summary>
    /// Resolves conversation and session targets for an inbound message, returning metadata
    /// required by hosts to continue processing and route outbound responses.
    /// </summary>
    /// <param name="context">Inbound dispatch context from the transport layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved dispatch outcome including source and session metadata.</returns>
    Task<DispatchResult> DispatchAsync(InboundMessageContext context, CancellationToken cancellationToken = default);
}
