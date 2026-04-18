namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Identifies the specific event emitted during the pi-mono style agent loop lifecycle.
/// </summary>
public enum AgentEventType
{
    /// <summary>
    /// Signals the start of an agent run.
    /// </summary>
    AgentStart,

    /// <summary>
    /// Signals the end of an agent run.
    /// </summary>
    AgentEnd,

    /// <summary>
    /// Signals the start of an agent turn.
    /// </summary>
    TurnStart,

    /// <summary>
    /// Signals the end of an agent turn.
    /// </summary>
    TurnEnd,

    /// <summary>
    /// Signals the start of assistant message production.
    /// </summary>
    MessageStart,

    /// <summary>
    /// Signals an incremental update while producing an assistant message.
    /// </summary>
    MessageUpdate,

    /// <summary>
    /// Signals completion of assistant message production.
    /// </summary>
    MessageEnd,

    /// <summary>
    /// Signals the start of a tool execution.
    /// </summary>
    ToolExecutionStart,

    /// <summary>
    /// Signals an incremental update while a tool executes.
    /// </summary>
    ToolExecutionUpdate,

    /// <summary>
    /// Signals completion of a tool execution.
    /// </summary>
    ToolExecutionEnd,
}
