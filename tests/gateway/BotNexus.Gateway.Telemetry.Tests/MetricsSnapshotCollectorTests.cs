using System.Linq;
using BotNexus.Gateway.Telemetry;
using BotNexus.Gateway.Telemetry.Snapshot;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests for the metrics read surface (#1853): <see cref="MetricsSnapshotCollector"/> accumulates
/// hot-path measurements from the canonical BotNexus meter and produces a JSON-serialisable snapshot.
/// Uses <see cref="HotPathMetrics"/> over the process-wide canonical meter (the collector listens to
/// the canonical scope by name), then asserts the snapshot reflects recorded values.
/// </summary>
public sealed class MetricsSnapshotCollectorTests
{
    [Fact]
    public void Snapshot_EmptyState_ReturnsWellFormedSnapshotWithNoUnexpectedData()
    {
        using var collector = new MetricsSnapshotCollector();

        var snapshot = collector.Snapshot();

        snapshot.ShouldNotBeNull();
        snapshot.Scope.ShouldBe(BotNexusMeters.Name);
        snapshot.GeneratedAt.ShouldNotBe(default);
        // Instruments collection is always well-formed (never null).
        snapshot.Instruments.ShouldNotBeNull();
    }

    [Fact]
    public void Snapshot_AfterRecordingTurn_ContainsCounterAndHistogramWithAccumulatedValues()
    {
        using var collector = new MetricsSnapshotCollector();
        var hotPath = new HotPathMetrics(new BotNexusMetrics());

        var agent = "farnsworth-" + Guid.NewGuid().ToString("N");
        hotPath.RecordTurn(agent: agent, channel: "signalr", outcome: "success", durationMs: 100.0);
        hotPath.RecordTurn(agent: agent, channel: "signalr", outcome: "success", durationMs: 300.0);

        var snapshot = collector.Snapshot();

        var counter = snapshot.Instruments.SingleOrDefault(i => i.Name == "botnexus.turns.total");
        counter.ShouldNotBeNull();
        counter.Kind.ShouldBe("counter");
        var counterRow = counter.Measurements.Single(m => (m.Tags.GetValueOrDefault("agent")) == agent);
        counterRow.Value.ShouldBe(2);

        var histogram = snapshot.Instruments.SingleOrDefault(i => i.Name == "botnexus.turn.duration");
        histogram.ShouldNotBeNull();
        histogram.Kind.ShouldBe("histogram");
        var histRow = histogram.Measurements.Single(m => m.Tags.GetValueOrDefault("agent") == agent);
        histRow.Count.ShouldBe(2);
        histRow.Sum.ShouldBe(400.0);
        histRow.Min.ShouldBe(100.0);
        histRow.Max.ShouldBe(300.0);
    }

    [Fact]
    public void Snapshot_ObservableGauge_SamplesCurrentValue()
    {
        using var collector = new MetricsSnapshotCollector();
        var hotPath = new HotPathMetrics(new BotNexusMetrics());

        var current = 5L;
        hotPath.RegisterActiveSessionsGauge(() => current);

        current = 9;
        var snapshot = collector.Snapshot();

        var gauge = snapshot.Instruments.SingleOrDefault(i => i.Name == "botnexus.sessions.active");
        gauge.ShouldNotBeNull();
        gauge.Kind.ShouldBe("gauge");
        gauge.Measurements.Single().Value.ShouldBe(9);
    }

    [Fact]
    public void Snapshot_TagOrderingAtCallSite_DoesNotFragmentAccumulation()
    {
        using var collector = new MetricsSnapshotCollector();
        var hotPath = new HotPathMetrics(new BotNexusMetrics());

        var tool = "read-" + Guid.NewGuid().ToString("N");
        hotPath.RecordToolCall(tool: tool, outcome: "success", durationMs: 1.0);
        hotPath.RecordToolCall(tool: tool, outcome: "success", durationMs: 2.0);

        var snapshot = collector.Snapshot();

        var counter = snapshot.Instruments.Single(i => i.Name == "botnexus.tool.calls");
        var rows = counter.Measurements.Where(m => m.Tags.GetValueOrDefault("tool") == tool).ToList();
        rows.Count.ShouldBe(1);
        rows[0].Value.ShouldBe(2);
    }
}
