using BotNexus.Gateway.Abstractions.Channels;

namespace BotNexus.Channels.Core;

/// <summary>
/// Read-only registry for registered channel adapters.
/// </summary>
public sealed class ChannelManager : IChannelManager
{
    private readonly List<IChannelAdapter> _adapters = [];

    public ChannelManager(IEnumerable<IChannelAdapter> adapters)
    {
        _adapters.AddRange(adapters);
    }

    /// <summary>Gets all registered channel adapters.</summary>
    public IReadOnlyList<IChannelAdapter> Adapters => _adapters;

    /// <summary>Gets a channel adapter by type, or <c>null</c> if not registered.</summary>
    public IChannelAdapter? Get(string channelType)
        => _adapters.Find(a => a.ChannelType.Equals(channelType, StringComparison.OrdinalIgnoreCase));
}
