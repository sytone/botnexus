using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Base;

/// <summary>Manages the lifecycle of all registered channels.</summary>
public sealed class ChannelManager
{
    private readonly IReadOnlyList<IChannel> _channels;
    private readonly ILogger<ChannelManager> _logger;

    public ChannelManager(IEnumerable<IChannel> channels, ILogger<ChannelManager> logger)
    {
        _channels = [.. channels];
        _logger = logger;
    }

    /// <summary>Returns all registered channels.</summary>
    public IReadOnlyList<IChannel> Channels => _channels;

    /// <summary>Starts all registered channels.</summary>
    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting {Count} channels", _channels.Count);
        var tasks = _channels.Select(c => c.StartAsync(cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Stops all running channels.</summary>
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping all channels");
        var tasks = _channels.Where(c => c.IsRunning).Select(c => c.StopAsync(cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Gets a channel by name.</summary>
    public IChannel? GetChannel(string name)
        => _channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
