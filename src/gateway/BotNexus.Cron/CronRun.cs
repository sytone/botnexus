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

    /// <summary>Age of a running row at projection time; null for terminal rows.</summary>
    public TimeSpan? RunningAge { get; init; }

    /// <summary>
    /// Lifecycle state of the session that owns a running row: active, suspended, sealed, expired,
    /// missing, none, or unknown. Null for terminal rows.
    /// </summary>
    public string? OwnerSessionState { get; init; }

    /// <summary>Whether the current scheduler process still owns an executor for this running row.</summary>
    public bool? HasActiveExecutor { get; init; }

    /// <summary>
    /// Per-run cost measurements (#2641). Never null as a record property - an unmeasured run
    /// carries a <see cref="CronRunCost"/> whose every member is null, so a consumer distinguishes
    /// "not measured" from "zero" without also having to null-check the container.
    /// </summary>
    public CronRunCost Cost { get; init; } = new();
}
