namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Represents a normalized tool execution payload.
/// Maps pi-mono tool result objects to strongly typed C# records.
/// </summary>
/// <param name="Content">Structured tool result content blocks (text, images, etc.).</param>
/// <param name="Details">Optional provider/tool-specific metadata (not sent to the LLM).</param>
/// <remarks>
/// Content is converted to provider ToolResultMessage and sent back to the LLM.
/// Details is preserved for logging, hooks, and application-specific use.
/// </remarks>
public record AgentToolResult(IReadOnlyList<AgentToolContent> Content, object? Details = null);
