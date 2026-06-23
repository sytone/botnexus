using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Routes an agent-exchange request to a remote world (cross-world federation): resolves the
/// target world/peer, enforces outbound permission, builds the sender-side conversation, and
/// relays each turn over the cross-world channel.
/// </summary>
/// <remarks>
/// Extracted from <see cref="AgentExchangeService"/> as part of #1542 to split cross-world
/// federation routing out of the in-world peer-exchange service (SRP). The federation half owns
/// the <c>CrossWorldChannelAdapter</c>, the source world id, and the <c>PlatformConfig</c> peer/
/// permission/target resolution — none of which the in-world turn loop needs. This makes the
/// federation permission/peer/call-chain logic unit-testable against just a config + a fake relay,
/// rather than the full local-exchange machinery (registry, supervisor, both stores, budget
/// tracker).
/// </remarks>
public interface ICrossWorldExchangeRouter
{
    /// <summary>
    /// Relays <paramref name="request"/> to the remote world identified by
    /// <paramref name="parsedTarget"/>. The shared turn loop, session pinning, transcript, and
    /// seal/archive are handled by the supplied <see cref="AgentExchangeTurnEngine"/>; this method
    /// owns the cross-world resolution, permission gate, and per-turn relay.
    /// </summary>
    /// <param name="request">The originating exchange request (already chain-normalised + budgeted).</param>
    /// <param name="parsedTarget">The parsed cross-world reference for <c>request.TargetId</c>.</param>
    /// <param name="normalizedChain">The normalised call chain for loop/depth bookkeeping.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    Task<AgentExchangeResult> ConverseCrossWorldAsync(
        AgentExchangeRequest request,
        CrossWorldAgentReference parsedTarget,
        IReadOnlyList<AgentId> normalizedChain,
        CancellationToken cancellationToken = default);
}
