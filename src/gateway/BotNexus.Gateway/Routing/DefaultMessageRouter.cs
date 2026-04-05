using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptions<GatewayOptions> _options;

    public DefaultMessageRouter(
        IAgentRegistry registry,
        ISessionStore sessions,
        ILogger<DefaultMessageRouter> logger,
        IOptions<GatewayOptions> options)
    {
        _registry = registry;
        _sessions = sessions;
        _logger = logger;
        _options = options;
    }

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
        var defaultAgentId = _options.Value.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(defaultAgentId) && _registry.Contains(defaultAgentId))
            return [defaultAgentId];

        _logger.LogWarning("No routable agent found for message from {ChannelType}:{SenderId}", message.ChannelType, message.SenderId);
        return [];
    }
}
