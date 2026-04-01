using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Heartbeat;

/// <summary>
/// Heartbeat service that periodically records system health and runs as an IHostedService.
/// </summary>
public sealed class HeartbeatService : BackgroundService, IHeartbeatService
{
    private readonly ILogger<HeartbeatService> _logger;
    private readonly HeartbeatConfig _config;
    private DateTimeOffset? _lastBeat;

    public HeartbeatService(ILogger<HeartbeatService> logger, IOptions<BotNexusConfig> config)
    {
        _logger = logger;
        _config = config.Value.Gateway.Heartbeat;
    }

    /// <inheritdoc/>
    public void Beat()
    {
        _lastBeat = DateTimeOffset.UtcNow;
        _logger.LogDebug("Heartbeat recorded at {Time}", _lastBeat);
    }

    /// <inheritdoc/>
    public DateTimeOffset? LastBeat => _lastBeat;

    /// <inheritdoc/>
    public bool IsHealthy =>
        _lastBeat.HasValue &&
        DateTimeOffset.UtcNow - _lastBeat.Value < TimeSpan.FromSeconds(_config.IntervalSeconds * 2);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Heartbeat service disabled");
            return;
        }

        _logger.LogInformation("Heartbeat service started, interval: {Interval}s", _config.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            Beat();
            _logger.LogInformation("💓 BotNexus heartbeat at {Time}", _lastBeat);
            await Task.Delay(TimeSpan.FromSeconds(_config.IntervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }
}
