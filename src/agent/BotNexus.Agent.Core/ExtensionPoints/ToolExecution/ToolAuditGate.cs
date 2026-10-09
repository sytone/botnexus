namespace BotNexus.Agent.Core.ExtensionPoints.ToolExecution;

/// <summary>
/// Runs durable audit work before the policy-only <see cref="ToolExecutionPolicy"/> gate.
/// </summary>
/// <param name="context">The validated tool-call context to audit.</param>
/// <param name="cancellationToken">The ambient turn cancellation token.</param>
/// <returns>An optional blocking audit decision.</returns>
/// <remarks>
/// The delegate owns its persistence deadline. Returning a blocking result prevents execution;
/// returning null permits policy evaluation to continue.
/// This legacy gate still combines durable service work with blocking policy authority;
/// renaming it does not separate those responsibilities.
/// </remarks>
public delegate Task<ToolExecutionDecision?> ToolAuditGate(
    ToolExecutionContext context,
    CancellationToken cancellationToken);