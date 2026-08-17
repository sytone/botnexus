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

    /// <summary>
    /// Per-run cost measurements (#2641). Never null as a record property - an unmeasured run
    /// carries a <see cref="CronRunCost"/> whose every member is null, so a consumer distinguishes
    /// "not measured" from "zero" without also having to null-check the container.
    /// </summary>
    public CronRunCost Cost { get; init; } = new();
}
