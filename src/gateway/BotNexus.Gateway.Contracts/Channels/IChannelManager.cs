using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;

namespace BotNexus.Gateway.Abstractions.Channels;

/// <summary>
/// Read-only registry for registered channel adapters.
/// </summary>
public interface IChannelManager
{
    /// <summary>Gets all registered channel adapters.</summary>
    IReadOnlyList<IChannelAdapter> Adapters { get; }

    /// <summary>Gets a channel adapter by type, or <c>null</c> if not registered.</summary>
    IChannelAdapter? Get(ChannelKey channelType);

    /// <summary>
    /// Gets a channel adapter by type and optional adapter ID.
    /// When <paramref name="adapterId"/> is provided and a matching adapter is found, that adapter
    /// is returned. If <paramref name="adapterId"/> is <c>null</c> or no adapter with that ID is
    /// registered for the type, falls back to the first adapter of the given type.
    /// Returns <c>null</c> when no adapters of that type are registered at all.
    /// </summary>
    IChannelAdapter? Get(ChannelKey channelType, string? adapterId);
}
