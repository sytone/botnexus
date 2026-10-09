namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

/// <summary>
/// Host evaluation performed when the model would otherwise end a run normally.
/// </summary>
public sealed record RunCompletionDecision(
    RunCompletionStatus Status,
    IReadOnlyList<string> OpenItemIds,
    RunStopReason? StopReason = null,
    string? Detail = null,
    string? Evidence = null,
    string? ContinuationOwner = null,
    string? WakeCondition = null)
{
    /// <summary>Accepts normal completion with no open work items.</summary>
    public static RunCompletionDecision Completed { get; } =
        new(RunCompletionStatus.Completed, []);

    /// <summary>Requests another turn for actionable work, subject to the loop's continuation bound.</summary>
    /// <param name="openItemIds">Identities of the work items that remain actionable.</param>
    /// <param name="detail">Context for the continuation instruction.</param>
    /// <returns>A working decision carrying the supplied work items and detail.</returns>
    public static RunCompletionDecision Continue(IReadOnlyList<string> openItemIds, string detail)
        => new(RunCompletionStatus.Working, openItemIds, Detail: detail);

    /// <summary>Creates a structured stop disposition for work that cannot continue now.</summary>
    /// <param name="reason">A defined reason for stopping with work still open.</param>
    /// <param name="openItemIds">Identities of the work items left open.</param>
    /// <param name="evidence">Non-blank evidence supporting the stop.</param>
    /// <param name="continuationOwner">Non-blank identity of the owner responsible for resuming work.</param>
    /// <param name="wakeCondition">Non-blank condition under which work can resume.</param>
    /// <param name="detail">Optional supporting explanation.</param>
    /// <returns>A parked decision containing the validated stop disposition.</returns>
    /// <exception cref="ArgumentException">Required disposition text is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stop reason is not defined.</exception>
    public static RunCompletionDecision Parked(
        RunStopReason reason,
        IReadOnlyList<string> openItemIds,
        string evidence,
        string continuationOwner,
        string wakeCondition,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(wakeCondition);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown run stop reason.");

        return new(
            RunCompletionStatus.Parked,
            openItemIds,
            reason,
            detail,
            evidence,
            continuationOwner,
            wakeCondition);
    }
}
