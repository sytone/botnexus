using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using ChannelAddress = BotNexus.Domain.Primitives.ChannelAddress;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using SessionId = BotNexus.Domain.Primitives.SessionId;

namespace BotNexus.Gateway;

/// <summary>
/// Default <see cref="IOutboundResponseDeliverer"/>. Owns the outbound fan-out delivery cluster
/// extracted verbatim from <see cref="GatewayHost"/> (#1811): resolve outbound bindings, skip
/// non-deliverable channel types, resolve the channel adapter, send, and self-heal stale bindings
/// by demoting them to <see cref="BindingMode.Muted"/>.
/// </summary>
internal sealed class OutboundResponseDeliverer(
    IConversationRouter conversationRouter,
    IChannelManager channelManager,
    ILogger<OutboundResponseDeliverer> logger) : IOutboundResponseDeliverer
{
    private readonly IConversationRouter _conversationRouter = conversationRouter;
    private readonly IChannelManager _channelManager = channelManager;
    private readonly ILogger<OutboundResponseDeliverer> _logger = logger;

    /// <summary>
    /// Channel types that are not deliverable (no adapter exists by design).
    /// Fan-out skips these silently at DEBUG level instead of logging a WARNING.
    /// </summary>
    internal static readonly HashSet<string> NonDeliverableChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "cron",
        "exchange"
    };

    internal static bool IsNonDeliverableChannel(ChannelKey channelType) =>
        NonDeliverableChannels.Contains(channelType.Value);

    /// <summary>
    /// The portal/observer channel. SignalR is deliberately NOT a competitor in the binding
    /// candidate set: it is a channel-agnostic observer that must see every turn regardless of
    /// which channel originated it or which channels also receive it (#2631).
    /// The concrete key lives on <see cref="ChannelKey.Observer"/> so this orchestration-adjacent
    /// file carries no channel-key literal (#2086 R2).
    /// </summary>
    internal static ChannelKey SignalRChannel => ChannelKey.Observer;

    /// <inheritdoc />
    public async Task FanOutAsync(
        InboundMessage source,
        SessionId sessionId,
        string? content,
        ConversationId conversationId,
        CancellationToken ct,
        bool primaryDeliveredToSignalR = false)
    {
        // Nothing to deliver - e.g. a NO_REPLY turn that produced no assistant entry. Preserves the
        // prior behaviour where a missing last-assistant entry short-circuited the fan-out.
        if (string.IsNullOrEmpty(content))
            return;

        // The primary (pre-fan-out) send in the orchestrator targets source.ChannelType. When the
        // inbound channel IS the observer channel, the reply already reached the portal, so the
        // sink must not emit it again. Derived HERE rather than passed in from GatewayHost: the
        // ChannelKnowledgeFence architecture rules forbid orchestration naming a concrete channel
        // key or resolving the observer channel (R2/R3), and deriving it also means the two paths
        // cannot drift the way a stored "already sent" flag would.
        var deliveredToSignalR = primaryDeliveredToSignalR || source.ChannelType == SignalRChannel;

        try
        {
            var otherBindings = await _conversationRouter.GetOutboundBindingsAsync(
                sessionId,
                source.BindingId,
                ct);

            // AC3: silence is the defect. The previous early `return` here is precisely why a
            // never-delivered binding went unnoticed for two weeks - a turn that fanned out to
            // nothing looked identical in the log to a turn that fanned out successfully.
            if (otherBindings.Count == 0)
            {
                _logger.LogDebug(
                    "Fan-out: no outbound bindings for session {SessionId} (origin '{OriginChannel}').",
                    sessionId.Value,
                    source.ChannelType);
            }

            foreach (var binding in otherBindings)
            {
                // An explicit signalr binding IS delivered to here; the sink below must then not
                // fire, or the portal renders the reply twice.
                if (binding.ChannelType == SignalRChannel)
                    deliveredToSignalR = true;

                await DeliverToBindingAsync(binding, content, sessionId, conversationId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out resolution failed for session {SessionId}. Continuing.", sessionId.Value);
        }

        // Runs even when binding resolution above threw: a broken binding store must not also
        // blind the portal. The sink is the observer of last resort.
        if (!deliveredToSignalR)
            await DeliverToSignalRSinkAsync(content, sessionId, conversationId, ct);
    }

    /// <summary>
    /// Emits the reply to the portal/SignalR sink unconditionally, independent of channel resolution.
    /// </summary>
    /// <remarks>
    /// Failures are logged and swallowed by design: the portal is an observer, so a dead hub must
    /// never fail a turn that was already delivered to its real channel.
    /// </remarks>
    private async Task DeliverToSignalRSinkAsync(
        string content,
        SessionId sessionId,
        ConversationId conversationId,
        CancellationToken ct)
    {
        // Resolve from the adapter inventory rather than IChannelManager.Get. Get() is the
        // per-BINDING resolution path, and OutboundResponseDelivererTests pins that a
        // non-deliverable binding short-circuits before Get is ever called. The sink is not a
        // binding, so it must not borrow that path -- otherwise the sink's own lookup would
        // make a real invariant untestable. (Widening that assertion instead would spend a
        // genuine contract to buy convenience.)
        var adapter = _channelManager.Adapters.FirstOrDefault(a => a.ChannelType == SignalRChannel);
        if (adapter is null)
        {
            _logger.LogDebug(
                "Fan-out: SignalR sink skipped for session {SessionId} - no 'signalr' adapter registered.",
                sessionId.Value);
            return;
        }

        try
        {
            await adapter.SendAsync(new OutboundMessage
            {
                ChannelType = SignalRChannel,
                // The portal addresses by conversation, not by a per-connection address: the sink
                // targets the conversation group so every connected observer sees the turn.
                ChannelAddress = ChannelAddress.From(conversationId.Value),
                Content = content,
                SessionId = sessionId.Value,
                ConversationId = conversationId.Value
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Fan-out: SignalR sink failed for session {SessionId}. Turn delivery unaffected.",
                sessionId.Value);
        }
    }

    /// <summary>
    /// Delivers a single fan-out message to one binding, with stale-binding self-heal.
    /// </summary>
    /// <remarks>
    /// A stale connection demotes the binding to Muted (so future fan-outs skip it); any other send
    /// failure is logged and swallowed so one bad binding never blocks delivery to the rest.
    /// </remarks>
    private async Task DeliverToBindingAsync(
        ChannelBinding binding,
        string content,
        SessionId sessionId,
        ConversationId conversationId,
        CancellationToken ct)
    {
        try
        {
            // Cron sessions create conversation bindings with channel type "cron" which
            // has no registered adapter (by design). Skip silently to avoid log noise.
            if (IsNonDeliverableChannel(binding.ChannelType))
            {
                _logger.LogDebug(
                    "Fan-out: skipping non-deliverable channel type '{ChannelType}' (binding {BindingId}).",
                    binding.ChannelType,
                    binding.BindingId);
                return;
            }

            var adapter = ResolveChannelAdapter(binding.ChannelType, binding.AdapterId);
            if (adapter is null)
            {
                _logger.LogWarning(
                    "Fan-out: no channel adapter for type '{ChannelType}' (binding {BindingId}). Skipping.",
                    binding.ChannelType,
                    binding.BindingId);
                return;
            }

            await adapter.SendAsync(new OutboundMessage
            {
                ChannelType = binding.ChannelType,
                ChannelAddress = binding.ChannelAddress,
                Content = content,
                SessionId = sessionId.Value,
                // Binding-aware fields: let the adapter render prefix decoration when
                // configured. Native sub-addresses (e.g. Telegram forum topics) are
                // already encoded in ChannelAddress by the originating adapter.
                BindingId = binding.BindingId,
                DisplayPrefix = binding.DisplayPrefix
            }, ct);

            _logger.LogDebug(
                "Fan-out delivered to {ChannelType}:{ChannelAddress} for session {SessionId}",
                binding.ChannelType, binding.ChannelAddress, sessionId.Value);
        }
        catch (StaleChannelConnectionException ex)
        {
            // Self-heal: demote stale bindings to Muted so future fan-outs skip them.
            _logger.LogWarning(
                ex,
                "Fan-out: stale connection for binding {BindingId} in conversation {ConversationId}. Demoting to Muted.",
                ex.BindingId, ex.ConversationId);

            if (conversationId.IsInitialized())
                await _conversationRouter.MuteBindingAsync(conversationId, ex.BindingId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Fan-out failed for binding {BindingId} ({ChannelType}:{ChannelAddress}). Continuing.",
                binding.BindingId, binding.ChannelType, binding.ChannelAddress);
        }
    }

    private IChannelAdapter? ResolveChannelAdapter(ChannelKey channelType, string? adapterId = null)
    {
        var adapter = _channelManager.Get(channelType, adapterId);
        if (adapter is not null)
            return adapter;

        _logger.LogWarning("No channel adapter found for type '{ChannelType}' (adapterId: '{AdapterId}'). Available: {Available}",
            channelType,
            adapterId ?? "<any>",
            string.Join(", ", _channelManager.Adapters.Select(a => a.ChannelType)));
        return null;
    }
}
