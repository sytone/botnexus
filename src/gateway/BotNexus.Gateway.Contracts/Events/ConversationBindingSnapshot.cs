using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// Value snapshot of a single channel binding at the instant an event was raised.
/// <para>
/// Channel bindings are mutable gateway state (<see cref="ChannelBinding"/> has settable
/// members and moving timestamps). Handing that live object to every sink would let one
/// extension observe - or cause - changes another extension is concurrently reading. This
/// record is the read-only projection published instead.
/// </para>
/// </summary>
/// <param name="BindingId">Identity of the binding, stable for its lifetime.</param>
/// <param name="ChannelType">Channel family the binding belongs to; sinks match on this without the publisher knowing it.</param>
/// <param name="AdapterId">Adapter instance discriminator when several adapters share a channel type; null for single-instance channels.</param>
/// <param name="ChannelAddress">Opaque channel-specific address; empty for addressless channels.</param>
/// <param name="Mode">Fan-out participation mode of the binding at snapshot time.</param>
/// <param name="ThreadingMode">Threading behaviour of the binding at snapshot time.</param>
public sealed record ConversationBindingSnapshot(
    BindingId BindingId,
    ChannelKey ChannelType,
    string? AdapterId,
    ChannelAddress ChannelAddress,
    BindingMode Mode,
    ThreadingMode ThreadingMode)
{
    /// <summary>
    /// Projects a live gateway binding into an immutable snapshot. Call this at publication
    /// time so the value captured is what was true when the fact occurred, not what the
    /// binding drifted to while sinks were being invoked.
    /// </summary>
    /// <param name="binding">The live binding to project. Must not be null.</param>
    public static ConversationBindingSnapshot From(ChannelBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new ConversationBindingSnapshot(
            binding.BindingId,
            binding.ChannelType,
            binding.AdapterId,
            binding.ChannelAddress,
            binding.Mode,
            binding.ThreadingMode);
    }

    /// <summary>
    /// Projects a sequence of live bindings into an immutable snapshot array suitable for
    /// <see cref="ConversationEvent.Bindings"/>. Returns an empty array for a null source.
    /// </summary>
    /// <param name="bindings">The live bindings to project; may be null or empty.</param>
    public static ImmutableArray<ConversationBindingSnapshot> FromMany(IEnumerable<ChannelBinding>? bindings)
    {
        if (bindings is null)
        {
            return ImmutableArray<ConversationBindingSnapshot>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<ConversationBindingSnapshot>();
        foreach (var binding in bindings)
        {
            builder.Add(From(binding));
        }

        return builder.ToImmutable();
    }
}
