using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default agent supervisor — manages agent instance lifecycle using isolation strategies.
/// </summary>
public sealed class DefaultAgentSupervisor : IAgentSupervisor
{
    private readonly IAgentRegistry _registry;
    private readonly IReadOnlyDictionary<string, IIsolationStrategy> _strategies;
    private readonly Dictionary<string, (AgentInstance Instance, IAgentHandle Handle)> _instances = [];
    private readonly Lock _sync = new();
    private readonly ILogger<DefaultAgentSupervisor> _logger;

    public DefaultAgentSupervisor(
        IAgentRegistry registry,
        IEnumerable<IIsolationStrategy> strategies,
        ILogger<DefaultAgentSupervisor> logger)
    {
        _registry = registry;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAgentHandle> GetOrCreateAsync(string agentId, string sessionId, CancellationToken cancellationToken = default)
    {
        var key = MakeKey(agentId, sessionId);

        lock (_sync)
        {
            if (_instances.TryGetValue(key, out var existing) && existing.Instance.Status is AgentInstanceStatus.Idle or AgentInstanceStatus.Running)
                return existing.Handle;
        }

        var descriptor = _registry.Get(agentId)
            ?? throw new KeyNotFoundException($"Agent '{agentId}' is not registered.");

        if (!_strategies.TryGetValue(descriptor.IsolationStrategy, out var strategy))
            throw new InvalidOperationException($"Isolation strategy '{descriptor.IsolationStrategy}' is not registered.");

        var context = new AgentExecutionContext { SessionId = sessionId };
        var handle = await strategy.CreateAsync(descriptor, context, cancellationToken);

        var instance = new AgentInstance
        {
            InstanceId = key,
            AgentId = agentId,
            SessionId = sessionId,
            IsolationStrategy = descriptor.IsolationStrategy,
            Status = AgentInstanceStatus.Idle
        };

        lock (_sync) _instances[key] = (instance, handle);

        _logger.LogInformation("Created agent instance '{AgentId}' for session '{SessionId}' (isolation: {Strategy})",
            agentId, sessionId, descriptor.IsolationStrategy);

        return handle;
    }

    /// <inheritdoc />
    public async Task StopAsync(string agentId, string sessionId, CancellationToken cancellationToken = default)
    {
        var key = MakeKey(agentId, sessionId);
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
    public AgentInstance? GetInstance(string agentId, string sessionId)
    {
        lock (_sync) return _instances.GetValueOrDefault(MakeKey(agentId, sessionId)).Instance;
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
        }

        foreach (var (instance, handle) in entries)
        {
            instance.Status = AgentInstanceStatus.Stopping;
            try { await handle.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping agent instance '{InstanceId}'", instance.InstanceId); }
            instance.Status = AgentInstanceStatus.Stopped;
        }
    }

    private static string MakeKey(string agentId, string sessionId) => $"{agentId}::{sessionId}";
}
