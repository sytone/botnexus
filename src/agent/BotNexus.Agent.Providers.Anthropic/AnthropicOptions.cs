namespace BotNexus.Agent.Providers.Anthropic;

/// <summary>
/// Anthropic-specific streaming options extending the base StreamOptions.
/// </summary>
public record class AnthropicOptions : Core.StreamOptions
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
    /// Tool choice: "auto", "any", "none", a specific tool name,
    /// or an Anthropic tool_choice object.
    /// </summary>
    public object? ToolChoice { get; set; }
}
