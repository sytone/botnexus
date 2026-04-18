namespace BotNexus.Agent.Providers.OpenAICompat;

/// <summary>
/// Options specific to OpenAI-compatible providers.
/// </summary>
public record class OpenAICompatOptions : BotNexus.Agent.Providers.Core.StreamOptions
{
    public string? ToolChoice { get; set; }
    public string? ReasoningEffort { get; set; }
}
