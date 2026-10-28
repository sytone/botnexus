namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;

/// <summary>Batch-boundary absolute stop evidence, independent of semantic progress.</summary>
public sealed record ToolResultBudgetEvidence(int Limit, long ExecutedResults, long Overshoot);
