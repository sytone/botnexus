using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.MicrosoftFoundry;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Composes configured Microsoft Foundry instances into the shared provider and model registries.
/// </summary>
public static class MicrosoftFoundryProviderComposition
{
    private const string ProviderType = "microsoft-foundry";
    private const string ProviderApi = "microsoft-foundry-responses";

    /// <summary>
    /// Registers every enabled Microsoft Foundry instance from the effective platform configuration.
    /// </summary>
    public static int Register(
        PlatformConfig config,
        ApiProviderRegistry apiProviders,
        ModelRegistry models,
        ILoggerFactory loggerFactory,
        ISecretRedactor? secretRedactor = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(apiProviders);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var instances = new List<MicrosoftFoundryResponsesInstance>();
        var registrations = new List<ModelRegistration>();
        if (config.Providers is null)
            return 0;

        foreach (var (providerName, providerConfig) in config.Providers)
        {
            if (!providerConfig.Enabled ||
                !string.Equals(providerConfig.Type, ProviderType, StringComparison.OrdinalIgnoreCase))
                continue;

            var endpoint = ValidateInferenceEndpoint(providerName, providerConfig);
            var authentication = CreateAuthentication(providerName, providerConfig);
            instances.Add(new MicrosoftFoundryResponsesInstance(providerName, endpoint, authentication));

            foreach (var modelId in ResolveModels(config, providerName, providerConfig))
            {
                var caps = DynamicModelCapabilities.Infer(
                    modelId,
                    providerConfig.ResolveChatReasoning(),
                    providerConfig.ResolveChatSupportsExtraHighThinking(),
                    providerConfig.ResolveChatSupportsExtendedContextWindow(),
                    providerConfig.ResolveChatInput());
                var capacity = ConfiguredModelCapacityResolver.Resolve(providerName, providerConfig, modelId);
                registrations.Add(new ModelRegistration(providerName, new LlmModel(
                    modelId,
                    modelId,
                    ProviderApi,
                    providerName,
                    endpoint.AbsoluteUri.TrimEnd('/'),
                    caps.Reasoning,
                    caps.Input,
                    new ModelCost(0, 0, 0, 0),
                    capacity.ContextWindow,
                    capacity.MaxTokens,
                    caps.SupportsExtraHighThinking,
                    caps.SupportsExtendedContextWindow,
                    ContextWindowSource: capacity.ContextWindowSource,
                    MaxTokensSource: capacity.MaxTokensSource)));
            }
        }

        if (instances.Count == 0)
            return 0;

        foreach (var registration in registrations)
            models.Register(registration.Provider, registration.Model);

        apiProviders.Register(new MicrosoftFoundryResponsesProvider(
            instances,
            loggerFactory.CreateLogger<MicrosoftFoundryResponsesProvider>(),
            secretRedactor));
        return instances.Count;
    }

    private static Uri ValidateInferenceEndpoint(string providerName, ProviderConfig config)
    {
        if (!string.Equals(config.ResolveChatApi(), ProviderApi, StringComparison.Ordinal))
            throw new InvalidOperationException($"Microsoft Foundry provider '{providerName}' must use chat API '{ProviderApi}'.");
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length > 0 ||
            endpoint.Query.Length > 0 ||
            endpoint.Fragment.Length > 0 ||
            !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/openai/v1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Microsoft Foundry provider '{providerName}' requires an HTTPS inference endpoint ending in /openai/v1.");
        }

        return endpoint;
    }

    private static MicrosoftFoundryAuthentication CreateAuthentication(string providerName, ProviderConfig config)
    {
        var type = config.Authentication?.Type?.Trim().ToLowerInvariant();
        return type switch
        {
            "entra-default" => MicrosoftFoundryAuthentication.DefaultAzureCredential(),
            "managed-identity" => MicrosoftFoundryAuthentication.SystemAssignedManagedIdentity(),
            "user-assigned-managed-identity" when !string.IsNullOrWhiteSpace(config.Authentication?.ClientId) =>
                MicrosoftFoundryAuthentication.UserAssignedManagedIdentity(config.Authentication.ClientId),
            "user-assigned-managed-identity" => throw new InvalidOperationException(
                $"Microsoft Foundry provider '{providerName}' requires authentication.clientId for user-assigned managed identity."),
            "api-key" when !string.IsNullOrWhiteSpace(config.ApiKey) => MicrosoftFoundryAuthentication.ApiKey(config.ApiKey),
            "api-key" => throw new InvalidOperationException(
                $"Microsoft Foundry provider '{providerName}' requires apiKey for API-key authentication."),
            _ => throw new InvalidOperationException(
                $"Microsoft Foundry provider '{providerName}' has unsupported authentication type '{config.Authentication?.Type}'.")
        };
    }

    private static IEnumerable<string> ResolveModels(
        PlatformConfig config,
        string providerName,
        ProviderConfig providerConfig)
    {
        var modelIds = providerConfig.ResolveChatModels()?.ToList() ?? [];
        if (config.Agents is not null)
        {
            modelIds.AddRange(config.Agents.Values
                .Where(agent => string.Equals(agent.Provider, providerName, StringComparison.OrdinalIgnoreCase))
                .Select(agent => agent.Model)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model!));
        }

        return modelIds.Distinct(StringComparer.Ordinal);
    }
}
