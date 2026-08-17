namespace BotNexus.Cron;

/// <summary>
/// Per-run cost measurements captured at run finalization (#2641).
/// </summary>
/// <remarks>
/// <para>
/// Every member is nullable and every <c>null</c> means <b>not measured</b>, never zero. This is the
/// #2554 <c>ScheduleActivatedAt</c> rule applied to cost: a <c>command</c> or <c>webhook</c> job has
/// no turn or token concept at all, and a run recorded before these columns existed measured nothing.
/// Coercing either to <c>0</c> would present "we did not look" as "this job is free" - the single
/// most misleading value this feature could produce, because it inverts the exact ranking the
/// feature exists to establish.
/// </para>
/// <para>
/// <see cref="PromptTokens"/> / <see cref="CompletionTokens"/> stay null whenever the provider
/// reported no usage for the run. They are deliberately NOT derived from a character-count estimate:
/// an estimate that reads as a measurement is worse than an honest absence.
/// </para>
/// </remarks>
/// <param name="TurnCount">Model turns (LLM invocations) the run consumed, or null when unmeasured.</param>
/// <param name="ToolCallCount">Tool invocations the run performed, or null when unmeasured.</param>
/// <param name="DurationMs">Wall-clock run duration in milliseconds, or null when unmeasured.</param>
/// <param name="PromptTokens">Provider-reported prompt tokens across the run, or null when unmeasured.</param>
/// <param name="CompletionTokens">Provider-reported completion tokens across the run, or null when unmeasured.</param>
public sealed record CronRunCost(
    int? TurnCount = null,
    int? ToolCallCount = null,
    long? DurationMs = null,
    long? PromptTokens = null,
    long? CompletionTokens = null)
{
    /// <summary>
    /// Total provider-reported tokens for the run, or <c>null</c> when neither side was measured.
    /// A measured prompt count with an unmeasured completion count still yields a total (the
    /// measured part), because the alternative - discarding a real measurement because its partner
    /// is absent - loses information the operator has.
    /// </summary>
    public long? TotalTokens => PromptTokens is null && CompletionTokens is null
        ? null
        : (PromptTokens ?? 0) + (CompletionTokens ?? 0);

    /// <summary>
    /// True when this instance carries no measurement at all, so a caller can skip the write
    /// entirely rather than issuing an UPDATE that only ever stores NULLs.
    /// </summary>
    public bool IsEmpty => TurnCount is null
        && ToolCallCount is null
        && DurationMs is null
        && PromptTokens is null
        && CompletionTokens is null;
}
