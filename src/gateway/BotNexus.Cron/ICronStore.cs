using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public interface ICronStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default);
    Task<CronJob?> GetAsync(JobId jobId, CancellationToken ct = default);
    Task<IReadOnlyList<CronJob>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);
    Task<CronJob> UpdateAsync(CronJob job, CancellationToken ct = default);
    Task DeleteAsync(JobId jobId, CancellationToken ct = default);
    Task<CronRun> RecordRunStartAsync(JobId jobId, CancellationToken ct = default);
    Task RecordRunCompleteAsync(RunId runId, string status, string? error = null, SessionId? sessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(JobId jobId, int limit = 20, CancellationToken ct = default);
}
