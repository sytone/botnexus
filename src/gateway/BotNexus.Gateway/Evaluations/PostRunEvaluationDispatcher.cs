using BotNexus.Gateway.Abstractions.Evaluations;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Evaluations;

/// <summary>
/// Focused authoritative-finalizer seam: post-run work is admitted only after transcript persistence
/// reports <see cref="SessionSaveOutcome.Persisted"/>. Rebound or failed persistence never reaches it.
/// </summary>
public sealed class PostRunEvaluationDispatcher(IPostRunEvaluationCoordinator coordinator)
{
    public PostRunEvaluationAdmission DispatchAfterFinalizer(
        SessionSaveOutcome finalizerOutcome,
        RunOutcomeSnapshot snapshot)
        => finalizerOutcome == SessionSaveOutcome.Persisted
            ? coordinator.Enqueue(snapshot)
            : PostRunEvaluationAdmission.Disabled;
}
