using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Background service that periodically captures memory pressure snapshots
/// for trend analysis and proactive alerting.
/// </summary>
/// <remarks>
/// Runs every 60 seconds by default. Snapshots are stored in the
/// <see cref="MemoryPressureMonitor"/> ring buffer and surfaced via
/// the diagnostics REST endpoint.
/// </remarks>
public sealed class MemoryPressureHostedService : BackgroundService
{
    private readonly MemoryPressureMonitor _monitor;
    private readonly ILogger<MemoryPressureHostedService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>
    /// Creates a new memory pressure hosted service.
    /// </summary>
    /// <param name="monitor">The memory pressure monitor singleton.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="interval">Optional override for the capture interval (default 60s).</param>
    public MemoryPressureHostedService(
        MemoryPressureMonitor monitor,
        ILogger<MemoryPressureHostedService> logger,
        TimeSpan? interval = null)
    {
        _monitor = monitor;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(60);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Memory pressure monitoring started (interval: {Interval}s)", _interval.TotalSeconds);

        // Capture an initial snapshot immediately
        _monitor.CaptureSnapshot();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            _monitor.CaptureSnapshot();
        }
    }
}
