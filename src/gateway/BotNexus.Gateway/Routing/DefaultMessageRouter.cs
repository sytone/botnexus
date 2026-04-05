using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Routing;

/// <summary>
/// Default message router. Resolves agent targets using the priority:
/// explicit target → session binding → default agent.
/// </summary>
public sealed class DefaultMessageRouter : IMessageRouter
{
    private readonly IAgentRegistry _registry;
    private readonly ISessionStore _sessions;
    private readonly ILogger<DefaultMessageRouter> _logger;
    private string? _defaultAgentId;

    public DefaultMessageRouter(
        IAgentRegistry registry,
        ISessionStore sessions,
        ILogger<DefaultMessageRouter> logger)
    {
        _registry = registry;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Sets the default agent ID used when no explicit target or session binding exists.
    /// </summary>
    public void SetDefaultAgent(string agentId) => _defaultAgentId = agentId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        // Priority 1: Explicit target
        if (!string.IsNullOrEmpty(message.TargetAgentId))
        {
            if (_registry.Contains(message.TargetAgentId))
                return [message.TargetAgentId];

            _logger.LogWarning("Explicit target agent '{AgentId}' not found", message.TargetAgentId);
            return [];
        }

        // Priority 2: Session-bound agent
        if (!string.IsNullOrEmpty(message.SessionId))
        {
            var session = await _sessions.GetAsync(message.SessionId, cancellationToken);
            if (session is not null && _registry.Contains(session.AgentId))
                return [session.AgentId];
        }

        // Priority 3: Default agent
        if (_defaultAgentId is not null && _registry.Contains(_defaultAgentId))
            return [_defaultAgentId];

        _logger.LogWarning("No routable agent found for message from {ChannelType}:{SenderId}", message.ChannelType, message.SenderId);
        return [];
    }
}
