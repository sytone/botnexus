using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Immutable scalar projection of the guard's existing run-local accounting; not a lease or token budget.</summary>
/// <param name="AgentRunId">Identity of the owning execution, never a session or provider attempt.</param>
/// <param name="Used">All executed results, including errors and batch overshoot.</param>
/// <param name="Limit">The validated configured executed-result limit.</param>
/// <param name="Phase">Whether this request is executing or finalizing.</param>
public sealed record ToolCapacitySnapshot(AgentRunId AgentRunId, long Used, int Limit, ToolCapacityPhase Phase)
{
    /// <summary>Non-negative results left before the hard edge; overshoot never becomes negative capacity.</summary>
    public long Remaining => Math.Max(0L, Limit - Used);
}
