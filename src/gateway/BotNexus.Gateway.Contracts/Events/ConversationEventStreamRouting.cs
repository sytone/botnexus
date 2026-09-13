using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// Derives the stream targets owned by one channel adapter from an immutable conversation-event
/// routing snapshot. This contract helper makes routing decisions only; channel extensions retain
/// ownership of transport sends.
/// </summary>
public static class ConversationEventStreamRouting
{
    /// <summary>
    /// Returns this adapter's non-muted targets for an agent event. Only the originating binding
    /// receives the origin correlation id; observer bindings cannot inherit transport correlation.
    /// </summary>
    /// <param name="conversationEvent">The immutable event offered to the channel extension.</param>
    /// <param name="channelType">The adapter's channel family.</param>
    /// <param name="adapterId">The adapter instance discriminator, when applicable.</param>
    public static ImmutableArray<ChannelStreamTarget> GetTargets(
        ConversationEvent conversationEvent,
        ChannelKey channelType,
        string? adapterId)
    {
        ArgumentNullException.ThrowIfNull(conversationEvent);

        if (conversationEvent is not ConversationAgentEvent ||
            conversationEvent.SessionId is not { } sessionId)
        {
            return ImmutableArray<ChannelStreamTarget>.Empty;
        }

        var targets = ImmutableArray.CreateBuilder<ChannelStreamTarget>();
        foreach (var binding in conversationEvent.Bindings)
        {
            if (binding.Mode == BindingMode.Muted ||
                binding.ChannelType != channelType ||
                !string.Equals(binding.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            targets.Add(new ChannelStreamTarget(
                conversationEvent.ConversationId,
                sessionId,
                binding.ChannelAddress,
                binding.BindingId,
                binding.BindingId == conversationEvent.Origin.BindingId
                    ? conversationEvent.Origin.CorrelationId
                    : null));
        }

        return targets.ToImmutable();
    }
}
