namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

/// <summary>
/// Evaluates host-owned work when a normal run would end after follow-up messages are drained.
/// The loop owns bounded continuation and validates structured parked dispositions.
/// </summary>
/// <param name="cancellationToken">The active run's cancellation token.</param>
/// <returns>A completion, continuation, or structured stop decision.</returns>
/// <remarks>Exceptions, including cancellation, propagate out of policy evaluation; the loop does not swallow them.</remarks>
public delegate Task<RunCompletionDecision> RunCompletionPolicy(CancellationToken cancellationToken);