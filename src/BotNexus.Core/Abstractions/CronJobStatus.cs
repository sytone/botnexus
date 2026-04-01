namespace BotNexus.Core.Abstractions;

/// <summary>Current status snapshot of a registered cron job.</summary>
public sealed record CronJobStatus(
    string Name,
    CronJobType Type,
    string Schedule,
    bool Enabled,
    DateTimeOffset? LastRunStartedAt,
    DateTimeOffset? NextOccurrence,
    bool? LastRunSuccess,
    TimeSpan? LastRunDuration,
    int ConsecutiveFailures = 0);
