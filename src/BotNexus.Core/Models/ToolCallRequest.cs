namespace BotNexus.Core.Models;

/// <summary>A tool call requested by the LLM.</summary>
public record ToolCallRequest(
    string Id,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments);
