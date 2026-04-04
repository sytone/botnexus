namespace BotNexus.AgentCore.Types;

/// <summary>
/// Represents the runtime status of an agent execution loop.
/// </summary>
public enum AgentStatus
{
    /// <summary>
    /// The agent is not currently processing a turn.
    /// </summary>
    Idle,

    /// <summary>
    /// The agent is currently processing turns and/or tools.
    /// </summary>
    Running,

    /// <summary>
    /// The agent is in the process of aborting execution.
    /// </summary>
    Aborting,
}
