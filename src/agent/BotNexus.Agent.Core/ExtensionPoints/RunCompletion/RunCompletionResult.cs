namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

/// <summary>
/// Authoritative terminal classification emitted with the run-end event.
/// </summary>
public sealed record RunCompletionResult(
    RunCompletionStatus Status,
    IReadOnlyList<string> OpenItemIds,
    RunStopReason? StopReason = null,
    string? Detail = null,
    string? Evidence = null,
    string? ContinuationOwner = null,
    string? WakeCondition = null,
    int ContinuationAttempts = 0)
{
    /// <summary>Bounded payload-free guard decisions; empty when no guard was observed.</summary>
    public IReadOnlyList<GuardObservation> GuardObservations { get; init; } = [];
    /// <summary>Reports successful completion with no open work items or continuation attempts.</summary>
    public static RunCompletionResult Completed { get; } =
        new(RunCompletionStatus.Completed, []);
}