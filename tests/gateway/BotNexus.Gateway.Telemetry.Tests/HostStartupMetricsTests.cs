using System.Diagnostics.Metrics;
using BotNexus.Gateway.Telemetry;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests for the <see cref="HostStartupMetrics"/> boot smoke counter.
/// </summary>
public sealed class HostStartupMetricsTests
{
    [Fact]
    public void CounterName_FollowsConvention()
    {
        HostStartupMetrics.StartsCounterName.ShouldBe("botnexus.host.starts");
    }

    [Fact]
    public async Task StartAsync_IncrementsSmokeCounter_ObservableViaMeterListener()
    {
        using var meter = new Meter("BotNexus.Test.Host");
        var metrics = new BotNexusMetrics(meter);

        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter && instrument.Name == HostStartupMetrics.StartsCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        var service = new HostStartupMetrics(metrics);
        await service.StartAsync(CancellationToken.None);

        observed.ShouldBe(1);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_NullMetrics_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new HostStartupMetrics(null!));
    }
}
