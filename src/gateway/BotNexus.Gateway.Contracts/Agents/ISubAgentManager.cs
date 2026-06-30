using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Manages background sub-agent sessions spawned by parent agents.
/// </summary>
public interface ISubAgentManager
{
    /// <summary>
    /// Gets the platform-wide number of sub-agents that are currently running, aggregated across
    /// every parent session. This differs from <see cref="ListAsync"/>, which is parent-scoped:
    /// the portal stats overview needs a single headline "active sub-agents" figure spanning all
    /// parents, and a parent-by-parent sum would require enumerating every session. Implementations
    /// that do not track sub-agents (for example a no-op manager used by a controller that cannot
    /// spawn) inherit the default value of zero. Exposed as a synchronous property because it is a
    /// cheap read over the in-memory registry and is polled by the live stats panel.
    /// </summary>
    int ActiveSubAgentCount => 0;

    /// <summary>
    /// Spawns a background sub-agent session for the specified request.
    /// </summary>
    /// <param name="request">The spawn request describing parent context and execution overrides.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>Metadata describing the spawned sub-agent session.</returns>
    Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lists sub-agents associated with the specified parent session.
    /// </summary>
    /// <param name="parentSessionId">The parent session identifier.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A read-only list of sub-agent metadata for the parent session.</returns>
    Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default);

    /// <summary>
    /// Gets details for a specific sub-agent instance.
    /// </summary>
    /// <param name="subAgentId">The sub-agent identifier.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>The sub-agent metadata when found; otherwise <see langword="null" />.</returns>
    Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default);

    /// <summary>
    /// Terminates a running sub-agent if the requesting session is authorized.
    /// </summary>
    /// <param name="subAgentId">The sub-agent identifier.</param>
    /// <param name="requestingSessionId">The session requesting termination.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns><see langword="true" /> when the sub-agent was terminated; otherwise <see langword="false" />.</returns>
    Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default);

    /// <summary>
    /// Marks a sub-agent as completed and records a completion summary.
    /// </summary>
    /// <param name="subAgentId">The completed sub-agent identifier.</param>
    /// <param name="resultSummary">A short summary of the sub-agent result.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default);
}
