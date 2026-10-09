namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

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