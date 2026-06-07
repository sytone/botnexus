using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Completions;

/// <summary>
/// Provider-specific options for the GitHub Copilot Chat Completions API.
/// Carved out of <c>OpenAICompletionsOptions</c> so the Copilot transport has
/// no cross-provider dependency on the OpenAI project. Field shape matches
/// <c>OpenAICompletionsOptions</c> intentionally — the carve-out preserves the
/// wire contract while removing the cross-provider coupling.
/// </summary>
public record class CopilotCompletionsOptions : BotNexus.Agent.Providers.Core.StreamOptions
{
    /// <summary>
    /// Tool choice mode: "auto", "none", "required".
    /// </summary>
    public string? ToolChoice { get; set; }

    /// <summary>
    /// Reasoning effort for reasoning-capable models: "low", "medium", "high".
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Optional Copilot-CLI-fidelity request headers
    /// (Copilot-Integration-Id, X-GitHub-Api-Version, Editor-Version,
    /// X-Interaction-Id, intent override). When null, only the default
    /// dynamic header set is emitted — preserving wire parity with previous releases.
    /// </summary>
    public CopilotHeaderOptions? HeaderOptions { get; set; }
}
