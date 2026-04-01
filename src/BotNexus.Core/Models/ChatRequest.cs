namespace BotNexus.Core.Models;

/// <summary>A single message in a chat conversation.</summary>
public record ChatMessage(string Role, string Content);

/// <summary>Request to the LLM for a chat completion.</summary>
public record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    GenerationSettings Settings,
    IReadOnlyList<ToolDefinition>? Tools = null,
    string? SystemPrompt = null);
