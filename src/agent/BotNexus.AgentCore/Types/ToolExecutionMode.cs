namespace BotNexus.AgentCore.Types;

/// <summary>
/// Defines how queued tool calls are executed within the agent loop.
/// Mirrors pi-mono tool queue execution behavior.
/// </summary>
public enum ToolExecutionMode
{
    /// <summary>
    /// Executes tool calls one after another.
    /// </summary>
    Sequential,

    /// <summary>
    /// Executes tool calls concurrently when possible.
    /// </summary>
    Parallel,
}
