using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

internal sealed class AgentConfigurationHostedService(
    IEnumerable<IAgentConfigurationSource> sources,
    IAgentRegistry registry,
    ILogger<AgentConfigurationHostedService> logger) : IHostedService, IDisposable
{
    private readonly IAgentConfigurationSource[] _sources = sources.ToArray();
    private readonly IAgentRegistry _registry = registry;
    private readonly ILogger<AgentConfigurationHostedService> _logger = logger;
    private readonly Lock _sync = new();
    private readonly List<IDisposable> _watchers = [];
    private readonly Dictionary<IAgentConfigurationSource, IReadOnlyList<AgentDescriptor>> _latestSourceDescriptors = [];
    private readonly Dictionary<string, AgentDescriptor> _appliedConfigDescriptors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _codeBasedAgentIds = new(StringComparer.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _codeBasedAgentIds = _registry.GetAll()
            .Select(descriptor => descriptor.AgentId.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            IReadOnlyList<AgentDescriptor> descriptors;
            try
            {
                descriptors = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load agent descriptors from source '{SourceType}'.", source.GetType().Name);
                continue;
            }

            lock (_sync)
            {
                _latestSourceDescriptors[source] = descriptors;
                ApplyMergedDescriptors();
            }
        }

        foreach (var source in _sources)
        {
            var watcher = source.Watch(descriptors => OnSourceChanged(source, descriptors));
            if (watcher is not null)
                _watchers.Add(watcher);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatchers();
        return Task.CompletedTask;
    }

    public void Dispose()
        => DisposeWatchers();

    private void OnSourceChanged(IAgentConfigurationSource source, IReadOnlyList<AgentDescriptor> descriptors)
    {
        lock (_sync)
        {
            _latestSourceDescriptors[source] = descriptors;
            ApplyMergedDescriptors();
        }
    }

    private void ApplyMergedDescriptors()
    {
        Dictionary<string, AgentDescriptor> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
        {
            if (!_latestSourceDescriptors.TryGetValue(source, out var descriptors))
                continue;

            HashSet<string> seenSourceIds = new(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in descriptors)
            {
                if (!seenSourceIds.Add(descriptor.AgentId))
                {
                    _logger.LogWarning(
                        "Agent '{AgentId}' is duplicated within source '{SourceType}'. Using the first occurrence.",
                        descriptor.AgentId,
                        source.GetType().Name);
                    continue;
                }

                if (_codeBasedAgentIds.Contains(descriptor.AgentId))
                {
                    _logger.LogDebug(
                        "Config-based agent '{AgentId}' is shadowed by code-based registration.",
                        descriptor.AgentId);
                    continue;
                }

                if (!merged.TryAdd(descriptor.AgentId, descriptor))
                {
                    _logger.LogWarning(
                        "Config-based agent '{AgentId}' from source '{SourceType}' is shadowed by an earlier source.",
                        descriptor.AgentId,
                        source.GetType().Name);
                }
            }
        }

        var removedIds = _appliedConfigDescriptors.Keys
            .Except(merged.Keys, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var removedId in removedIds)
        {
            var typedRemovedId = AgentId.From(removedId);
            if (_registry.Contains(typedRemovedId))
                _registry.Unregister(typedRemovedId);

            _appliedConfigDescriptors.Remove(removedId);
        }

        foreach (var (agentId, descriptor) in merged)
        {
            if (_appliedConfigDescriptors.TryGetValue(agentId, out var existingDescriptor))
            {
                if (existingDescriptor == descriptor)
                    continue;

                var typedAgentId = AgentId.From(agentId);
                if (_registry.Contains(typedAgentId))
                    _registry.Unregister(typedAgentId);

                _registry.Register(descriptor);
                _appliedConfigDescriptors[agentId] = descriptor;
                continue;
            }

            var typedId = AgentId.From(agentId);
            if (_registry.Contains(typedId))
            {
                _logger.LogWarning(
                    "Skipping config-based agent '{AgentId}' because it is already registered by a non-config source.",
                    agentId);
                continue;
            }

            _registry.Register(descriptor);
            _appliedConfigDescriptors[agentId] = descriptor;
        }
    }

    private void DisposeWatchers()
    {
        lock (_sync)
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();

            _watchers.Clear();
        }
    }
}
