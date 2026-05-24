using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

public sealed record CronRun
{
    public required RunId Id { get; init; }
    public required JobId JobId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
    public SessionId? SessionId { get; init; }
}
