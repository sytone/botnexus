namespace BotNexus.Gateway.Configuration;

/// <summary>Resolved token capacities and their declaration origins, not verification claims.</summary>
public sealed record ConfiguredModelCapacity(
    int ContextWindow,
    int MaxTokens,
    string ContextWindowSource,
    string MaxTokensSource);

/// <summary>Shared resolution for config-defined chat model registrations.</summary>
public static class ConfiguredModelCapacityResolver
{
    /// <summary>
    /// Resolves each field independently. Context inherits model, provider, then 128000; output
    /// inherits model, then 32000 capped below context. Invalid effective capacities reject the
    /// named provider catalogue without changing its last-known-good registrations.
    /// </summary>
    public static ConfiguredModelCapacity Resolve(string providerName, ProviderConfig provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ProviderModelCapacityConfig? model = null;
        provider.Chat?.ModelCapacities.TryGetValue(modelId, out model);
        var providerContext = provider.ResolveChatContextWindow();
        var context = model?.ContextWindow ?? providerContext ?? 128_000;
        if (context <= 1)
            throw Invalid(providerName, modelId, "contextWindow must be greater than 1 to allow positive output below context");

        var output = model?.MaxTokens ?? Math.Min(32_000, context - 1);
        if (output <= 0 || output >= context)
            throw Invalid(providerName, modelId, "maxTokens must be positive and less than contextWindow");

        return new ConfiguredModelCapacity(
            context,
            output,
            model?.ContextWindow is not null ? "configured-model" : providerContext is not null ? "configured-provider" : "fallback",
            model?.MaxTokens is not null ? "configured-model" : "fallback");
    }

    private static ConfigDefinedProviderActivationException Invalid(string provider, string model, string reason) =>
        new(provider, $"Provider '{provider}' model '{model}' has invalid capacity: {reason}.");
}

internal sealed class ConfigDefinedProviderActivationException(string providerName, string message)
    : InvalidOperationException(message)
{
    public string ProviderName { get; } = providerName;
}
