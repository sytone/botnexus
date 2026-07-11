using System.Collections.Generic;

namespace BotNexus.Gateway.Telemetry.Snapshot;

/// <summary>
/// JSON response body for the metrics read surface (#1853): a point-in-time snapshot of every
/// instrument the in-process <see cref="MetricsSnapshotCollector"/> has observed on the canonical
/// <c>BotNexus</c> meter scope. Lets operators and the portal read PBI3 hot-path metrics without an
/// external OpenTelemetry collector.
/// </summary>
public sealed class MetricsSnapshotResponse
{
    /// <summary>UTC timestamp the snapshot was produced.</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>The canonical instrumentation scope the snapshot was collected from (always <c>BotNexus</c>).</summary>
    public string Scope { get; set; } = BotNexusMeters.Name;

    /// <summary>Every instrument observed since collection started, ordered by instrument name.</summary>
    public IReadOnlyList<InstrumentSnapshotDto> Instruments { get; set; } = [];
}

/// <summary>
/// A single instrument's accumulated state in a <see cref="MetricsSnapshotResponse"/>. One instrument
/// may carry several <see cref="MeasurementSnapshotDto"/> rows - one per distinct tag combination.
/// </summary>
public sealed class InstrumentSnapshotDto
{
    /// <summary>Convention-compliant instrument name, e.g. <c>botnexus.turns.total</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Instrument kind: <c>counter</c>, <c>updowncounter</c>, <c>histogram</c>, or <c>gauge</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Optional UCUM unit string reported by the instrument.</summary>
    public string? Unit { get; set; }

    /// <summary>Optional human-readable instrument description.</summary>
    public string? Description { get; set; }

    /// <summary>One row per distinct tag combination observed for this instrument.</summary>
    public IReadOnlyList<MeasurementSnapshotDto> Measurements { get; set; } = [];
}

/// <summary>
/// Accumulated measurement state for one instrument/tag-set pair. Counters and up/down counters expose
/// a running <see cref="Value"/> (sum of deltas); histograms additionally expose distribution summary
/// fields (<see cref="Count"/>, <see cref="Sum"/>, <see cref="Min"/>, <see cref="Max"/>); gauges expose
/// the most recently sampled <see cref="Value"/>.
/// </summary>
public sealed class MeasurementSnapshotDto
{
    /// <summary>The bounded tag dimensions attached to this measurement stream.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// For counters/up-down counters: the running total. For gauges: the latest sampled value. For
    /// histograms: the sum of all recorded values (mirrors <see cref="Sum"/>).
    /// </summary>
    public double Value { get; set; }

    /// <summary>Number of recorded values (histograms only; <c>null</c> otherwise).</summary>
    public long? Count { get; set; }

    /// <summary>Sum of recorded values (histograms only; <c>null</c> otherwise).</summary>
    public double? Sum { get; set; }

    /// <summary>Smallest recorded value (histograms only; <c>null</c> otherwise).</summary>
    public double? Min { get; set; }

    /// <summary>Largest recorded value (histograms only; <c>null</c> otherwise).</summary>
    public double? Max { get; set; }
}
