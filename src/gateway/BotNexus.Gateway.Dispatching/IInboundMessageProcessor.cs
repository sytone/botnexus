using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Per-message processor invoked by <see cref="IInboundMessageOrchestrator"/>
/// from inside the per-session queued worker. Runs the message's resolution,
/// session save, and agent execution and returns an <see cref="InboundProcessingOutcome"/>.
/// </summary>
/// <remarks>
/// The processor is the seam between the orchestrator (which owns FIFO queues
/// and backpressure) and the host (which owns sessions, the supervisor, channel
/// manager, and conversation dispatcher). Hosts implement this interface and
/// hand themselves to the orchestrator as the worker callback. The split keeps
/// queue mechanics out of the host and host mechanics out of the orchestrator,
/// which is the prerequisite for migrating SignalR's GatewayHub (and any future
/// transport) onto the same queue without re-inventing the host's pipeline.
/// </remarks>
public interface IInboundMessageProcessor
{
    /// <summary>
    /// Process a single inbound message: resolve target agents, persist session
    /// state, run the agent(s), and propagate any outbound events. Called
    /// serially per session-key by the orchestrator's worker loop.
    /// </summary>
    /// <param name="message">Inbound message dequeued from the per-session queue.</param>
    /// <param name="cancellationToken">
    /// Cancellation token for the processing work. Typically <see cref="CancellationToken.None"/>
    /// when invoked by the orchestrator — agent work survives transport disconnect.
    /// </param>
    /// <returns>
    /// Aggregate outcome including per-agent dispatch results and a signal
    /// telling the orchestrator whether to close the per-session queue.
    /// </returns>
    Task<InboundProcessingOutcome> ProcessAsync(InboundMessage message, CancellationToken cancellationToken);
}
