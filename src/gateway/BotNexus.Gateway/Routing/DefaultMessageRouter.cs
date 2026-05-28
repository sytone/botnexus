using System.Diagnostics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Dispatching;
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
    private readonly IOptionsMonitor<GatewayOptions> _options;

    public DefaultMessageRouter(
        IAgentRegistry registry,
        ISessionStore sessions,
        ILogger<DefaultMessageRouter> logger,
        IOptionsMonitor<GatewayOptions> options)
    {
        _registry = registry;
        _sessions = sessions;
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        using var activity = GatewayDiagnostics.Source.StartActivity("gateway.route", ActivityKind.Internal);
        activity?.SetTag("botnexus.channel.type", message.ChannelType);
        activity?.SetTag("botnexus.correlation.id", System.Diagnostics.Activity.Current?.TraceId.ToString());

        IReadOnlyList<string> Complete(IReadOnlyList<string> targets)
        {
            activity?.SetTag("botnexus.route.agent_count", targets.Count);
            return targets;
        }

        // Sub-PR 6.2 (#582): consume Vogen-typed routing hints instead of reading the legacy
        // string fields directly. InboundMessageRoutingHints.FromMessage is the sanctioned
        // single reader (architecture fence allowlist); whitespace-or-empty inputs normalise
        // to null hints, replacing the prior IsNullOrEmpty branches in this method.
        var hints = InboundMessageRoutingHints.FromMessage(message);

        // Priority 1: Explicit target
        if (hints.RequestedAgentId is { } targetAgentId)
        {
            if (_registry.Contains(targetAgentId))
                return Complete([targetAgentId.Value]);

            _logger.LogWarning("Explicit target agent '{AgentId}' not found", targetAgentId.Value);
            return Complete([]);
        }

        // Priority 2: Session-bound agent
        if (hints.RequestedSessionId is { } requestedSessionId)
        {
            using var sessionActivity = GatewayDiagnostics.Source.StartActivity("session.get", ActivityKind.Internal);
            sessionActivity?.SetTag("botnexus.session.id", requestedSessionId.Value);
            var session = await _sessions.GetAsync(requestedSessionId, cancellationToken);
            if (session is not null && _registry.Contains(session.AgentId))
                return Complete([session.AgentId.Value]);
        }

        // Priority 3: Default agent
        var defaultAgentId = _options.CurrentValue.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(defaultAgentId))
        {
            var typedDefaultAgentId = AgentId.From(defaultAgentId);
            if (_registry.Contains(typedDefaultAgentId))
                return Complete([typedDefaultAgentId.Value]);
        }

        _logger.LogWarning("No routable agent found for message from {ChannelType}:{SenderId}", message.ChannelType, message.SenderId);
        return Complete([]);
    }
}
