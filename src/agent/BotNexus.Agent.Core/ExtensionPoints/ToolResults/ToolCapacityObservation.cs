namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Informational crossing captured after authoritative accounting; contains no tool payload.</summary>
/// <param name="Stage">The once-only milestone, in ascending order even when a batch crosses several.</param>
/// <param name="Capacity">Actual full-batch accounting at the crossing, not an interpolated threshold count.</param>
public sealed record ToolCapacityObservation(ToolCapacityStage Stage, ToolCapacitySnapshot Capacity);
