namespace BotNexus.Agent.Providers.Core.Models;

/// <summary>
/// LLM request context containing system prompt, messages, and optional tools.
/// </summary>
public record Context(
    string? SystemPrompt,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<Tool>? Tools = null
);
