using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
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

    /// <inheritdoc />
    public async Task FanOutAsync(
        InboundMessage source,
        SessionId sessionId,
        string? content,
        ConversationId conversationId,
        CancellationToken ct)
    {
        // Nothing to deliver - e.g. a NO_REPLY turn that produced no assistant entry. Preserves the
        // prior behaviour where a missing last-assistant entry short-circuited the fan-out.
        if (string.IsNullOrEmpty(content))
            return;

        try
        {
            var otherBindings = await _conversationRouter.GetOutboundBindingsAsync(
                sessionId,
                source.BindingId,
                ct);

            if (otherBindings.Count == 0)
                return;

            foreach (var binding in otherBindings)
                await DeliverToBindingAsync(binding, content, sessionId, conversationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fan-out resolution failed for session {SessionId}. Continuing.", sessionId.Value);
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
