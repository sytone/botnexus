namespace BotNexus.Core.Configuration;

/// <summary>Configuration for all LLM providers.</summary>
public class ProvidersConfig
{
    public ProviderConfig OpenAI { get; set; } = new();
    public ProviderConfig Anthropic { get; set; } = new();
    public ProviderConfig AzureOpenAI { get; set; } = new();
}
