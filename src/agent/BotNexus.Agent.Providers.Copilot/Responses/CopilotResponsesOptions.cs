using BotNexus.Agent.Providers.Core.Utilities;

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

    /// <summary>
    /// Optional Copilot-CLI-fidelity request headers
    /// (Copilot-Integration-Id, X-GitHub-Api-Version, Editor-Version,
    /// X-Interaction-Id, intent override). When null, only the default
    /// dynamic header set is emitted — preserving wire parity with previous releases.
    /// </summary>
    public CopilotHeaderOptions? HeaderOptions { get; set; }

    /// <summary>
    /// Provider-private diagnostic override for Copilot Responses transport. Auto honors the
    /// capability advertised by model discovery; the agent loop and gateway never select this.
    /// </summary>
    internal CopilotResponsesTransportPreference TransportPreference { get; init; } = CopilotResponsesTransportPreference.Auto;
}
