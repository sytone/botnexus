namespace BotNexus.Agent.Providers.Copilot.Responses;

/// <summary>
/// Provider-specific options for the GitHub Copilot Responses API.
/// Carved out of <c>OpenAIResponsesOptions</c> so the Copilot transport has no
/// cross-provider dependency on the OpenAI project. Field shape matches
/// <c>OpenAIResponsesOptions</c> intentionally — the carve-out preserves the
/// wire contract while removing the cross-provider coupling.
/// </summary>
public record class CopilotResponsesOptions : BotNexus.Agent.Providers.Core.StreamOptions
{
    public string? ReasoningEffort { get; set; }
    public string? ReasoningSummary { get; set; }
    public string? PreviousResponseId { get; set; }
    public string? ServiceTier { get; set; }
}
