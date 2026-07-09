using System.Diagnostics.Metrics;
using BotNexus.Gateway.Telemetry;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests for the <see cref="BotNexusMetrics"/> facade over a <see cref="Meter"/>.
/// </summary>
public sealed class BotNexusMetricsTests
{
    [Fact]
    public void CreateCounter_EmitsMeasurements_ObservableViaMeterListener()
    {
        using var meter = new Meter("BotNexus.Test.Counter");
        var metrics = new BotNexusMetrics(meter);

        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        var counter = metrics.CreateCounter<long>("botnexus.test.counter");
        counter.Add(3);
        counter.Add(4);

        observed.ShouldBe(7);
    }

    [Fact]
    public void CreateHistogram_RecordsMeasurements_ObservableViaMeterListener()
    {
        using var meter = new Meter("BotNexus.Test.Histogram");
        var metrics = new BotNexusMetrics(meter);

        var recorded = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => recorded.Add(measurement));
        listener.Start();

        var histogram = metrics.CreateHistogram<double>("botnexus.test.latency", unit: "ms");
        histogram.Record(1.5);
        histogram.Record(2.5);

        recorded.ShouldBe(new[] { 1.5, 2.5 });
    }

    [Fact]
    public void CreateUpDownCounter_TracksNetValue_ObservableViaMeterListener()
    {
        using var meter = new Meter("BotNexus.Test.UpDown");
        var metrics = new BotNexusMetrics(meter);

        long net = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => net += measurement);
        listener.Start();

        var updown = metrics.CreateUpDownCounter<long>("botnexus.test.active");
        updown.Add(5);
        updown.Add(-2);

        net.ShouldBe(3);
    }

    [Fact]
    public void CreateObservableGauge_SamplesValue_OnCollection()
    {
        using var meter = new Meter("BotNexus.Test.Gauge");
        var metrics = new BotNexusMetrics(meter);

        var current = 42;
        long? observed = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed = measurement);
        listener.Start();

        _ = metrics.CreateObservableGauge("botnexus.test.gauge", () => (long)current);
        listener.RecordObservableInstruments();

        observed.ShouldBe(42);
    }

    [Fact]
    public void Constructor_NullMeter_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new BotNexusMetrics(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateCounter_BlankName_Throws(string name)
    {
        using var meter = new Meter("BotNexus.Test.Blank");
        var metrics = new BotNexusMetrics(meter);

        Should.Throw<ArgumentException>(() => metrics.CreateCounter<long>(name));
    }

    [Fact]
    public void CreateObservableGauge_NullCallback_Throws()
    {
        using var meter = new Meter("BotNexus.Test.NullCb");
        var metrics = new BotNexusMetrics(meter);

        Should.Throw<ArgumentNullException>(
            () => metrics.CreateObservableGauge<long>("botnexus.test.gauge", null!));
    }

    [Fact]
    public void DefaultConstructor_UsesCanonicalMeter()
    {
        var metrics = new BotNexusMetrics();

        // Instruments created here belong to the canonical "BotNexus" scope; a listener
        // filtering on that meter name should see the measurement.
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BotNexusMeters.Name
                && instrument.Name == "botnexus.test.canonical")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        var counter = metrics.CreateCounter<long>("botnexus.test.canonical");
        counter.Add(11);

        observed.ShouldBe(11);
    }
}
