using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Messages;

/// <summary>
/// Copilot-Messages streaming options. Mirrors the Anthropic Messages options
/// but is owned by the Copilot provider so the Copilot carve-out has no
/// dependency on the Anthropic project.
/// </summary>
public record class CopilotMessagesOptions : Core.StreamOptions
{
    public bool? ThinkingEnabled { get; set; }
    public int? ThinkingBudgetTokens { get; set; }

    /// <summary>
    /// Adaptive thinking effort level: "low", "medium", "high", "max".
    /// Used with Opus 4.6/4.8 and Sonnet 4.6 adaptive-thinking models served via GitHub Copilot.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Enable interleaved thinking (thinking blocks between text/tool blocks).
    /// </summary>
    public bool InterleavedThinking { get; set; } = true;

    /// <summary>
    /// Tool choice: "auto", "any", "none", a specific tool name,
    /// or an Anthropic-shaped tool_choice object.
    /// </summary>
    public object? ToolChoice { get; set; }

    /// <summary>
    /// Optional Copilot-CLI-fidelity request headers
    /// (Copilot-Integration-Id, X-GitHub-Api-Version, Editor-Version,
    /// X-Interaction-Id, intent override). When null, only the default
    /// dynamic header set (X-Initiator, Openai-Intent, Copilot-Vision-Request)
    /// is emitted — preserving wire parity with previous releases.
    /// </summary>
    public CopilotHeaderOptions? HeaderOptions { get; set; }
}
