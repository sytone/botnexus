using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Hubs;

#pragma warning disable CS1591 // Channel adapter methods match base class contracts

/// <summary>
/// SignalR-based channel adapter. Sends agent output to session groups via IHubContext.
/// </summary>
public sealed class SignalRChannelAdapter(ILogger<SignalRChannelAdapter> logger, IHubContext<GatewayHub> hubContext)
    : ChannelAdapterBase(logger), IStreamEventChannelAdapter
{
    private readonly IHubContext<GatewayHub> _hubContext = hubContext;

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

    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        => _hubContext.Clients.Group(GetSessionGroup(message.SessionId ?? message.ConversationId))
            .SendAsync("ContentDelta", message.Content, cancellationToken);

    public override Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
        => _hubContext.Clients.Group(GetSessionGroup(conversationId))
            .SendAsync("ContentDelta", delta, cancellationToken);

    public Task SendStreamEventAsync(string conversationId, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        var method = streamEvent.Type switch
        {
            AgentStreamEventType.MessageStart => "MessageStart",
            AgentStreamEventType.ThinkingDelta => "ThinkingDelta",
            AgentStreamEventType.ContentDelta => "ContentDelta",
            AgentStreamEventType.ToolStart => "ToolStart",
            AgentStreamEventType.ToolEnd => "ToolEnd",
            AgentStreamEventType.MessageEnd => "MessageEnd",
            AgentStreamEventType.Error => "Error",
            _ => "Unknown"
        };

        logger.LogInformation("SignalR → group session:{SessionId} method {Method}", conversationId, method);
        var enrichedEvent = streamEvent with { SessionId = conversationId };
        return _hubContext.Clients.Group(GetSessionGroup(conversationId))
            .SendAsync(method, enrichedEvent, cancellationToken);
    }

    private static string GetSessionGroup(string sessionId) => $"session:{sessionId}";
}
