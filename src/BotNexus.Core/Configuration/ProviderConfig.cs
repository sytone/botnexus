namespace BotNexus.Core.Configuration;

/// <summary>Configuration for a single LLM provider.</summary>
public class ProviderConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string? ApiBase { get; set; }
    public string? DefaultModel { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 3;
}
