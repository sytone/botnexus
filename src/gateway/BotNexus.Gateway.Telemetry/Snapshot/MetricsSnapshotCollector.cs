using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Telemetry.Snapshot;

/// <summary>
/// In-process metrics reader (#1853). Subscribes a <see cref="MeterListener"/> to the canonical
/// <see cref="BotNexusMeters.Name"/> instrumentation scope and accumulates every measurement so the
/// metrics read endpoint can serve a JSON snapshot of current instrument values without an external
/// OpenTelemetry collector.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately lightweight aggregator, not a full OTel SDK: counters and up/down counters
/// accumulate a running sum per tag-set, histograms accumulate count/sum/min/max, and observable
/// gauges hold their latest sampled value. Observable instruments are sampled on demand when
/// <see cref="Snapshot"/> is called via <see cref="MeterListener.RecordObservableInstruments"/>.
/// </para>
/// <para>
/// Registered as a singleton so accumulation spans the whole process lifetime. Only instruments on
/// the canonical <c>BotNexus</c> meter are observed, keeping the snapshot scoped to platform metrics
/// and ignoring the AspNetCore/Http instrumentation meters.
/// </para>
/// </remarks>
public sealed class MetricsSnapshotCollector : IHostedService, IDisposable
{
    private readonly MeterListener _listener;
    private readonly TimeProvider _timeProvider;

    // instrument name -> (metadata, tag-set -> accumulated state). ConcurrentDictionary because
    // measurement callbacks fire on arbitrary hot-path threads while a request may read concurrently.
    private readonly ConcurrentDictionary<string, InstrumentState> _instruments = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the collector and starts listening to the canonical BotNexus meter. Instruments that
    /// already exist on the scope at construction time are picked up via the listener's initial
    /// publish pass.
    /// </summary>
    /// <param name="timeProvider">Clock used to stamp snapshots; defaults to <see cref="TimeProvider.System"/>.</param>
    public MetricsSnapshotCollector(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _listener = new MeterListener
        {
            InstrumentPublished = OnInstrumentPublished
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurementLong);
        _listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
        _listener.SetMeasurementEventCallback<int>(OnMeasurementInt);
        _listener.Start();
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        // Only observe the canonical platform scope; ignore AspNetCore/Http instrumentation meters.
        if (!string.Equals(instrument.Meter.Name, BotNexusMeters.Name, StringComparison.Ordinal))
        {
            return;
        }

        _instruments.TryAdd(instrument.Name, new InstrumentState(
            instrument.Name,
            KindOf(instrument),
            instrument.Unit,
            instrument.Description));
        listener.EnableMeasurementEvents(instrument);
    }

    private void OnMeasurementLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, measurement, tags);

    private void OnMeasurementInt(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, measurement, tags);

    private void OnMeasurementDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, measurement, tags);

    private void Record(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (!_instruments.TryGetValue(instrument.Name, out var instrumentState))
        {
            return;
        }

        var key = BuildTagKey(tags, out var tagDictionary);
        var isGauge = string.Equals(instrumentState.Kind, "gauge", StringComparison.Ordinal);
        var isHistogram = string.Equals(instrumentState.Kind, "histogram", StringComparison.Ordinal);

        var measurementState = instrumentState.Measurements.GetOrAdd(key, _ => new MeasurementState(tagDictionary));
        lock (measurementState.SyncRoot)
        {
            if (isGauge)
            {
                // Observable gauge: keep the latest sampled value rather than a running sum.
                measurementState.Value = measurement;
            }
            else
            {
                measurementState.Value += measurement;
            }

            if (isHistogram)
            {
                measurementState.Count += 1;
                measurementState.Sum += measurement;
                measurementState.Min = measurementState.Count == 1 ? measurement : Math.Min(measurementState.Min, measurement);
                measurementState.Max = measurementState.Count == 1 ? measurement : Math.Max(measurementState.Max, measurement);
            }
        }
    }

    /// <summary>
    /// Produces a point-in-time snapshot of every observed instrument. Observable instruments (gauges)
    /// are sampled synchronously as part of this call, so the returned gauge values reflect the current
    /// state at the moment of the request.
    /// </summary>
    public MetricsSnapshotResponse Snapshot()
    {
        // Sample observable instruments (e.g. botnexus.sessions.active) so gauges reflect current state.
        _listener.RecordObservableInstruments();

        var instruments = _instruments.Values
            .Select(ToDto)
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        return new MetricsSnapshotResponse
        {
            GeneratedAt = _timeProvider.GetUtcNow(),
            Scope = BotNexusMeters.Name,
            Instruments = instruments
        };
    }

    private static InstrumentSnapshotDto ToDto(InstrumentState state)
    {
        var measurements = state.Measurements.Values
            .Select(m =>
            {
                lock (m.SyncRoot)
                {
                    var isHistogram = m.Count > 0;
                    return new MeasurementSnapshotDto
                    {
                        Tags = m.Tags,
                        Value = m.Value,
                        Count = isHistogram ? m.Count : null,
                        Sum = isHistogram ? m.Sum : null,
                        Min = isHistogram ? m.Min : null,
                        Max = isHistogram ? m.Max : null
                    };
                }
            })
            .OrderBy(m => string.Join(",", m.Tags.Select(t => $"{t.Key}={t.Value}")), StringComparer.Ordinal)
            .ToList();

        return new InstrumentSnapshotDto
        {
            Name = state.Name,
            Kind = state.Kind,
            Unit = state.Unit,
            Description = state.Description,
            Measurements = measurements
        };
    }

    private static string KindOf(Instrument instrument)
    {
        var typeName = instrument.GetType().Name;
        if (typeName.StartsWith("ObservableGauge", StringComparison.Ordinal) || typeName.StartsWith("Gauge", StringComparison.Ordinal))
        {
            return "gauge";
        }
        if (typeName.StartsWith("Histogram", StringComparison.Ordinal))
        {
            return "histogram";
        }
        if (typeName.StartsWith("UpDownCounter", StringComparison.Ordinal) || typeName.StartsWith("ObservableUpDownCounter", StringComparison.Ordinal))
        {
            return "updowncounter";
        }
        return "counter";
    }

    private static string BuildTagKey(ReadOnlySpan<KeyValuePair<string, object?>> tags, out IReadOnlyDictionary<string, string> tagDictionary)
    {
        if (tags.Length == 0)
        {
            tagDictionary = new Dictionary<string, string>();
            return string.Empty;
        }

        // Sort by key so tag ordering at the call site does not fragment the accumulation key.
        var ordered = new List<KeyValuePair<string, string>>(tags.Length);
        foreach (var tag in tags)
        {
            ordered.Add(new KeyValuePair<string, string>(tag.Key, tag.Value?.ToString() ?? string.Empty));
        }
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var dictionary = new Dictionary<string, string>(ordered.Count, StringComparer.Ordinal);
        foreach (var tag in ordered)
        {
            dictionary[tag.Key] = tag.Value;
        }
        tagDictionary = dictionary;

        return string.Join("\u001f", ordered.Select(t => $"{t.Key}={t.Value}"));
    }

    /// <summary>No-op: the listener starts in the constructor so accumulation begins as soon as the
    /// singleton is instantiated at host startup, before any hot path records a measurement.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private sealed class InstrumentState(string name, string kind, string? unit, string? description)
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public string? Unit { get; } = unit;
        public string? Description { get; } = description;
        public ConcurrentDictionary<string, MeasurementState> Measurements { get; } = new(StringComparer.Ordinal);
    }

    private sealed class MeasurementState(IReadOnlyDictionary<string, string> tags)
    {
        public object SyncRoot { get; } = new();
        public IReadOnlyDictionary<string, string> Tags { get; } = tags;
        public double Value { get; set; }
        public long Count { get; set; }
        public double Sum { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
    }
}
