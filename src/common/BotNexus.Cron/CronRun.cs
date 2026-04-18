namespace BotNexus.Cron;

public sealed record CronRun
{
    public required string Id { get; init; }
    public required string JobId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
    public string? SessionId { get; init; }
}
