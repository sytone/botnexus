using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Manages background sub-agent sessions spawned by parent agents.
/// Distinct from <see cref="IAgentCommunicator" />, which handles synchronous sub/cross-agent calls.
/// </summary>
public interface ISubAgentManager
{
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
    Task<IReadOnlyList<SubAgentInfo>> ListAsync(string parentSessionId, CancellationToken ct = default);

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
    Task<bool> KillAsync(string subAgentId, string requestingSessionId, CancellationToken ct = default);

    /// <summary>
    /// Marks a sub-agent as completed and records a completion summary.
    /// </summary>
    /// <param name="subAgentId">The completed sub-agent identifier.</param>
    /// <param name="resultSummary">A short summary of the sub-agent result.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default);
}
