namespace BotNexus.Providers.Anthropic;

/// <summary>
/// Anthropic-specific streaming options extending the base StreamOptions.
/// </summary>
public class AnthropicOptions : Core.StreamOptions
{
    public bool? ThinkingEnabled { get; set; }
    public int? ThinkingBudgetTokens { get; set; }

    /// <summary>
    /// Adaptive thinking effort level: "low", "medium", "high", "max".
    /// Used with Opus 4.6 and Sonnet 4.6 models.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Enable interleaved thinking (thinking blocks between text/tool blocks).
    /// </summary>
    public bool InterleavedThinking { get; set; } = true;

    /// <summary>
    /// Tool choice: "auto", "any", "none", or a specific tool name.
    /// </summary>
    public string? ToolChoice { get; set; }
}
