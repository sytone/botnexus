using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default in-memory agent registry. Thread-safe via lock.
/// </summary>
public sealed class DefaultAgentRegistry : IAgentRegistry
{
    private readonly Dictionary<string, AgentDescriptor> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private readonly ILogger<DefaultAgentRegistry> _logger;
    private readonly IActivityBroadcaster? _activity;

    public DefaultAgentRegistry(ILogger<DefaultAgentRegistry> logger, IActivityBroadcaster? activity = null)
    {
        _logger = logger;
        _activity = activity;
    }

    /// <inheritdoc />
    public void Register(AgentDescriptor descriptor)
    {
        lock (_sync)
        {
            if (!_agents.TryAdd(descriptor.AgentId, descriptor))
                throw new InvalidOperationException($"Agent '{descriptor.AgentId}' is already registered.");

            _logger.LogInformation("Registered agent '{AgentId}' ({DisplayName})", descriptor.AgentId, descriptor.DisplayName);
            PublishActivity(
                GatewayActivityType.AgentRegistered,
                descriptor.AgentId,
                $"Agent '{descriptor.AgentId}' registered.",
                new Dictionary<string, object?> { ["displayName"] = descriptor.DisplayName });
        }
    }

    /// <inheritdoc />
    public void Unregister(AgentId agentId)
    {
        lock (_sync)
        {
            if (_agents.Remove(agentId))
            {
                _logger.LogInformation("Unregistered agent '{AgentId}'", agentId);
                PublishActivity(
                    GatewayActivityType.AgentUnregistered,
                    agentId,
                    $"Agent '{agentId}' unregistered.",
                    null);
            }
        }
    }

    /// <inheritdoc />
    public bool Update(AgentId agentId, AgentDescriptor descriptor)
    {
        if (!string.Equals(agentId, descriptor.AgentId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The descriptor AgentId must match the route agentId.", nameof(descriptor));

        lock (_sync)
        {
            if (!_agents.ContainsKey(agentId))
                return false;

            _agents[agentId] = descriptor;
            _logger.LogInformation("Updated agent '{AgentId}' ({DisplayName})", descriptor.AgentId, descriptor.DisplayName);
            PublishActivity(
                GatewayActivityType.AgentConfigChanged,
                descriptor.AgentId,
                $"Agent '{descriptor.AgentId}' configuration updated.",
                new Dictionary<string, object?> { ["displayName"] = descriptor.DisplayName });
            return true;
        }
    }

    /// <inheritdoc />
    public AgentDescriptor? Get(AgentId agentId)
    {
        lock (_sync) return _agents.GetValueOrDefault(agentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDescriptor> GetAll()
    {
        lock (_sync) return [.. _agents.Values];
    }

    /// <inheritdoc />
    public bool Contains(AgentId agentId)
    {
        lock (_sync) return _agents.ContainsKey(agentId);
    }

    private void PublishActivity(GatewayActivityType type, AgentId agentId, string message, IReadOnlyDictionary<string, object?>? data)
    {
        if (_activity is null)
            return;

        try
        {
            _activity.PublishAsync(new GatewayActivity
            {
                Type = type,
                AgentId = agentId,
                Message = message,
                Data = data
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish gateway activity '{ActivityType}' for agent '{AgentId}'", type, agentId);
        }
    }
}
