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
    private readonly IHubContext<GatewayHub, IGatewayHubClient> _hubContext = hubContext;

    public override ChannelKey ChannelType => ChannelKey.From("signalr");
    public override string DisplayName => "Web Chat";
    public override bool SupportsStreaming => true;
    public override bool SupportsSteering => true;
    public override bool SupportsFollowUp => true;
    public override bool SupportsThinkingDisplay => true;
    public override bool SupportsToolDisplay => true;

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
        var normalizedSessionId = NormalizeSessionId(message.SessionId ?? message.ConversationId);
        return _hubContext.Clients.Group(GetSessionGroup(normalizedSessionId))
            .ContentDelta(new ContentDeltaPayload(normalizedSessionId, message.Content));
    }

    /// <summary>
    /// Executes send stream delta async.
    /// </summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="delta">The delta.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send stream delta async result.</returns>
    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
    {
        var normalizedSessionId = NormalizeSessionId(conversationId);
        return _hubContext.Clients.Group(GetSessionGroup(normalizedSessionId))
            .ContentDelta(new ContentDeltaPayload(normalizedSessionId, delta));
    }

    /// <summary>
    /// Executes send stream event async.
    /// </summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="streamEvent">The stream event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send stream event async result.</returns>
    public Task SendStreamEventAsync(string conversationId, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        var normalizedSessionId = NormalizeSessionId(conversationId);
        var typedSessionId = SessionId.From(normalizedSessionId);

        logger.LogInformation("SignalR → group session:{SessionId} method {Method}", normalizedSessionId, streamEvent.Type);
        var enrichedEvent = streamEvent with { SessionId = typedSessionId };
        var client = _hubContext.Clients.Group(GetSessionGroup(normalizedSessionId));

        return streamEvent.Type switch
        {
            AgentStreamEventType.MessageStart => client.MessageStart(enrichedEvent),
            AgentStreamEventType.ThinkingDelta => client.ThinkingDelta(enrichedEvent),
            AgentStreamEventType.ContentDelta => client.ContentDelta(enrichedEvent),
            AgentStreamEventType.ToolStart => client.ToolStart(enrichedEvent),
            AgentStreamEventType.ToolEnd => client.ToolEnd(enrichedEvent),
            AgentStreamEventType.MessageEnd => client.MessageEnd(enrichedEvent),
            AgentStreamEventType.Error => client.Error(enrichedEvent),
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
