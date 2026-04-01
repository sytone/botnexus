using System.Diagnostics.Metrics;
using BotNexus.Core.Observability;
using FluentAssertions;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-OBS-004: Metrics emitted correctly.
/// Validates that BotNexusMetrics emits correct metric instruments with proper
/// tags for messages, tool calls, provider latency, extensions, and cron jobs.
/// Uses MeterListener to capture metrics in-process without external exporters.
/// </summary>
public sealed class MetricsE2eTests : IDisposable
{
    private readonly BotNexusMetrics _metrics = new();

    [Fact]
    public void MessagesProcessed_Counter_EmitsWithChannelTag()
    {
        var measurements = CaptureCounterMeasurements<long>("botnexus.messages.processed", () =>
        {
            _metrics.IncrementMessagesProcessed("mock-web");
            _metrics.IncrementMessagesProcessed("mock-web");
            _metrics.IncrementMessagesProcessed("slack");
        });

        measurements.Should().HaveCount(3);
        measurements.Where(m => GetTag(m.Tags, "channel") == "mock-web").Should().HaveCount(2);
        measurements.Where(m => GetTag(m.Tags, "channel") == "slack").Should().HaveCount(1);
    }

    [Fact]
    public void ToolCallsExecuted_Counter_EmitsWithToolTag()
    {
        var measurements = CaptureCounterMeasurements<long>("botnexus.tool_calls.executed", () =>
        {
            _metrics.IncrementToolCallsExecuted("memory_save");
            _metrics.IncrementToolCallsExecuted("memory_search");
            _metrics.IncrementToolCallsExecuted("memory_save");
        });

        measurements.Should().HaveCount(3);
        measurements.Where(m => GetTag(m.Tags, "tool") == "memory_save").Should().HaveCount(2);
        measurements.Where(m => GetTag(m.Tags, "tool") == "memory_search").Should().HaveCount(1);
    }

    [Fact]
    public void ProviderLatency_Histogram_EmitsWithProviderTag()
    {
        var measurements = CaptureHistogramMeasurements<double>("botnexus.provider.latency", () =>
        {
            _metrics.RecordProviderLatency("copilot", 150.5);
            _metrics.RecordProviderLatency("anthropic", 200.0);
        });

        measurements.Should().HaveCount(2);
        var copilotMeasurement = measurements.Single(m => GetTag(m.Tags, "provider") == "copilot");
        copilotMeasurement.Value.Should().BeApproximately(150.5, 0.01);

        var anthropicMeasurement = measurements.Single(m => GetTag(m.Tags, "provider") == "anthropic");
        anthropicMeasurement.Value.Should().BeApproximately(200.0, 0.01);
    }

    [Fact]
    public void ExtensionsLoaded_Gauge_ReflectsCurrentCount()
    {
        _metrics.UpdateExtensionsLoaded(5);
        _metrics.UpdateExtensionsLoaded(3); // Should replace, not accumulate

        var measurements = CaptureGaugeMeasurements<int>("botnexus.extensions.loaded");

        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(3, "gauge should reflect the latest value");
    }

    [Fact]
    public void CronJobsExecuted_Counter_EmitsWithJobTag()
    {
        var measurements = CaptureCounterMeasurements<long>("botnexus.cron.jobs.executed", () =>
        {
            _metrics.IncrementCronJobsExecuted("nova-briefing");
            _metrics.IncrementCronJobsExecuted("health-check");
        });

        measurements.Should().HaveCount(2);
        measurements.Should().Contain(m => GetTag(m.Tags, "job") == "nova-briefing");
        measurements.Should().Contain(m => GetTag(m.Tags, "job") == "health-check");
    }

    [Fact]
    public void CronJobsFailed_Counter_EmitsWithJobTag()
    {
        var measurements = CaptureCounterMeasurements<long>("botnexus.cron.jobs.failed", () =>
        {
            _metrics.IncrementCronJobsFailed("broken-job");
        });

        measurements.Should().HaveCount(1);
        GetTag(measurements[0].Tags, "job").Should().Be("broken-job");
    }

    [Fact]
    public void CronJobDuration_Histogram_EmitsWithJobTag()
    {
        var measurements = CaptureHistogramMeasurements<double>("botnexus.cron.job.duration", () =>
        {
            _metrics.RecordCronJobDuration("nova-briefing", 1234.5);
        });

        measurements.Should().HaveCount(1);
        measurements[0].Value.Should().BeApproximately(1234.5, 0.01);
        GetTag(measurements[0].Tags, "job").Should().Be("nova-briefing");
    }

    [Fact]
    public void CronJobsSkipped_Counter_EmitsWithJobAndReasonTags()
    {
        var measurements = CaptureCounterMeasurements<long>("botnexus.cron.jobs.skipped", () =>
        {
            _metrics.IncrementCronJobsSkipped("maintenance-job", "still_running");
        });

        measurements.Should().HaveCount(1);
        GetTag(measurements[0].Tags, "job").Should().Be("maintenance-job");
        GetTag(measurements[0].Tags, "reason").Should().Be("still_running");
    }

    public void Dispose() => _metrics.Dispose();

    #region MeterListener Helpers

    private record struct TaggedMeasurement<T>(T Value, KeyValuePair<string, object?>[] Tags) where T : struct;

    private static List<TaggedMeasurement<T>> CaptureCounterMeasurements<T>(string instrumentName, Action action) where T : struct
    {
        var captured = new List<TaggedMeasurement<T>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "BotNexus.Platform" && instrument.Name == instrumentName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((instrument, value, tags, _) =>
        {
            if (instrument.Name == instrumentName)
                captured.Add(new TaggedMeasurement<T>(value, tags.ToArray()));
        });
        listener.Start();

        action();

        return captured;
    }

    private static List<TaggedMeasurement<T>> CaptureHistogramMeasurements<T>(string instrumentName, Action action) where T : struct
    {
        var captured = new List<TaggedMeasurement<T>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "BotNexus.Platform" && instrument.Name == instrumentName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((instrument, value, tags, _) =>
        {
            if (instrument.Name == instrumentName)
                captured.Add(new TaggedMeasurement<T>(value, tags.ToArray()));
        });
        listener.Start();

        action();

        return captured;
    }

    private static List<TaggedMeasurement<T>> CaptureGaugeMeasurements<T>(string instrumentName) where T : struct
    {
        var captured = new List<TaggedMeasurement<T>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "BotNexus.Platform" && instrument.Name == instrumentName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((instrument, value, tags, _) =>
        {
            if (instrument.Name == instrumentName)
                captured.Add(new TaggedMeasurement<T>(value, tags.ToArray()));
        });
        listener.Start();

        // For observable instruments, trigger collection
        listener.RecordObservableInstruments();

        return captured;
    }

    private static string? GetTag(KeyValuePair<string, object?>[] tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase))
                return tag.Value?.ToString();
        }
        return null;
    }

    #endregion
}
