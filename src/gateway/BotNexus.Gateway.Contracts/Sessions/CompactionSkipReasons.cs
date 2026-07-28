namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// #2460: stable, machine-readable codes naming WHY a compaction aborted without mutating
/// history. Every <c>Succeeded = false</c> return path in the compactor stamps exactly one of
/// these onto <see cref="CompactionResult.SkipReason"/>, and the coordinator logs it alongside
/// the <c>outcome=Aborted</c> line so a repeating no-op abort loop is diagnosable from logs
/// alone. Values are treated as a log/telemetry contract: do not rename existing codes.
/// </summary>
public static class CompactionSkipReasons
{
    /// <summary>The per-session circuit breaker is open and still inside its cooldown window.</summary>
    public const string CircuitBreakerOpen = "CircuitBreakerOpen";

    /// <summary>The session history snapshot was empty; there is nothing to summarise.</summary>
    public const string EmptyHistory = "EmptyHistory";

    /// <summary>
    /// The turn split produced no summarizable entries (and the PreservedTurns fallback also found
    /// none). This is the branch behind the observed repeating abort loop: the session keeps
    /// growing while every split remains unsummarizable.
    /// </summary>
    public const string NoSummarizableTurns = "NoSummarizableTurns";

    /// <summary>The summarization call exceeded the configured compaction timeout.</summary>
    public const string SummarizationTimeout = "SummarizationTimeout";

    /// <summary>All candidate models returned an empty/unusable summary.</summary>
    public const string EmptySummary = "EmptySummary";

    /// <summary>The summarization call threw (surfaced by the coordinator, not the compactor).</summary>
    public const string SummarizationFailed = "SummarizationFailed";

    /// <summary>
    /// The compaction result could not be applied because history was destructively modified
    /// while the summary call was in flight.
    /// </summary>
    public const string ConcurrentHistoryChange = "ConcurrentHistoryChange";

    /// <summary>The session was deleted, sealed or rebound while compaction was in flight (#1518).</summary>
    public const string SessionRebound = "SessionRebound";
}
