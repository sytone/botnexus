namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>Provider request phase; finalization cannot execute tools.</summary>
public enum ToolCapacityPhase
{
    /// <summary>The ordinary provider continuation lane.</summary>
    Executing,
    /// <summary>The already-exhausted, tool-free summary lane.</summary>
    Finalizing
}
