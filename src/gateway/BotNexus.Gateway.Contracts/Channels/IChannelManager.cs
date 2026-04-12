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
}
