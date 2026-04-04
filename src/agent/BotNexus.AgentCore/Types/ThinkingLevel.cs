namespace BotNexus.AgentCore.Types;

/// <summary>
/// Controls how much reasoning effort the agent should request from the backing model.
/// Mirrors pi-mono thinking intensity levels.
/// </summary>
public enum ThinkingLevel
{
    /// <summary>
    /// Disables explicit thinking behavior.
    /// </summary>
    Off,

    /// <summary>
    /// Applies minimal additional reasoning.
    /// </summary>
    Minimal,

    /// <summary>
    /// Applies low reasoning effort.
    /// </summary>
    Low,

    /// <summary>
    /// Applies medium reasoning effort.
    /// </summary>
    Medium,

    /// <summary>
    /// Applies high reasoning effort.
    /// </summary>
    High,

    /// <summary>
    /// Applies the maximum reasoning effort.
    /// </summary>
    ExtraHigh,
}
