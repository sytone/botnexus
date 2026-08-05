using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Channel adapter for internal messages (sub-agent completions, cross-agent routing).
/// Resolves the target session's original channel and delegates message and stream delivery to that adapter.
/// </summary>
public sealed class InternalChannelAdapter : ChannelAdapterBase, IStreamEventChannelAdapter
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionStore _sessionStore;

    public InternalChannelAdapter(
        IServiceProvider serviceProvider,
        ISessionStore sessionStore,
        ILogger<InternalChannelAdapter> logger) : base(logger)
    {
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
    }

    public override ChannelKey ChannelType => ChannelKey.From("internal");
    public override string DisplayName => "Internal";
    public override bool SupportsStreaming => true;
    public override bool SupportsSteering => false;
    public override bool SupportsFollowUp => false;
    public override bool SupportsThinkingDisplay => false;
    public override bool SupportsToolDisplay => false;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task OnStopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        var targetAdapter = await ResolveTargetAdapterAsync(message, cancellationToken);
        if (targetAdapter is null)
        {
            Logger.LogWarning(
                "Internal adapter: no target channel resolved for session '{SessionId}'. Response saved to history but not delivered.",
                message.SessionId);
            return;
        }

        var remapped = await ReaddressForTargetAsync(message, targetAdapter, cancellationToken);
        await targetAdapter.SendAsync(remapped, cancellationToken);
    }

    /// <summary>
    /// Rewrites the outbound message so it is addressed in the TARGET channel's own address space
    /// before hand-off (#2815).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ChannelAddress"/> is overloaded: on the <c>internal</c> channel it is a
    /// session-routing key, and internal producers legitimately build one from an agent id (see
    /// <c>ConversationTool</c>, <c>ConversationCronFailureAlertSink</c>,
    /// <c>AskUserCheckpointResumer</c>) or from an internal <c>c_</c> conversation id. On an
    /// EXTERNAL transport neither is a deliverable destination.
    /// </para>
    /// <para>
    /// Before #2815 this method forwarded the message verbatim apart from the channel type, so an
    /// internal-origin turn whose session had previously been established over Service Bus was
    /// re-targeted onto the Service Bus adapter while still carrying the agent id as its address.
    /// The adapter faithfully copied that agent id into the envelope's <c>conversationId</c>, and
    /// the fail-closed Teams relay dead-lettered every such reply (134 messages; Teams one-way
    /// broken). The observed envelopes had <c>correlationId: null</c> AND <c>agentId: null</c> -
    /// i.e. no pending reply context and no adapter metadata - which is precisely the signature of
    /// this re-target, not of the ordinary inbound reply path.
    /// </para>
    /// <para>
    /// The fix reuses the ONE existing notion of "where does this reply go": the conversation's
    /// <see cref="ChannelBinding"/> for the target channel, which is exactly what
    /// <c>OutboundResponseDeliverer</c> fans out on. No second spelling of the destination is
    /// introduced - a second spelling is the defect family that produced #2796 and #2792. When no
    /// binding exists the message is left untouched so the target adapter's own fail-closed
    /// validity clause can refuse it loudly rather than emitting a certain dead-letter.
    /// </para>
    /// </remarks>
    private async Task<OutboundMessage> ReaddressForTargetAsync(
        OutboundMessage message,
        IChannelAdapter targetAdapter,
        CancellationToken cancellationToken)
    {
        var retyped = message with { ChannelType = targetAdapter.ChannelType };

        var binding = await ResolveTargetBindingAsync(message.SessionId, targetAdapter, cancellationToken);
        if (binding is null)
            return retyped;

        if (binding.ChannelAddress == message.ChannelAddress)
            return retyped;

        Logger.LogDebug(
            "Internal adapter: re-addressing outbound message for session '{SessionId}' from internal "
            + "routing key '{InternalAddress}' to the '{ChannelType}' binding address (#2815).",
            message.SessionId,
            message.ChannelAddress,
            targetAdapter.ChannelType);

        return retyped with
        {
            ChannelAddress = binding.ChannelAddress,
            BindingId = message.BindingId ?? binding.BindingId,
            DisplayPrefix = message.DisplayPrefix ?? binding.DisplayPrefix
        };
    }

    /// <summary>
    /// Finds the conversation binding that addresses <paramref name="targetAdapter"/> for the
    /// session's conversation. Best-effort: any lookup failure leaves the message unchanged.
    /// </summary>
    private async Task<ChannelBinding?> ResolveTargetBindingAsync(
        string? sessionId,
        IChannelAdapter targetAdapter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        try
        {
            var session = await _sessionStore.GetAsync(SessionId.From(sessionId), cancellationToken);
            if (session is null || !session.ConversationId.IsInitialized())
                return null;

            var conversationStore = _serviceProvider.GetService<IConversationStore>();
            if (conversationStore is null)
                return null;

            var conversation = await conversationStore.GetAsync(session.ConversationId, cancellationToken);
            if (conversation is null)
                return null;

            return conversation.ChannelBindings
                .FirstOrDefault(b =>
                    b.ChannelType == targetAdapter.ChannelType
                    && b.Mode != BindingMode.Muted);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(
                ex,
                "Internal adapter: could not resolve a '{ChannelType}' binding for session '{SessionId}'.",
                targetAdapter.ChannelType,
                sessionId);
            return null;
        }
    }

    public override async Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        var targetAdapter = await ResolveTargetAdapterForSessionAsync(target.SessionId, cancellationToken);
        if (targetAdapter is null)
            return;

        await targetAdapter.SendStreamDeltaAsync(target, delta, cancellationToken);
    }

    /// <summary>
    /// Routes structured stream events for internal wake-ups through the session's original channel adapter so
    /// lifecycle events (start/end, thinking, and tool notifications) are preserved when parent agents are resumed.
    /// If the target channel only supports plain deltas, content events degrade gracefully to delta forwarding.
    /// </summary>
    /// <param name="target">Typed stream target — the parent session and its originating address.</param>
    /// <param name="streamEvent">The structured stream event to deliver to the resolved channel.</param>
    /// <param name="cancellationToken">Cancellation token for the async send operation.</param>
    public async Task SendStreamEventAsync(
        ChannelStreamTarget target,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken = default)
    {
        var targetAdapter = await ResolveTargetAdapterForSessionAsync(target.SessionId, cancellationToken);
        if (targetAdapter is null)
        {
            Logger.LogWarning(
                "Internal adapter: no target channel resolved for session '{SessionId}'. Stream event '{EventType}' was not delivered.",
                target.SessionId,
                streamEvent.Type);
            return;
        }

        if (targetAdapter is IStreamEventChannelAdapter streamTarget)
        {
            if (!streamTarget.CanSendStreamEvent(target))
            {
                Logger.LogWarning(
                    "Internal adapter: target channel '{TargetChannelType}' cannot deliver stream event '{EventType}' for session '{SessionId}'; skipping delivery.",
                    targetAdapter.ChannelType,
                    streamEvent.Type,
                    target.SessionId);
                return;
            }

            await streamTarget.SendStreamEventAsync(target, streamEvent, cancellationToken);
            return;
        }

        if (streamEvent.Type == AgentStreamEventType.ContentDelta
            && streamEvent.ContentDelta is not null)
        {
            await targetAdapter.SendStreamDeltaAsync(target, streamEvent.ContentDelta, cancellationToken);
        }
    }

    private async Task<IChannelAdapter?> ResolveTargetAdapterAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(message.SessionId))
        {
            var session = await _sessionStore.GetAsync(SessionId.From(message.SessionId), cancellationToken);
            if (session?.ChannelType is { } channelType)
            {
                var adapter = GetChannelManager().Get(channelType);
                if (adapter is not null && !adapter.ChannelType.Equals(ChannelType))
                {
                    Logger.LogDebug(
                        "Internal adapter: routing to '{ChannelType}' for session '{SessionId}'",
                        channelType, message.SessionId);
                    return adapter;
                }
            }
        }

        var fallback = GetChannelManager().Get(ChannelKey.From("signalr"));
        if (fallback is not null && !fallback.ChannelType.Equals(ChannelType))
        {
            Logger.LogDebug("Internal adapter: falling back to 'signalr' for delivery");
            return fallback;
        }

        return null;
    }

    private async Task<IChannelAdapter?> ResolveTargetAdapterForSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
            if (session?.ChannelType is { } channelType)
            {
                var adapter = GetChannelManager().Get(channelType);
                if (adapter is not null && !adapter.ChannelType.Equals(ChannelType))
                    return adapter;
            }
        }
        catch
        {
            // Best effort — fall through to signalr.
        }

        var fallback = GetChannelManager().Get(ChannelKey.From("signalr"));
        return fallback is not null && !fallback.ChannelType.Equals(ChannelType)
            ? fallback
            : null;
    }

    private IChannelManager GetChannelManager()
        => _serviceProvider.GetRequiredService<IChannelManager>();
}
