using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Loop;

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
    public static RunCompletionDecision Completed { get; } =
        new(RunCompletionStatus.Completed, []);

    public static RunCompletionDecision Continue(IReadOnlyList<string> openItemIds, string detail)
        => new(RunCompletionStatus.Working, openItemIds, Detail: detail);

    public static RunCompletionDecision Parked(
        RunStopReason reason,
        IReadOnlyList<string> openItemIds,
        string evidence,
        string continuationOwner,
        string wakeCondition,
        string? detail = null)
        => new(
            RunCompletionStatus.Parked,
            openItemIds,
            reason,
            detail,
            evidence,
            continuationOwner,
            wakeCondition);
}

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
    public static RunCompletionResult Completed { get; } =
        new(RunCompletionStatus.Completed, []);
}

public enum RunCompletionStatus
{
    Working,
    Parked,
    IncompleteWithoutStopReason,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Bounded reasons for legitimately ending a run while actionable checklist items remain.
/// Free text is supporting detail, never the reason itself.
/// </summary>
public enum RunStopReason
{
    UserInput,
    Approval,
    ExternalBlocker,
    Cancellation,
    SafetyBoundary,
    DurableAsyncWait,
}

public delegate Task<RunCompletionDecision> EvaluateRunCompletionDelegate(CancellationToken cancellationToken);
