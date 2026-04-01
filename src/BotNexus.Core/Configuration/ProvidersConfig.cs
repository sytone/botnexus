namespace BotNexus.Core.Configuration;

/// <summary>Configuration for all LLM providers.</summary>
public class ProvidersConfig : Dictionary<string, ProviderConfig>
{
    public ProvidersConfig()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}
