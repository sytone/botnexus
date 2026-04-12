using System.Diagnostics;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default agent supervisor — manages agent instance lifecycle using isolation strategies.
/// </summary>
public sealed class DefaultAgentSupervisor : IAgentSupervisor, IAgentHandleInspector
{
    private readonly IAgentRegistry _registry;
    private readonly IReadOnlyDictionary<string, IIsolationStrategy> _strategies;
    private readonly ISessionStore _sessionStore;
    private readonly Dictionary<AgentSessionKey, (AgentInstance Instance, IAgentHandle Handle)> _instances = [];
    private readonly Dictionary<AgentSessionKey, Task<(AgentInstance Instance, IAgentHandle Handle)>> _pendingCreates = [];
    private readonly Lock _sync = new();
    private readonly ILogger<DefaultAgentSupervisor> _logger;

    public DefaultAgentSupervisor(
        IAgentRegistry registry,
        IEnumerable<IIsolationStrategy> strategies,
        ISessionStore sessionStore,
        ILogger<DefaultAgentSupervisor> logger)
    {
        _registry = registry;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = GatewayDiagnostics.Source.StartActivity("gateway.agent_lifecycle", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", agentId);
        activity?.SetTag("botnexus.session.id", sessionId);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());

        var descriptor = _registry.Get(agentId)
            ?? throw new KeyNotFoundException($"Agent '{agentId}' is not registered.");
        var key = AgentSessionKey.From(agentId, sessionId);
        Task<(AgentInstance Instance, IAgentHandle Handle)> creationTask;
        TaskCompletionSource<(AgentInstance Instance, IAgentHandle Handle)>? creationCompletion = null;

        lock (_sync)
        {
            if (_instances.TryGetValue(key, out var existing) && existing.Instance.Status is AgentInstanceStatus.Idle or AgentInstanceStatus.Running)
                return existing.Handle;

            if (!_pendingCreates.TryGetValue(key, out creationTask!))
            {
                if (descriptor.MaxConcurrentSessions > 0)
                {
                    var activeSessions = CountActiveSessionsForAgent(agentId);
                    if (activeSessions >= descriptor.MaxConcurrentSessions)
                        throw new AgentConcurrencyLimitExceededException(agentId, descriptor.MaxConcurrentSessions);
                }

                creationCompletion = new TaskCompletionSource<(AgentInstance Instance, IAgentHandle Handle)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                creationTask = creationCompletion.Task;
                _pendingCreates[key] = creationTask;
            }
        }

        if (creationCompletion is null)
        {
            var existingCreate = await creationTask.WaitAsync(cancellationToken);
            return existingCreate.Handle;
        }

        try
        {
            var created = await CreateEntryAsync(descriptor, sessionId, key, cancellationToken);
            lock (_sync)
            {
                _pendingCreates.Remove(key);
                _instances[key] = created;
            }
            creationCompletion.SetResult(created);

            _logger.LogInformation("Created agent instance '{AgentId}' for session '{SessionId}' (isolation: {Strategy})",
                agentId, sessionId, created.Instance.IsolationStrategy);

            return created.Handle;
        }
        catch (Exception ex)
        {
            lock (_sync) _pendingCreates.Remove(key);
            creationCompletion.SetException(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var key = AgentSessionKey.From(agentId, sessionId);
        (AgentInstance Instance, IAgentHandle Handle) entry;

        lock (_sync)
        {
            if (!_instances.Remove(key, out entry))
                return;
        }

        entry.Instance.Status = AgentInstanceStatus.Stopping;
        await entry.Handle.DisposeAsync();
        entry.Instance.Status = AgentInstanceStatus.Stopped;

        _logger.LogInformation("Stopped agent instance '{AgentId}' for session '{SessionId}'", agentId, sessionId);
    }

    /// <inheritdoc />
    public AgentInstance? GetInstance(AgentId agentId, SessionId sessionId)
    {
        lock (_sync) return _instances.GetValueOrDefault(AgentSessionKey.From(agentId, sessionId)).Instance;
    }

    /// <inheritdoc />
    public IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId)
    {
        lock (_sync) return _instances.GetValueOrDefault(AgentSessionKey.From(agentId, sessionId)).Handle;
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentInstance> GetAllInstances()
    {
        lock (_sync) return _instances.Values.Select(e => e.Instance).ToList();
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        List<(AgentInstance Instance, IAgentHandle Handle)> entries;
        lock (_sync)
        {
            entries = [.. _instances.Values];
            _instances.Clear();
            _pendingCreates.Clear();
        }

        foreach (var (instance, handle) in entries)
        {
            instance.Status = AgentInstanceStatus.Stopping;
            try { await handle.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping agent instance '{InstanceId}'", instance.InstanceId); }
            instance.Status = AgentInstanceStatus.Stopped;
        }
    }

    private int CountActiveSessionsForAgent(AgentId agentId)
    {
        var activeInstanceKeys = _instances
            .Where(pair =>
                string.Equals(pair.Value.Instance.AgentId, agentId, StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Instance.Status is not AgentInstanceStatus.Stopped and not AgentInstanceStatus.Faulted)
            .Select(pair => pair.Key.SessionId);

        var pendingKeys = _pendingCreates.Keys.Where(key => string.Equals(key.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .Select(key => key.SessionId);

        return activeInstanceKeys
            .Concat(pendingKeys)
            .Distinct()
            .Count();
    }

    private async Task<(AgentInstance Instance, IAgentHandle Handle)> CreateEntryAsync(
        AgentDescriptor descriptor,
        SessionId sessionId,
        AgentSessionKey key,
        CancellationToken cancellationToken)
    {
        var descriptorErrors = AgentDescriptorValidator.Validate(descriptor, _strategies.Keys);
        if (descriptorErrors.Count > 0)
            throw new InvalidOperationException(
                $"Agent '{descriptor.AgentId}' configuration is invalid: {string.Join("; ", descriptorErrors)}");

        if (!_strategies.TryGetValue(descriptor.IsolationStrategy, out var strategy))
        {
            var availableStrategies = string.Join(", ", _strategies.Keys.Order(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"Isolation strategy '{descriptor.IsolationStrategy}' is not registered for agent '{descriptor.AgentId}'. " +
                $"Available strategies: {availableStrategies}.");
        }

        var context = new AgentExecutionContext { SessionId = sessionId };
        var handle = await strategy.CreateAsync(descriptor, context, cancellationToken);

        var instance = new AgentInstance
        {
            InstanceId = key.ToString(),
            AgentId = descriptor.AgentId,
            SessionId = sessionId,
            IsolationStrategy = descriptor.IsolationStrategy,
            Status = AgentInstanceStatus.Idle
        };

        return (instance, handle);
    }
}
