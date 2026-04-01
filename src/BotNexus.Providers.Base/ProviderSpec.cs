namespace BotNexus.Providers.Base;

/// <summary>Metadata about a provider implementation.</summary>
public record ProviderSpec(
    string Name,
    string DisplayName,
    string DefaultModel,
    Type ProviderType);
