using BotNexus.Gateway.Abstractions.Channels;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
namespace BotNexus.Gateway.Channels;
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
    public IChannelAdapter? Get(ChannelKey channelType)
        => _adapters.Find(a => a.ChannelType.Equals(channelType));

    /// <summary>
    /// Gets a channel adapter by type and optional adapter ID.
    /// Falls back to the first adapter of the given type when adapterId is null or not found.
    /// Returns null when no adapters of that type are registered.
    /// </summary>
    public IChannelAdapter? Get(ChannelKey channelType, string? adapterId)
    {
        var ofType = _adapters.FindAll(a => a.ChannelType.Equals(channelType));
        if (ofType.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(adapterId))
        {
            var match = ofType.Find(a => string.Equals(a.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return ofType[0];
    }
}
