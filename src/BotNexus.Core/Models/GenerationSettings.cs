namespace BotNexus.Core.Models;

/// <summary>Generation settings for LLM requests.</summary>
public class GenerationSettings
{
    public string Model { get; set; } = "gpt-4o";
    public int MaxTokens { get; set; } = 8192;
    public double Temperature { get; set; } = 0.1;
    public int ContextWindowTokens { get; set; } = 65536;
    public int MaxToolIterations { get; set; } = 40;
}
