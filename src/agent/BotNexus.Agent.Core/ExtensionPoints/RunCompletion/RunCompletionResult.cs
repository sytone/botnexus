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
    /// <summary>Reports successful completion with no open work items or continuation attempts.</summary>
    public static RunCompletionResult Completed { get; } =
        new(RunCompletionStatus.Completed, []);
}