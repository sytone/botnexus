using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.SignalR;

#pragma warning disable CS1591 // Channel adapter methods match base class contracts

/// <summary>
/// SignalR-based channel adapter. Sends agent output to session groups via IHubContext.
/// </summary>
public sealed class SignalRChannelAdapter(ILogger<SignalRChannelAdapter> logger, IHubContext<GatewayHub, IGatewayHubClient> hubContext)
    : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    /// <summary>Sentinel value agents use to suppress user-visible replies.</summary>
    private const string NoReplySentinel = "NO_REPLY";

    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    public override ChannelKey ChannelType => ChannelKey.From("signalr");
    public override string DisplayName => "Web Chat";
    public override bool SupportsStreaming => true;
    public override bool SupportsSteering => true;
    public override bool SupportsFollowUp => true;
    public override bool SupportsThinkingDisplay => true;
    public override bool SupportsToolDisplay => true;
    public override bool SupportsInboundImages => true;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Executes send async.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send async result.</returns>
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (string.Equals(message.Content?.Trim(), NoReplySentinel, StringComparison.Ordinal))
        {
            logger.LogDebug("Suppressed NO_REPLY message for session {SessionId}", message.SessionId ?? message.ChannelAddress.Value);
            return Task.CompletedTask;
        }

        var normalizedSessionId = NormalizeSessionId(message.SessionId ?? message.ChannelAddress.Value);
        return _hubContext.Clients.Group(GetSessionGroup(normalizedSessionId))
            .ContentDelta(new ContentDeltaPayload(normalizedSessionId, message.Content));
    }

    /// <summary>
    /// Sends a streaming delta to the SignalR group for the target session.
    /// </summary>
    /// <param name="target">Typed stream target — SignalR routes by <c>target.SessionId</c>.</param>
    /// <param name="delta">The delta.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send stream delta async result.</returns>
    public override Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        var sessionIdValue = target.SessionId.Value;
        return _hubContext.Clients.Group(GetSessionGroup(sessionIdValue))
            .ContentDelta(new ContentDeltaPayload(sessionIdValue, delta));
    }

    /// <summary>
    /// Sends a structured stream event to the SignalR group for the target session.
    /// </summary>
    /// <param name="target">Typed stream target — SignalR routes by <c>target.SessionId</c>.</param>
    /// <param name="streamEvent">The stream event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send stream event async result.</returns>
    public Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        // Prefer the session ID stamped on the event (set by GatewayHost) over the target,
        // so observer fan-out — which addresses each observer by their own binding — still
        // surfaces the originating session id to the client.
        var typedSessionId = streamEvent.SessionId ?? target.SessionId;
        var sessionIdStr = typedSessionId.Value;

        logger.LogInformation("SignalR → group session:{SessionId} method {Method}", sessionIdStr, streamEvent.Type);
        var enrichedEvent = streamEvent with { SessionId = typedSessionId };
        var client = _hubContext.Clients.Group(GetSessionGroup(sessionIdStr));

        return streamEvent.Type switch
        {
            AgentStreamEventType.MessageStart => client.MessageStart(enrichedEvent),
            AgentStreamEventType.ThinkingDelta => client.ThinkingDelta(enrichedEvent),
            AgentStreamEventType.ContentDelta => client.ContentDelta(enrichedEvent),
            AgentStreamEventType.ToolStart => client.ToolStart(enrichedEvent),
            AgentStreamEventType.ToolEnd => client.ToolEnd(enrichedEvent),
            AgentStreamEventType.MessageEnd => client.MessageEnd(enrichedEvent),
            AgentStreamEventType.Error => client.Error(enrichedEvent),
            AgentStreamEventType.UserInputRequired => client.UserInputRequired(enrichedEvent),
            _ => Task.CompletedTask
        };
    }

    private static string GetSessionGroup(string sessionId) => $"session:{sessionId}";

    private static string NormalizeSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        return SessionId.From(sessionId).Value;
    }
}
