namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Once-only crossings of the run's executed-result budget.</summary>
public enum ToolCapacityStage
{
    /// <summary>The run starts with no executed results.</summary>
    Start,
    /// <summary>At least 75 percent of the configured limit has been consumed.</summary>
    SeventyFivePercent,
    /// <summary>At least 90 percent of the configured limit has been consumed.</summary>
    NinetyPercent,
    /// <summary>The full batch reached or exceeded the hard limit.</summary>
    Exhausted
}
