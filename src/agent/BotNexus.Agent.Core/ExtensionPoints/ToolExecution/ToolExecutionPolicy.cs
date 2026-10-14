namespace BotNexus.Agent.Core.ExtensionPoints.ToolExecution;

/// <summary>
/// Evaluates whether a validated tool call may execute.
/// </summary>
/// <param name="context">The tool-execution policy context.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>
/// A decision that permits execution only when <see cref="ToolExecutionDecision.IsUnambiguousAllow"/>
/// is true, or null for the historical no-opinion allow.
/// </returns>
/// <remarks>
/// Ordinary exceptions block the tool call; ambient cancellation propagates.
/// The configured cooperative timeout fails closed when cancellation is observed or a decision
/// arrives after the budget, subject to the executor's host-suspend adjustment. Cancellation
/// requests do not forcibly interrupt a callback that ignores its token.
/// </remarks>
public delegate Task<ToolExecutionDecision?> ToolExecutionPolicy(
    ToolExecutionContext context,
    CancellationToken cancellationToken);