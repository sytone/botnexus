using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway;

public sealed class ConfigReloadOrchestrator : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly IOptionsMonitor<BotNexusConfig> _configMonitor;
    private readonly AgentRouter _agentRouter;
    private readonly ProviderRegistry _providerRegistry;
    private readonly IReadOnlyList<ILlmProvider> _providers;
    private readonly IReadOnlyList<ExtensionServiceRegistration> _registrations;
    private readonly ICronService _cronService;
    private readonly IActivityStream _activityStream;
    private readonly ILogger<ConfigReloadOrchestrator> _logger;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _debounceSync = new();
    private BotNexusConfig _lastKnownConfig = new();
    private BotNexusConfig _pendingConfig = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _debounceCts;

    public ConfigReloadOrchestrator(
        IOptionsMonitor<BotNexusConfig> configMonitor,
        AgentRouter agentRouter,
        ProviderRegistry providerRegistry,
        IEnumerable<ILlmProvider> providers,
        IEnumerable<ExtensionServiceRegistration> registrations,
        ICronService cronService,
        IActivityStream activityStream,
        ILogger<ConfigReloadOrchestrator> logger)
    {
        _configMonitor = configMonitor;
        _agentRouter = agentRouter;
        _providerRegistry = providerRegistry;
        _providers = providers.ToList();
        _registrations = registrations.ToList();
        _cronService = cronService;
        _activityStream = activityStream;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastKnownConfig = CloneConfig(_configMonitor.CurrentValue);
        _pendingConfig = CloneConfig(_lastKnownConfig);

        _subscription = _configMonitor.OnChange((newConfig, sectionName) =>
        {
            lock (_debounceSync)
            {
                _pendingConfig = CloneConfig(newConfig);
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _ = DebounceApplyAsync(_debounceCts.Token);
            }
        });

        return Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        lock (_debounceSync)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _debounceCts?.Dispose();
        _applyGate.Dispose();
        base.Dispose();
    }

    private async Task DebounceApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            await ApplyChangeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyChangeAsync(CancellationToken cancellationToken)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _lastKnownConfig;
            var current = CloneConfig(_pendingConfig);
            var actions = new List<string>();

            var affectedAgents = GetAffectedAgents(previous, current);
            if (affectedAgents.Count > 0)
            {
                _agentRouter.ReloadFromConfig(previous, current, affectedAgents);
                var agents = string.Join(", ", affectedAgents.OrderBy(static x => x));
                _logger.LogInformation("Reloaded agent runners for: {Agents}", agents);
                actions.Add($"agents:{agents}");
            }

            var providerChanges = GetChangedProviderKeys(previous, current);
            if (providerChanges.Count > 0)
            {
                var refreshed = RefreshProviderRegistry(current);
                _logger.LogInformation(
                    "Reloaded provider registry (changed: {ChangedKeys}, active: {ActiveKeys})",
                    string.Join(", ", providerChanges.OrderBy(static x => x)),
                    string.Join(", ", refreshed.OrderBy(static x => x)));
                actions.Add($"providers:{string.Join(",", refreshed.OrderBy(static x => x))}");
            }

            if (!JsonEquivalent(previous.Cron, current.Cron))
            {
                await _cronService.ReloadFromConfigAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Reloaded cron jobs from configuration changes.");
                actions.Add("cron:reloaded");
            }

            if (!string.Equals(previous.Gateway.ApiKey, current.Gateway.ApiKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Gateway API key changed; middleware uses the updated key immediately.");
                actions.Add("gateway:api-key-updated");
            }

            if (!string.Equals(previous.Gateway.Host, current.Gateway.Host, StringComparison.OrdinalIgnoreCase) ||
                previous.Gateway.Port != current.Gateway.Port)
            {
                _logger.LogWarning("Restart required for host/port changes.");
                actions.Add("restart-required:host-port");
            }

            if (!string.Equals(previous.ExtensionsPath, current.ExtensionsPath, StringComparison.Ordinal))
            {
                _logger.LogWarning("Restart required for extension path changes.");
                actions.Add("restart-required:extensions-path");
            }

            if (actions.Count > 0)
            {
                await PublishReloadActivityAsync(actions, cancellationToken).ConfigureAwait(false);
            }

            _lastKnownConfig = current;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private IReadOnlyList<string> RefreshProviderRegistry(BotNexusConfig config)
    {
        var providersByConfiguredKey = new Dictionary<string, ILlmProvider>(StringComparer.OrdinalIgnoreCase);
        var keyedProviders = BuildKeyedProviderMap();

        foreach (var configuredKey in config.Providers.Keys)
        {
            if (keyedProviders.TryGetValue(configuredKey, out var keyedProvider))
            {
                providersByConfiguredKey[configuredKey] = keyedProvider;
                continue;
            }

            var inferred = _providers.FirstOrDefault(provider =>
                string.Equals(InferProviderKey(provider), configuredKey, StringComparison.OrdinalIgnoreCase));
            if (inferred is not null)
                providersByConfiguredKey[configuredKey] = inferred;
        }

        _providerRegistry.ReplaceAll(providersByConfiguredKey);
        return _providerRegistry.GetProviderNames();
    }

    private Dictionary<string, ILlmProvider> BuildKeyedProviderMap()
    {
        var keyedProviders = new Dictionary<string, ILlmProvider>(StringComparer.OrdinalIgnoreCase);
        var registrationKeys = _registrations
            .Where(r => r.ServiceType == typeof(ILlmProvider))
            .Select(r => r.Key)
            .ToList();

        var registrationsToApply = Math.Min(_providers.Count, registrationKeys.Count);
        for (var i = 0; i < registrationsToApply; i++)
            keyedProviders[registrationKeys[i]] = _providers[i];

        return keyedProviders;
    }

    private static IReadOnlySet<string> GetChangedProviderKeys(BotNexusConfig previous, BotNexusConfig current)
    {
        var keys = new HashSet<string>(previous.Providers.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(current.Providers.Keys);
        keys.RemoveWhere(key => JsonEquivalent(
            previous.Providers.TryGetValue(key, out var oldProvider) ? oldProvider : null,
            current.Providers.TryGetValue(key, out var newProvider) ? newProvider : null));
        return keys;
    }

    private static IReadOnlySet<string> GetAffectedAgents(BotNexusConfig previous, BotNexusConfig current)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultsChanged =
            !string.Equals(previous.Agents.Workspace, current.Agents.Workspace, StringComparison.Ordinal) ||
            !string.Equals(previous.Agents.Model, current.Agents.Model, StringComparison.Ordinal) ||
            previous.Agents.MaxTokens != current.Agents.MaxTokens ||
            previous.Agents.ContextWindowTokens != current.Agents.ContextWindowTokens ||
            previous.Agents.MaxToolIterations != current.Agents.MaxToolIterations ||
            !string.Equals(previous.Agents.Timezone, current.Agents.Timezone, StringComparison.Ordinal) ||
            Math.Abs(previous.Agents.Temperature - current.Agents.Temperature) > double.Epsilon;

        if (defaultsChanged || !JsonEquivalent(previous.Agents.Named, current.Agents.Named))
        {
            var allNames = previous.Agents.Named.Keys
                .Concat(current.Agents.Named.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in allNames)
            {
                var hadAgent = previous.Agents.Named.TryGetValue(name, out var previousAgent);
                var hasAgent = current.Agents.Named.TryGetValue(name, out var currentAgent);
                if (defaultsChanged || !hadAgent || !hasAgent || !JsonEquivalent(previousAgent, currentAgent))
                    affected.Add(name);
            }
        }

        return affected;
    }

    private async Task PublishReloadActivityAsync(IReadOnlyList<string> actions, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _activityStream.PublishAsync(new ActivityEvent(
            ActivityEventType.AgentCompleted,
            "gateway",
            "config:reload",
            ChatId: null,
            SenderId: "config-reload",
            Content: string.Join("; ", actions),
            Timestamp: now,
            Metadata: new Dictionary<string, object>
            {
                ["event"] = "gateway.config.reloaded",
                ["actions"] = actions,
                ["changed_at"] = now.ToString("O")
            }), cancellationToken).ConfigureAwait(false);
    }

    private static bool JsonEquivalent<T>(T left, T right)
        => string.Equals(
            JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);

    private static BotNexusConfig CloneConfig(BotNexusConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<BotNexusConfig>(json, JsonOptions) ?? new BotNexusConfig();
    }

    private static string InferProviderKey(ILlmProvider provider)
    {
        var ns = provider.GetType().Namespace;
        if (!string.IsNullOrWhiteSpace(ns))
        {
            const string marker = ".Providers.";
            var start = ns.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var segment = ns[(start + marker.Length)..].Split('.')[0];
                if (!string.IsNullOrWhiteSpace(segment))
                    return segment.ToLowerInvariant();
            }
        }

        return provider.GetType().Name
            .Replace("Provider", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
    }
}
