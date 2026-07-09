using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Emits the <c>botnexus.host.starts</c> smoke counter exactly once when the host starts.
/// This is the foundation PBI's proof-of-life measurement: it verifies the metrics plane is
/// live end-to-end (facade → Meter → MeterProvider) without instrumenting any hot path.
/// </summary>
public sealed class HostStartupMetrics : IHostedService
{
    /// <summary>Canonical name of the host-start smoke counter.</summary>
    public static readonly string StartsCounterName = BotNexusMeters.InstrumentName("host", "starts");

    private readonly System.Diagnostics.Metrics.Counter<long> _starts;

    /// <summary>
    /// Creates the hosted service and its counter instrument from the injected facade.
    /// </summary>
    /// <param name="metrics">The platform metrics facade.</param>
    public HostStartupMetrics(IMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _starts = metrics.CreateCounter<long>(
            StartsCounterName,
            unit: "{start}",
            description: "Number of times the BotNexus host process has started.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _starts.Add(1);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
