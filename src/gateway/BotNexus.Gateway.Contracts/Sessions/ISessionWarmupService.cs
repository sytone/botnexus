using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Defines the contract for isession warmup service.
/// </summary>
public interface ISessionWarmupService
{
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(AgentId agentId, CancellationToken ct = default);
}
