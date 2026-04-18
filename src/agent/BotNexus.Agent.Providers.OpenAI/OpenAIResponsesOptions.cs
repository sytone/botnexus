namespace BotNexus.Agent.Providers.OpenAI;

/// <summary>
/// Provider-specific options for OpenAI Responses API.
/// </summary>
public record class OpenAIResponsesOptions : BotNexus.Agent.Providers.Core.StreamOptions
{
    public string? ReasoningEffort { get; set; }
    public string? ReasoningSummary { get; set; }
    public string? PreviousResponseId { get; set; }
    public string? ServiceTier { get; set; }
}
