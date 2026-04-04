namespace BotNexus.Providers.OpenAI;

/// <summary>
/// Provider-specific options for OpenAI Chat Completions API.
/// Extends StreamOptions with tool choice and reasoning control.
/// </summary>
public class OpenAICompletionsOptions : BotNexus.Providers.Core.StreamOptions
{
    /// <summary>
    /// Tool choice mode: "auto", "none", "required".
    /// </summary>
    public string? ToolChoice { get; set; }

    /// <summary>
    /// Reasoning effort for reasoning-capable models: "low", "medium", "high".
    /// </summary>
    public string? ReasoningEffort { get; set; }
}
