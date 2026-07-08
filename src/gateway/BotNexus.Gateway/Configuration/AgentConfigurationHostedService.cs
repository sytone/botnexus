using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Watches <see cref="IAgentConfigurationSource"/> instances for changes and synchronizes
/// the <see cref="IAgentRegistry"/> accordingly.
/// <para>
/// Change notifications are debounced: rapid-fire events (e.g., from FileSystemWatcher
/// spurious triggers or IOptionsMonitor re-binding) are coalesced into a single registry
/// update after a quiet period of <see cref="DebounceDelay"/>.
/// </para>
/// </summary>
internal sealed class AgentConfigurationHostedService(
    IEnumerable<IAgentConfigurationSource> sources,
    IAgentRegistry registry,
    ILogger<AgentConfigurationHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// Duration to wait after the last change notification before applying registry updates.
    /// This coalesces rapid-fire FileSystemWatcher events into a single reload.
    /// </summary>
    internal static TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly IAgentConfigurationSource[] _sources = sources.ToArray();
    private readonly IAgentRegistry _registry = registry;
    private readonly ILogger<AgentConfigurationHostedService> _logger = logger;
    private readonly Lock _sync = new();
    private readonly List<IDisposable> _watchers = [];
    private readonly Dictionary<IAgentConfigurationSource, IReadOnlyList<AgentDescriptor>> _latestSourceDescriptors = [];
    private readonly Dictionary<string, AgentDescriptor> _appliedConfigDescriptors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _codeBasedAgentIds = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _debounceCts;
    private int _suppressedNotifications;

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
        CancelDebounce();
        DisposeWatchers();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        CancelDebounce();
        DisposeWatchers();
    }

    private void OnSourceChanged(IAgentConfigurationSource source, IReadOnlyList<AgentDescriptor> descriptors)
    {
        lock (_sync)
        {
            _latestSourceDescriptors[source] = descriptors;
            ScheduleDebouncedApply();
        }
    }

    /// <summary>
    /// Resets the debounce timer. When the timer fires after <see cref="DebounceDelay"/> of
    /// inactivity, <see cref="ApplyMergedDescriptors"/> runs once with the latest state.
    /// </summary>
    private void ScheduleDebouncedApply()
    {
        // Cancel any pending debounce timer
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        _suppressedNotifications++;

        var cts = _debounceCts;
        _ = Task.Delay(DebounceDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;

            lock (_sync)
            {
                var suppressed = _suppressedNotifications;
                _suppressedNotifications = 0;

                if (suppressed > 1)
                {
                    _logger.LogInformation(
                        "Agent configuration reload: coalesced {SuppressedCount} notifications into single apply.",
                        suppressed);
                }

                ApplyMergedDescriptors();
            }
        }, TaskScheduler.Default);
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
                if (!seenSourceIds.Add(descriptor.AgentId.Value))
                {
                    _logger.LogWarning(
                        "Agent '{AgentId}' is duplicated within source '{SourceType}'. Using the first occurrence.",
                        descriptor.AgentId.Value,
                        source.GetType().Name);
                    continue;
                }

                if (_codeBasedAgentIds.Contains(descriptor.AgentId.Value))
                {
                    _logger.LogDebug(
                        "Config-based agent '{AgentId}' is shadowed by code-based registration.",
                        descriptor.AgentId.Value);
                    continue;
                }

                if (!merged.TryAdd(descriptor.AgentId.Value, descriptor))
                {
                    _logger.LogWarning(
                        "Config-based agent '{AgentId}' from source '{SourceType}' is shadowed by an earlier source.",
                        descriptor.AgentId.Value,
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
            _logger.LogInformation("Removed agent '{AgentId}' (no longer in config sources).", removedId);
        }

        int added = 0, updated = 0, unchanged = 0;
        foreach (var (agentId, descriptor) in merged)
        {
            if (_appliedConfigDescriptors.TryGetValue(agentId, out var existingDescriptor))
            {
                if (DescriptorsEqual(existingDescriptor, descriptor))
                {
                    unchanged++;
                    continue;
                }

                var typedAgentId = AgentId.From(agentId);
                if (_registry.Contains(typedAgentId))
                    _registry.Unregister(typedAgentId);

                _registry.Register(descriptor);
                _appliedConfigDescriptors[agentId] = descriptor;
                updated++;
                _logger.LogInformation("Updated agent '{AgentId}' registration (config changed).", agentId);
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
            added++;
        }

        if (added > 0 || updated > 0 || removedIds.Length > 0)
        {
            _logger.LogInformation(
                "Agent configuration applied: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged.",
                added, updated, removedIds.Length, unchanged);
        }
        else if (unchanged > 0)
        {
            _logger.LogDebug(
                "Agent configuration reload: no changes detected ({Unchanged} agents unchanged).",
                unchanged);
        }
    }

    /// <summary>
    /// Compares two descriptors for semantic equality. Records use value equality for
    /// value-type properties and reference equality for collections, so we need a
    /// deeper comparison for the collection properties that matter.
    /// </summary>
    private static bool DescriptorsEqual(AgentDescriptor a, AgentDescriptor b)
    {
        // Record equality handles simple value types, but we need to verify it's correct
        // for our case since collections (arrays, dictionaries) use reference equality.
        // However, since PlatformConfigAgentSource creates fresh instances on every load,
        // record == will always be false even when nothing changed.
        // We compare the fields that are most likely to change during config reload.

        if (a.AgentId != b.AgentId) return false;
        if (a.DisplayName != b.DisplayName) return false;
        if (a.ModelId != b.ModelId) return false;
        if (a.ApiProvider != b.ApiProvider) return false;
        if (a.Emoji != b.Emoji) return false;
        if (a.Description != b.Description) return false;
        if (a.SystemPromptFile != b.SystemPromptFile) return false;
        if (a.IsolationStrategy != b.IsolationStrategy) return false;
        if (a.CacheRetentionMode != b.CacheRetentionMode) return false;
        if (a.Thinking != b.Thinking) return false;
        if (a.ContextWindow != b.ContextWindow) return false;
        if (a.MaxConcurrentSessions != b.MaxConcurrentSessions) return false;
        if (a.SessionAccessLevel != b.SessionAccessLevel) return false;
        if (a.ConversationAccessLevel != b.ConversationAccessLevel) return false;
        if (a.Kind != b.Kind) return false;
        if (!SequenceEqual(a.ToolIds, b.ToolIds)) return false;
        if (!SequenceEqual(a.AllowedModelIds, b.AllowedModelIds)) return false;
        if (!SequenceEqual(a.SubAgentIds, b.SubAgentIds)) return false;
        if (!SequenceEqual(a.SubAgentRoles, b.SubAgentRoles)) return false;
        if (!SequenceEqual(a.SystemPromptFiles, b.SystemPromptFiles)) return false;
        if (!SequenceEqual(a.SessionAllowedAgents, b.SessionAllowedAgents)) return false;
        if (!SequenceEqual(a.ConversationAllowedAgents, b.ConversationAllowedAgents)) return false;
        if (!ShellCommandEqual(a.ShellCommand, b.ShellCommand)) return false;

        return true;
    }

    private static bool SequenceEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool ShellCommandEqual(string[]? a, string[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void CancelDebounce()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
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
