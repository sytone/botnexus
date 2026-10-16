namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Evaluates whether a completed tool result establishes observable run progress.</summary>
/// <param name="context">The completed tool-call/result pair.</param>
/// <param name="cancellationToken">The active run's cancellation token.</param>
/// <returns>
/// A Progress, NoProgress, or Neutral decision, or null when the policy does not classify the result.
/// Null and Neutral neither increment nor reset known repetition evidence. Only explicit Progress,
/// changed authoritative evidence at a retained scope, or external steering resets that evidence.
/// Every completed result still counts toward the run's absolute tool-result fuse.
/// </returns>
/// <remarks>
/// The loop invokes the policy after tool execution and transcript retention, once per result in
/// result order. The policy has no execution authority and must not replay, suppress, or transform
/// a tool result. Exceptions and cancellation propagate out of policy evaluation.
/// </remarks>
public delegate Task<ToolProgressDecision?> ToolProgressPolicy(
    ToolProgressContext context,
    CancellationToken cancellationToken);
