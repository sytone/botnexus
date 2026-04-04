namespace BotNexus.AgentCore.Types;

/// <summary>
/// Represents a normalized tool execution payload.
/// Maps pi-mono tool result objects to strongly typed C# records.
/// </summary>
/// <param name="Content">Structured tool result content blocks.</param>
/// <param name="Details">Optional provider/tool-specific metadata.</param>
public record AgentToolResult(IReadOnlyList<AgentToolContent> Content, object? Details = null);
