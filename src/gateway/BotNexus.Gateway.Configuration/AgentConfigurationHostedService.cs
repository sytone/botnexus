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
/// update after a quiet period.
/// </para>
/// </summary>
internal sealed class AgentConfigurationHostedService(
    IEnumerable<IAgentConfigurationSource> sources,
    IAgentRegistry registry,
    ILogger<AgentConfigurationHostedService> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly IAgentConfigurationSource[] _sources = sources.ToArray();
    private readonly IAgentRegistry _registry = registry;
    private readonly ILogger<AgentConfigurationHostedService> _logger = logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Lock _sync = new();
    private readonly List<IDisposable> _watchers = [];
    private readonly Dictionary<IAgentConfigurationSource, IReadOnlyList<AgentDescriptor>> _latestSourceDescriptors = [];
    private readonly Dictionary<string, AgentDescriptor> _appliedConfigDescriptors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _codeBasedAgentIds = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _debounceCts;
    private Task _debounceTask = Task.CompletedTask;
    private int _suppressedNotifications;

    internal Task PendingDebounceTask => _debounceTask;

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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelDebounce();
        await _debounceTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        DisposeWatchers();
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
    /// Resets the debounce timer. When the timer fires after the configured quiet period,
    /// inactivity, <see cref="ApplyMergedDescriptors"/> runs once with the latest state.
    /// </summary>
    private void ScheduleDebouncedApply()
    {
        // Cancel any pending debounce timer
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        _suppressedNotifications++;

        _debounceTask = ApplyAfterDebounceAsync(_debounceCts.Token);
    }

    private async Task ApplyAfterDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _delay(DebounceDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

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

        int added = 0, updated = 0, unchanged = 0, adopted = 0;
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
                // The id is in the registry but this reload has never applied it. Two very different
                // situations reach here and they must not share a diagnosis (#3561):
                //
                //  a) The ordinary create path. POST /api/agents registers the descriptor AND writes it
                //     to config in one operation; the reload watcher then observes it ~2s later. Nothing
                //     is shadowing anything - this reload is simply the first to see an agent that config
                //     already owns. Adopt it so config remains the owner of record, otherwise a later
                //     config-driven EDIT re-takes this first-seen branch instead of the update branch and
                //     the edit is silently dropped.
                //
                //  b) Genuine shadowing. Something registered a differently-shaped descriptor under this
                //     id, so config edits for this agent will not take effect. That is worth a warning.
                //
                // Only (b) gets the warning, and it names the observation (a shape mismatch) rather than
                // inferring a "non-config source" that, in case (a), does not exist.
                var registeredDescriptor = _registry.Get(typedId);
                if (registeredDescriptor is not null && DescriptorsEqual(registeredDescriptor, descriptor))
                {
                    _appliedConfigDescriptors[agentId] = descriptor;
                    adopted++;
                    _logger.LogDebug(
                        "Adopting config-based agent '{AgentId}': it is already registered with an equivalent descriptor from an earlier write.",
                        agentId);
                    continue;
                }

                _logger.LogWarning(
                    "Config-based agent '{AgentId}' is already registered with a different descriptor, so config changes for this agent are not being applied. Unregister the shadowing registration or reconcile it with the config entry.",
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
        else if (unchanged > 0 || adopted > 0)
        {
            _logger.LogDebug(
                "Agent configuration reload: no changes detected ({Unchanged} agents unchanged, {Adopted} adopted).",
                unchanged,
                adopted);
        }
    }

    /// <summary>
    /// Compares two descriptors for semantic equality. Record equality is unusable here because
    /// the config sources mint fresh instances (and collection properties compare by reference)
    /// on every load, so <c>==</c> would report "changed" on every reload.
    /// <para>
    /// Delegates to <see cref="AgentDescriptorFingerprint"/> - the same primitive the config
    /// source uses to suppress no-op reloads. A hand-maintained field list here drifted from the
    /// source's list and silently omitted <c>FileAccess</c>, so edited file-access policies were
    /// never re-registered and agents kept a stale path validator for the process lifetime
    /// (#2383). Do not reintroduce a local field-by-field comparison.
    /// </para>
    /// </summary>
    private static bool DescriptorsEqual(AgentDescriptor a, AgentDescriptor b)
        => AgentDescriptorFingerprint.AreEquivalent(a, b);
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
