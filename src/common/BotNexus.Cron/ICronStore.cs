namespace BotNexus.Cron;

public interface ICronStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<CronJob> CreateAsync(CronJob job, CancellationToken ct = default);
    Task<CronJob?> GetAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<CronJob>> ListAsync(string? agentId = null, CancellationToken ct = default);
    Task<CronJob> UpdateAsync(CronJob job, CancellationToken ct = default);
    Task DeleteAsync(string jobId, CancellationToken ct = default);
    Task<CronRun> RecordRunStartAsync(string jobId, CancellationToken ct = default);
    Task RecordRunCompleteAsync(string runId, string status, string? error = null, string? sessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CronRun>> GetRunHistoryAsync(string jobId, int limit = 20, CancellationToken ct = default);
}
