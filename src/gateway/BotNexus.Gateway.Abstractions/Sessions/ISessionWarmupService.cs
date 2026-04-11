namespace BotNexus.Gateway.Abstractions.Sessions;

public interface ISessionWarmupService
{
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(string agentId, CancellationToken ct = default);
}
