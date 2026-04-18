namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Defines how queued tool calls are executed within the agent loop.
/// Mirrors pi-mono tool queue execution behavior.
/// </summary>
/// <remarks>
/// Sequential mode ensures deterministic execution order and is safer for tools with side effects.
/// Parallel mode improves throughput for independent read-only tools.
/// </remarks>
public enum ToolExecutionMode
{
    /// <summary>
    /// Executes tool calls one after another in assistant message order.
    /// </summary>
    /// <remarks>
    /// Each tool is prepared, executed, and finalized before the next one starts.
    /// Use when tools have dependencies or shared state.
    /// </remarks>
    Sequential,

    /// <summary>
    /// Executes tool calls concurrently when possible.
    /// </summary>
    /// <remarks>
    /// Tool calls are prepared sequentially, then allowed tools execute concurrently.
    /// Final tool results are still emitted in assistant source order.
    /// Use when tools are independent and thread-safe.
    /// </remarks>
    Parallel,
}
