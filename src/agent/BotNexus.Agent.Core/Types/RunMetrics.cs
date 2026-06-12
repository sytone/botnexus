namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Aggregate metrics for a completed agent run.
/// Accumulated across all turns during a single <see cref="AgentLoopRunner"/> execution.
/// </summary>
/// <param name="InputTokens">Total input tokens reported by the provider across all turns.</param>
/// <param name="OutputTokens">Total output tokens reported by the provider across all turns.</param>
/// <param name="TurnCount">Number of turns (LLM invocations) in this run.</param>
/// <param name="ToolCallCount">Number of tool executions completed in this run.</param>
/// <param name="Duration">Wall-clock duration from run start to end.</param>
public sealed record RunMetrics(
    long InputTokens,
    long OutputTokens,
    int TurnCount,
    int ToolCallCount,
    TimeSpan Duration);
