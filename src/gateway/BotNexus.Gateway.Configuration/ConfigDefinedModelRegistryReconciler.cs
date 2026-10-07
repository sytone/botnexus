using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Projects config-defined chat models into the live model registry and replaces that complete
/// owned overlay whenever the effective platform configuration changes.
/// </summary>
public sealed class ConfigDefinedModelRegistryReconciler : IHostedService, IDisposable
{
    private const string Owner = "platform-config";
    private readonly IOptionsMonitor<PlatformConfig> _config;
    private readonly ModelRegistry _registry;
    private readonly ApiProviderRegistry? _apiProviders;
    private readonly ILogger<ConfigDefinedModelRegistryReconciler> _logger;
    private volatile IReadOnlyDictionary<string, string> _activationFailures =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _subscription;

    public ConfigDefinedModelRegistryReconciler(
        IOptionsMonitor<PlatformConfig> config,
        ModelRegistry registry,
        ILogger<ConfigDefinedModelRegistryReconciler> logger)
        : this(config, registry, apiProviders: null, logger)
    {
    }

    /// <summary>
    /// Creates the production reconciler with the execution-provider registry used to reject
    /// catalogue entries that cannot be routed by an agent turn.
    /// </summary>
    public ConfigDefinedModelRegistryReconciler(
        IOptionsMonitor<PlatformConfig> config,
        ModelRegistry registry,
        ApiProviderRegistry? apiProviders,
        ILogger<ConfigDefinedModelRegistryReconciler> logger)
    {
        _config = config;
        _registry = registry;
        _apiProviders = apiProviders;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Apply(_config.CurrentValue);
        _subscription ??= _config.OnChange((updated, _) => Apply(updated));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    /// <summary>
    /// Returns the current activation failure for a configured provider, if its latest catalogue
    /// revision was rejected. A successful later revision clears the failure.
    /// </summary>
    public string? GetActivationFailure(string providerName) =>
        _activationFailures.TryGetValue(providerName, out var failure) ? failure : null;

    private void Apply(PlatformConfig config)
    {
        try
        {
            var registrations = BuildRegistrations(config);
            _registry.ReplaceOwnedRegistrations(Owner, registrations);
            _activationFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation(
                "Reconciled {ModelCount} config-defined model registrations.",
                registrations.Count);
        }
        catch (ConfigDefinedProviderActivationException ex)
        {
            _activationFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ex.ProviderName] = ex.Message
            };
            _logger.LogError(ex, "Rejected config-defined model catalogue; retaining last-known-good registrations.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rejected config-defined model catalogue; retaining last-known-good registrations.");
        }
    }

    internal IReadOnlyList<ModelRegistration> BuildRegistrations(PlatformConfig config)
    {
        var registrations = new List<ModelRegistration>();
        if (config.Providers is null)
            return registrations;

        foreach (var (providerName, providerConfig) in config.Providers)
        {
            if (!providerConfig.Enabled ||
                string.Equals(providerConfig.Type, "github-copilot", StringComparison.OrdinalIgnoreCase) ||
                IsLegacyCanonicalCopilot(providerName, providerConfig))
                continue;

            var apiName = string.IsNullOrWhiteSpace(providerConfig.ResolveChatApi())
                ? "openai-completions"
                : providerConfig.ResolveChatApi()!;
            if (apiName == "openai-completions" && string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
                throw new ConfigDefinedProviderActivationException(
                    providerName,
                    $"Provider '{providerName}' requires a base URL for openai-completions.");
            if (_apiProviders is not null && _apiProviders.Get(apiName) is null)
                throw new ConfigDefinedProviderActivationException(
                    providerName,
                    $"Provider '{providerName}' uses unregistered chat API '{apiName}'.");

            var modelIds = providerConfig.ResolveChatModels()?.ToList() ?? [];
            if (config.Agents is not null)
            {
                modelIds.AddRange(config.Agents.Values
                    .Where(agent => string.Equals(agent.Provider, providerName, StringComparison.OrdinalIgnoreCase))
                    .Select(agent => agent.Model)
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => model!));
            }

            foreach (var modelId in modelIds.Distinct(StringComparer.Ordinal))
            {
                var caps = DynamicModelCapabilities.Infer(
                    modelId,
                    providerConfig.ResolveChatReasoning(),
                    providerConfig.ResolveChatSupportsExtraHighThinking(),
                    providerConfig.ResolveChatSupportsExtendedContextWindow(),
                    providerConfig.ResolveChatInput());
                registrations.Add(new ModelRegistration(
                    providerName,
                    new LlmModel(
                        modelId,
                        modelId,
                        apiName,
                        providerName,
                        providerConfig.BaseUrl ?? string.Empty,
                        caps.Reasoning,
                        caps.Input,
                        new ModelCost(0, 0, 0, 0),
                        providerConfig.ResolveChatContextWindow() ?? 128_000,
                        32_000,
                        caps.SupportsExtraHighThinking,
                        caps.SupportsExtendedContextWindow)));
            }
        }

        return registrations;
    }

    private static bool IsLegacyCanonicalCopilot(string providerName, ProviderConfig providerConfig) =>
        string.Equals(providerName, "github-copilot", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(providerConfig.Type) &&
        string.IsNullOrWhiteSpace(providerConfig.BaseUrl);

    private sealed class ConfigDefinedProviderActivationException(string providerName, string message)
        : InvalidOperationException(message)
    {
        public string ProviderName { get; } = providerName;
    }
}
