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
    /// Sends a non-streaming message to the SignalR group for the target conversation.
    /// </summary>
    /// <param name="message">The message — <see cref="OutboundMessage.ConversationId"/>
    /// is the preferred routing key. <see cref="OutboundMessage.SessionId"/> is used as
    /// a fallback for callers that have not yet adopted the typed conversation field.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (string.Equals(message.Content?.Trim(), NoReplySentinel, StringComparison.Ordinal))
        {
            logger.LogDebug("Suppressed NO_REPLY message for session {SessionId}", message.SessionId ?? message.ChannelAddress.Value);
            return Task.CompletedTask;
        }

        var normalizedSessionId = NormalizeSessionId(message.SessionId ?? message.ChannelAddress.Value);
        // Prefer the conversation when present; otherwise fall back to "conversation:{sessionId}"
        // as a back-compat synonym so callers that have not yet populated ConversationId still
        // reach the connection (which subscribed via the same fallback in SubscribeAll).
        var groupKey = message.ConversationId is { Length: > 0 } conv
            ? GetConversationGroup(conv)
            : GetConversationGroup(normalizedSessionId);
        return _hubContext.Clients.Group(groupKey)
            .ContentDelta(new ContentDeltaPayload(normalizedSessionId, message.Content, message.ConversationId));
    }

    /// <summary>
    /// Sends a streaming delta to the SignalR group for the target conversation.
    /// </summary>
    /// <param name="target">Typed stream target — SignalR routes by
    /// <see cref="ChannelStreamTarget.ConversationId"/> so the group survives compaction.</param>
    /// <param name="delta">The delta.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public override Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        var conversationIdValue = target.ConversationId.Value;
        var sessionIdValue = target.SessionId.Value;
        return _hubContext.Clients.Group(GetConversationGroup(conversationIdValue))
            .ContentDelta(new ContentDeltaPayload(sessionIdValue, delta, conversationIdValue));
    }

    /// <summary>
    /// Sends a structured stream event to the SignalR group for the target conversation.
    /// </summary>
    /// <param name="target">Typed stream target — SignalR routes by
    /// <see cref="ChannelStreamTarget.ConversationId"/> so the group survives compaction.</param>
    /// <param name="streamEvent">The stream event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        // Prefer the session and conversation ids stamped on the event (set by GatewayHost)
        // over the target, so observer fan-out — which addresses each observer by their own
        // binding — still surfaces the originating ids to the client.
        var typedSessionId = streamEvent.SessionId ?? target.SessionId;
        var typedConversationId = streamEvent.ConversationId ?? target.ConversationId;
        var sessionIdStr = typedSessionId.Value;
        var conversationIdStr = typedConversationId.Value;

        logger.LogInformation(
            "SignalR → group conversation:{ConversationId} (session:{SessionId}) method {Method}",
            conversationIdStr,
            sessionIdStr,
            streamEvent.Type);
        var enrichedEvent = streamEvent with { SessionId = typedSessionId, ConversationId = typedConversationId };
        var client = _hubContext.Clients.Group(GetConversationGroup(conversationIdStr));

        return streamEvent.Type switch
        {
            AgentStreamEventType.RunStarted => client.RunStarted(enrichedEvent),
            AgentStreamEventType.MessageStart => client.MessageStart(enrichedEvent),
            AgentStreamEventType.ThinkingDelta => client.ThinkingDelta(enrichedEvent),
            AgentStreamEventType.ContentDelta => client.ContentDelta(enrichedEvent),
            AgentStreamEventType.ToolStart => client.ToolStart(enrichedEvent),
            AgentStreamEventType.ToolEnd => client.ToolEnd(enrichedEvent),
            AgentStreamEventType.MessageEnd => client.MessageEnd(enrichedEvent),
            AgentStreamEventType.Error => client.Error(enrichedEvent),
            AgentStreamEventType.UserInputRequired => client.UserInputRequired(enrichedEvent),
            AgentStreamEventType.TurnInterrupted => client.TurnInterrupted(enrichedEvent),
            // TurnEnd is emitted when the agent's full turn completes (including tool-only turns
            // where no MessageEnd is sent). Without this, the portal never clears IsStreaming
            // for tool-only cron or background turns, leaving a permanent spinner (#668).
            AgentStreamEventType.TurnEnd => client.TurnEnd(enrichedEvent),
            // RunStarted/RunEnded bracket the whole loop (across every turn and tool boundary).
            // RunEnded is the authoritative idle signal; clients drive steer/follow-up/stop control
            // visibility from it so the controls don't flicker in the gaps between turns and tools.
            AgentStreamEventType.RunEnded => client.RunEnded(enrichedEvent),
            _ => Task.CompletedTask
        };
    }

    internal static string GetConversationGroup(string conversationId) => $"conversation:{conversationId}";

    // Retained for the back-compat fallback path in SendAsync (until every caller stamps
    // ConversationId) and for tests that pin the legacy group naming.
    internal static string GetSessionGroup(string sessionId) => $"session:{sessionId}";

    private static string NormalizeSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        return SessionId.From(sessionId).Value;
    }
}
