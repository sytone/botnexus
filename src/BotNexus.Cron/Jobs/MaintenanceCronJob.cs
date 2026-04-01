using System.Diagnostics;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;

namespace BotNexus.Cron.Jobs;

public sealed class MaintenanceCronJob(
    CronJobConfig config,
    IMemoryConsolidator memoryConsolidator,
    ISessionManager sessionManager) : ICronJob
{
    private const int DefaultRetentionDays = 30;
    private readonly CronJobConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IMemoryConsolidator _memoryConsolidator = memoryConsolidator ?? throw new ArgumentNullException(nameof(memoryConsolidator));
    private readonly ISessionManager _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));

    public string Name => $"maintenance:{_config.Action ?? "unknown"}";
    public CronJobType Type => CronJobType.Maintenance;
    public string Schedule => _config.Schedule;
    public TimeZoneInfo? TimeZone => ResolveTimeZone(_config.Timezone);
    public bool Enabled { get; set; } = config.Enabled;

    public async Task<CronJobResult> ExecuteAsync(CronJobContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var action = _config.Action?.Trim().ToLowerInvariant();

        return action switch
        {
            "consolidate-memory" => await ConsolidateMemoryAsync(stopwatch, cancellationToken).ConfigureAwait(false),
            "cleanup-sessions" => await CleanupSessionsAsync(stopwatch, cancellationToken).ConfigureAwait(false),
            "rotate-logs" => await RotateLogsAsync(stopwatch, cancellationToken).ConfigureAwait(false),
            _ => new CronJobResult(false, Error: $"Unknown maintenance action '{_config.Action}'.", Duration: stopwatch.Elapsed)
        };
    }

    private async Task<CronJobResult> ConsolidateMemoryAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var agents = _config.Agents
            .Where(static agent => !string.IsNullOrWhiteSpace(agent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var succeeded = 0;
        var dailyFilesProcessed = 0;
        var entriesConsolidated = 0;
        var details = new List<string>(agents.Count);

        foreach (var agent in agents)
        {
            var result = await _memoryConsolidator.ConsolidateAsync(agent, cancellationToken).ConfigureAwait(false);
            if (result.Success)
                succeeded++;

            dailyFilesProcessed += result.DailyFilesProcessed;
            entriesConsolidated += result.EntriesConsolidated;
            details.Add($"{agent}: success={result.Success}, files={result.DailyFilesProcessed}, entries={result.EntriesConsolidated}");
        }

        var allSucceeded = succeeded == agents.Count;
        return new CronJobResult(
            Success: allSucceeded,
            Output: details.Count == 0 ? "No agents configured for memory consolidation." : string.Join(Environment.NewLine, details),
            Duration: stopwatch.Elapsed,
            Metadata: new Dictionary<string, object>
            {
                ["agentsProcessed"] = agents.Count,
                ["agentsSucceeded"] = succeeded,
                ["dailyFilesProcessed"] = dailyFilesProcessed,
                ["entriesConsolidated"] = entriesConsolidated
            });
    }

    private async Task<CronJobResult> CleanupSessionsAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var retentionDays = _config.SessionCleanupDays > 0 ? _config.SessionCleanupDays : DefaultRetentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        var keys = await _sessionManager.ListKeysAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        var checkedCount = 0;

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await _sessionManager.GetOrCreateAsync(key, "maintenance", cancellationToken).ConfigureAwait(false);
            checkedCount++;

            if (session.History.Count == 0)
                continue;

            var lastEntry = session.History.Max(static entry => entry.Timestamp);
            if (lastEntry >= cutoff)
                continue;

            await _sessionManager.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            deleted++;
        }

        return new CronJobResult(
            Success: true,
            Output: $"Deleted {deleted} sessions older than {retentionDays} days.",
            Duration: stopwatch.Elapsed,
            Metadata: new Dictionary<string, object>
            {
                ["sessionsChecked"] = checkedCount,
                ["sessionsDeleted"] = deleted,
                ["retentionDays"] = retentionDays
            });
    }

    private Task<CronJobResult> RotateLogsAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var retentionDays = _config.LogRetentionDays > 0 ? _config.LogRetentionDays : DefaultRetentionDays;
        var logsPath = ResolveLogsPath(_config.LogsPath);

        if (!Directory.Exists(logsPath))
        {
            return Task.FromResult(new CronJobResult(
                Success: true,
                Output: $"Log directory not found: {logsPath}.",
                Duration: stopwatch.Elapsed,
                Metadata: new Dictionary<string, object>
                {
                    ["archivedFiles"] = 0,
                    ["retentionDays"] = retentionDays,
                    ["logsPath"] = logsPath
                }));
        }

        var archivePath = Path.Combine(logsPath, "archive");
        Directory.CreateDirectory(archivePath);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var archived = 0;
        var candidates = Directory.EnumerateFiles(logsPath, "*", SearchOption.TopDirectoryOnly);
        foreach (var filePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(filePath, archivePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if (lastWrite >= cutoff.UtcDateTime)
                continue;

            var destination = Path.Combine(archivePath, Path.GetFileName(filePath));
            if (File.Exists(destination))
                destination = Path.Combine(archivePath, $"{Path.GetFileNameWithoutExtension(filePath)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(filePath)}");

            File.Move(filePath, destination);
            archived++;
        }

        return Task.FromResult(new CronJobResult(
            Success: true,
            Output: $"Archived {archived} log files older than {retentionDays} days.",
            Duration: stopwatch.Elapsed,
            Metadata: new Dictionary<string, object>
            {
                ["archivedFiles"] = archived,
                ["retentionDays"] = retentionDays,
                ["logsPath"] = logsPath
            }));
    }

    private static string ResolveLogsPath(string? logsPath)
    {
        if (!string.IsNullOrWhiteSpace(logsPath))
            return BotNexusHome.ResolvePath(logsPath);

        return Path.Combine(BotNexusHome.ResolveHomePath(), "logs");
    }

    private static TimeZoneInfo? ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
