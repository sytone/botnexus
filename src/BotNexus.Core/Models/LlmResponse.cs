namespace BotNexus.Core.Models;

/// <summary>The reason the LLM stopped generating.</summary>
public enum FinishReason { Stop, ToolCalls, Length, ContentFilter, Other }

/// <summary>Response from the LLM.</summary>
public record LlmResponse(
    string Content,
    FinishReason FinishReason,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    int? InputTokens = null,
    int? OutputTokens = null);
