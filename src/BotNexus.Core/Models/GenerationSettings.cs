namespace BotNexus.Core.Models;

/// <summary>Generation settings for LLM requests.</summary>
public class GenerationSettings
{
    public string? Model { get; set; }
    public int? MaxTokens { get; set; }
    public double? Temperature { get; set; }
    public int? ContextWindowTokens { get; set; }
    public int MaxToolIterations { get; set; } = 40;
    public int MaxRepeatedToolCalls { get; set; } = 2;
}
