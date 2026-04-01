using Cronos;

namespace BotNexus.Cron;

/// <summary>Represents a single scheduled cron job.</summary>
internal sealed class CronJob(
    string name,
    CronExpression expression,
    Func<CancellationToken, Task> action,
    TimeZoneInfo timeZone)
{
    public string Name { get; } = name;
    public CronExpression Expression { get; } = expression;
    public Func<CancellationToken, Task> Action { get; } = action;
    public TimeZoneInfo TimeZone { get; } = timeZone;
    public DateTimeOffset? NextOccurrence { get; set; }
}
