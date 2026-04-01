namespace BotNexus.Core.Abstractions;

/// <summary>Contract for cron job scheduling.</summary>
public interface ICronService
{
    /// <summary>Schedules a cron job.</summary>
    void Schedule(string name, string cronExpression, Func<CancellationToken, Task> action, TimeZoneInfo? timeZone = null);

    /// <summary>Removes a scheduled cron job.</summary>
    void Remove(string name);

    /// <summary>Returns all scheduled job names.</summary>
    IReadOnlyList<string> GetScheduledJobs();
}
