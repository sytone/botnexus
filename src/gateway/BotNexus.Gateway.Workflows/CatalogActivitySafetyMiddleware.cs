using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace BotNexus.Gateway.Workflows;

/// <summary>
/// A catalog activity may have performed a side effect before failing or losing its checkpoint.
/// Never let the engine's default retry policy blindly invoke it again after a reported error.
/// An operator must reconcile the existing attempt/uncertainty ledger before explicitly resuming.
/// This is not an exactly-once guarantee: crash recovery still requires an idempotent activity/attempt boundary.
/// </summary>
public sealed class CatalogActivitySafetyMiddleware : IWorkflowStepMiddleware
{
    /// <summary>Sets catalog steps to suspend on reported errors before invoking the next middleware.</summary>
    /// <param name="context">The engine execution context whose step error policy may be changed.</param>
    /// <param name="body">The step body; non-catalog bodies retain their configured policy.</param>
    /// <param name="next">The remaining execution pipeline.</param>
    /// <returns>The remaining pipeline's result without catching or retrying its failures.</returns>
    /// <remarks>Stateless and safe to share. Suspension does not prevent replay after an unreported process crash.</remarks>
    public Task<ExecutionResult> HandleAsync(IStepExecutionContext context, IStepBody body, WorkflowStepDelegate next)
    {
        if (body is CatalogActivityStep)
            context.Step.ErrorBehavior = WorkflowErrorHandling.Suspend;
        return next();
    }
}
