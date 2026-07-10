using System.Collections.Generic;
using System.Diagnostics.Metrics;
using BotNexus.Gateway.Telemetry;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// MeterListener-based unit tests for <see cref="HotPathMetrics"/>. Each test asserts that
/// recording a hot-path event fires the expected instrument with the expected bounded tags,
/// per the PBI3 acceptance criteria (turn / tool / provider / cron / channel / session).
/// </summary>
public sealed class HotPathMetricsTests
{
    private static (HotPathMetrics Metrics, Meter Meter) CreateSut()
    {
        var meter = new Meter("BotNexus.Test.HotPath." + System.Guid.NewGuid().ToString("N"));
        return (new HotPathMetrics(new BotNexusMetrics(meter)), meter);
    }

    private static MeterListener ListenFor(
        Meter meter,
        string instrumentName,
        List<(double Value, KeyValuePair<string, object?>[] Tags)> sink)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter && instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            sink.Add((measurement, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
            sink.Add((measurement, tags.ToArray())));
        listener.Start();
        return listener;
    }

    private static object? Tag(KeyValuePair<string, object?>[] tags, string key)
    {
        foreach (var t in tags)
        {
            if (t.Key == key)
            {
                return t.Value;
            }
        }

        return null;
    }

    [Fact]
    public void RecordTurn_FiresCounterAndHistogram_WithBoundedTags()
    {
        var (metrics, meter) = CreateSut();

        var counter = new List<(double, KeyValuePair<string, object?>[])>();
        var histogram = new List<(double, KeyValuePair<string, object?>[])>();
        using var l1 = ListenFor(meter, "botnexus.turns.total", counter);
        using var l2 = ListenFor(meter, "botnexus.turn.duration", histogram);

        metrics.RecordTurn(agent: "farnsworth", channel: "signalr", outcome: "success", durationMs: 123.4);

        counter.Count.ShouldBe(1);
        counter[0].Item1.ShouldBe(1);
        Tag(counter[0].Item2, "agent").ShouldBe("farnsworth");
        Tag(counter[0].Item2, "channel").ShouldBe("signalr");
        Tag(counter[0].Item2, "outcome").ShouldBe("success");

        histogram.Count.ShouldBe(1);
        histogram[0].Item1.ShouldBe(123.4);
    }

    [Fact]
    public void RecordToolCall_FiresCounterAndHistogram_WithBoundedTags()
    {
        var (metrics, meter) = CreateSut();

        var counter = new List<(double, KeyValuePair<string, object?>[])>();
        var histogram = new List<(double, KeyValuePair<string, object?>[])>();
        using var l1 = ListenFor(meter, "botnexus.tool.calls", counter);
        using var l2 = ListenFor(meter, "botnexus.tool.duration", histogram);

        metrics.RecordToolCall(tool: "read", outcome: "error", durationMs: 5.0);

        counter.Count.ShouldBe(1);
        Tag(counter[0].Item2, "tool").ShouldBe("read");
        Tag(counter[0].Item2, "outcome").ShouldBe("error");
        histogram.Count.ShouldBe(1);
        histogram[0].Item1.ShouldBe(5.0);
    }

    [Fact]
    public void RecordProviderRequest_FiresRequestDurationAndTokens()
    {
        var (metrics, meter) = CreateSut();

        var requests = new List<(double, KeyValuePair<string, object?>[])>();
        var duration = new List<(double, KeyValuePair<string, object?>[])>();
        var tokens = new List<(double, KeyValuePair<string, object?>[])>();
        using var l1 = ListenFor(meter, "botnexus.provider.requests", requests);
        using var l2 = ListenFor(meter, "botnexus.provider.duration", duration);
        using var l3 = ListenFor(meter, "botnexus.provider.tokens", tokens);

        metrics.RecordProviderRequest(
            provider: "copilot",
            model: "gpt-4",
            outcome: "success",
            durationMs: 900.0,
            inputTokens: 100,
            outputTokens: 42);

        requests.Count.ShouldBe(1);
        Tag(requests[0].Item2, "provider").ShouldBe("copilot");
        Tag(requests[0].Item2, "model").ShouldBe("gpt-4");
        Tag(requests[0].Item2, "outcome").ShouldBe("success");

        duration.Count.ShouldBe(1);
        duration[0].Item1.ShouldBe(900.0);

        // input + output are separate measurements with direction tags
        tokens.Count.ShouldBe(2);
        var input = tokens.Find(t => (string?)Tag(t.Item2, "direction") == "input");
        var output = tokens.Find(t => (string?)Tag(t.Item2, "direction") == "output");
        input.Item1.ShouldBe(100);
        output.Item1.ShouldBe(42);
    }

    [Fact]
    public void RecordProviderRequest_ZeroTokens_DoesNotEmitTokenMeasurements()
    {
        var (metrics, meter) = CreateSut();

        var tokens = new List<(double, KeyValuePair<string, object?>[])>();
        using var l = ListenFor(meter, "botnexus.provider.tokens", tokens);

        metrics.RecordProviderRequest(
            provider: "copilot",
            model: "gpt-4",
            outcome: "success",
            durationMs: 10.0,
            inputTokens: 0,
            outputTokens: 0);

        tokens.ShouldBeEmpty();
    }

    [Fact]
    public void RecordCronRun_FiresCounter_WithJobAndStatus()
    {
        var (metrics, meter) = CreateSut();

        var runs = new List<(double, KeyValuePair<string, object?>[])>();
        using var l = ListenFor(meter, "botnexus.cron.runs", runs);

        metrics.RecordCronRun(job: "heartbeat", status: "success");

        runs.Count.ShouldBe(1);
        Tag(runs[0].Item2, "job").ShouldBe("heartbeat");
        Tag(runs[0].Item2, "status").ShouldBe("success");
    }

    [Fact]
    public void RecordChannelMessage_FiresCounter_WithChannelAndDirection()
    {
        var (metrics, meter) = CreateSut();

        var messages = new List<(double, KeyValuePair<string, object?>[])>();
        using var l = ListenFor(meter, "botnexus.channel.messages", messages);

        metrics.RecordChannelMessage(channel: "signalr", direction: "inbound");
        metrics.RecordChannelMessage(channel: "signalr", direction: "outbound");

        messages.Count.ShouldBe(2);
        Tag(messages[0].Item2, "channel").ShouldBe("signalr");
        Tag(messages[0].Item2, "direction").ShouldBe("inbound");
        Tag(messages[1].Item2, "direction").ShouldBe("outbound");
    }

    [Fact]
    public void ActiveSessions_ObservableGauge_SamplesProvidedValue()
    {
        var (metrics, meter) = CreateSut();

        var count = 0;
        metrics.RegisterActiveSessionsGauge(() => count);

        long? observed = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter && instrument.Name == "botnexus.sessions.active")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed = measurement);
        listener.Start();

        count = 7;
        listener.RecordObservableInstruments();

        observed.ShouldBe(7);
    }

    [Fact]
    public void Record_NullOrBlankTags_AreNormalisedToUnknown_AndNeverThrow()
    {
        var (metrics, meter) = CreateSut();

        var counter = new List<(double, KeyValuePair<string, object?>[])>();
        using var l = ListenFor(meter, "botnexus.turns.total", counter);

        Should.NotThrow(() => metrics.RecordTurn(agent: null!, channel: "   ", outcome: "", durationMs: 1.0));

        counter.Count.ShouldBe(1);
        Tag(counter[0].Item2, "agent").ShouldBe("unknown");
        Tag(counter[0].Item2, "channel").ShouldBe("unknown");
        Tag(counter[0].Item2, "outcome").ShouldBe("unknown");
    }

    [Fact]
    public void Constructor_NullMetrics_Throws()
    {
        Should.Throw<System.ArgumentNullException>(() => new HotPathMetrics(null!));
    }
}
