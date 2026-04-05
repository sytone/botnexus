using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Core;

/// <summary>
/// Manages the lifecycle of all registered channel adapters.
/// Provides a central point to start, stop, and query adapters.
/// </summary>
public sealed class ChannelManager
{
    private readonly List<IChannelAdapter> _adapters = [];
    private readonly ILogger<ChannelManager> _logger;

    public ChannelManager(IEnumerable<IChannelAdapter> adapters, ILogger<ChannelManager> logger)
    {
        _adapters.AddRange(adapters);
        _logger = logger;
    }

    /// <summary>Gets all registered channel adapters.</summary>
    public IReadOnlyList<IChannelAdapter> Adapters => _adapters;

    /// <summary>Gets a channel adapter by type, or <c>null</c> if not registered.</summary>
    public IChannelAdapter? Get(string channelType)
        => _adapters.Find(a => a.ChannelType.Equals(channelType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts all registered channel adapters.</summary>
    public async Task StartAllAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters)
        {
            try
            {
                await adapter.StartAsync(dispatcher, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start channel adapter '{ChannelType}'", adapter.ChannelType);
            }
        }
    }

    /// <summary>Stops all running channel adapters.</summary>
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters.Where(a => a.IsRunning))
        {
            try
            {
                await adapter.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop channel adapter '{ChannelType}'", adapter.ChannelType);
            }
        }
    }
}
