using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

/// <summary>Bounded scheduled-run activity request; null means the last 24 hours across the gateway.</summary>
public sealed record CronRunActivityQuery
{
    /// <summary>Inclusive start, normalized to UTC; defaults to 24 hours before the requested end.</summary>
    public DateTimeOffset? StartInclusive { get; init; }
    /// <summary>Exclusive end, normalized to UTC; defaults to the injected clock's current UTC time.</summary>
    public DateTimeOffset? EndExclusive { get; init; }
    /// <summary>Optional exact job scope; null deliberately means gateway-wide, not an empty job set.</summary>
    public JobId? JobId { get; init; }
    /// <summary>Maximum ranked jobs to return, clamped to 1..50. Totals are never limited by this value.</summary>
    public int TopJobLimit { get; init; } = 10;
}

/// <summary>SQL-derived measurements and coverage of all runs in one scope and interval.</summary>
public sealed record CronRunActivityTotals
{
    /// <summary>All runs, including unmeasured and in-flight runs.</summary>
    public long RunCount { get; init; }
    /// <summary>Distinct job identities with runs in the interval.</summary>
    public long JobCount { get; init; }
    /// <summary>Runs carrying at least one non-null token field; measured zero is included.</summary>
    public long MeasuredRunCount { get; init; }
    /// <summary>Runs with neither token field measured.</summary>
    public long UnmeasuredRunCount => RunCount - MeasuredRunCount;
    /// <summary>Runs still stamped running.</summary>
    public long RunningRunCount { get; init; }
    /// <summary>Runs without a completion timestamp, regardless of status.</summary>
    public long UnfinalizedRunCount { get; init; }
    /// <summary>Sum of known prompt tokens; null when no prompt measurement exists.</summary>
    public long? TotalPromptTokens { get; init; }
    /// <summary>Sum of known completion tokens; null when no completion measurement exists.</summary>
    public long? TotalCompletionTokens { get; init; }
    /// <summary>Sum of known token fields, not an estimate for missing fields; null when both are unmeasured.</summary>
    public long? TotalTokens { get; init; }
    /// <summary>Sum of known model turns, or null when unmeasured.</summary>
    public long? TotalTurns { get; init; }
    /// <summary>Sum of known tool calls, or null when unmeasured.</summary>
    public long? TotalToolCalls { get; init; }
    /// <summary>Sum of known durations in milliseconds, or null when unmeasured.</summary>
    public long? TotalDurationMs { get; init; }
    /// <summary>Runs with a prompt-token measurement.</summary>
    public long PromptTokenRunCount { get; init; }
    /// <summary>Runs with a completion-token measurement.</summary>
    public long CompletionTokenRunCount { get; init; }
    /// <summary>Runs with a turn-count measurement.</summary>
    public long TurnRunCount { get; init; }
    /// <summary>Runs with a tool-count measurement.</summary>
    public long ToolCallRunCount { get; init; }
    /// <summary>Runs with a duration measurement.</summary>
    public long DurationRunCount { get; init; }
}

/// <summary>One ranked job and its SQL-derived activity measurements.</summary>
public sealed record CronRunActivityJob
{
    /// <summary>Persisted job identity, without provider attribution inferred from current configuration.</summary>
    public required JobId JobId { get; init; }
    /// <summary>All runs for this job in the effective interval.</summary>
    public required CronRunActivityTotals Totals { get; init; }
}

/// <summary>Bounded activity, including metadata even when there are no matching runs.</summary>
/// <remarks>
/// Retention purges by completion time, not start time, and protects running rows. The start-time
/// clamp is therefore conservative: retained older running or long-lived runs can exist outside
/// this report. The result is not a promise about completeness of historical data or purge timing.
/// </remarks>
public sealed record CronRunActivity
{
    /// <summary>Requested inclusive start in UTC, before clamps.</summary>
    public required DateTimeOffset RequestedStartInclusiveUtc { get; init; }
    /// <summary>Requested exclusive end in UTC, before clamps.</summary>
    public required DateTimeOffset RequestedEndExclusiveUtc { get; init; }
    /// <summary>Effective inclusive start in UTC, clamped to the conservative retention horizon and now.</summary>
    public required DateTimeOffset EffectiveStartInclusiveUtc { get; init; }
    /// <summary>Effective exclusive end in UTC; equal to start when clamping removes the entire interval.</summary>
    public required DateTimeOffset EffectiveEndExclusiveUtc { get; init; }
    /// <summary>True when requested start precedes the conservative configured retention horizon.</summary>
    public required bool WindowTruncatedByRetention { get; init; }
    /// <summary>True when requested end is later than now.</summary>
    public required bool WindowTruncatedByNow { get; init; }
    /// <summary>Totals over every matching job, independently of the ranked-job limit.</summary>
    public required CronRunActivityTotals Totals { get; init; }
    /// <summary>At most 50 jobs, known-token totals descending, unmeasured last, then ordinal job identity.</summary>
    public required IReadOnlyList<CronRunActivityJob> TopJobs { get; init; }
}
