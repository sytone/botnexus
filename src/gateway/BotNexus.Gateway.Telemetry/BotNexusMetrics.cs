using System.Diagnostics.Metrics;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Default <see cref="IMetrics"/> implementation backed by the single canonical
/// <see cref="BotNexusMeters.Meter"/>. Registered as a singleton by
/// <c>AddBotNexusTelemetry</c>. Kept allocation-light: it holds no state beyond the
/// shared meter reference and simply forwards instrument creation.
/// </summary>
public sealed class BotNexusMetrics : IMetrics
{
    private readonly Meter _meter;

    /// <summary>
    /// Creates a facade over the canonical platform meter.
    /// </summary>
    public BotNexusMetrics()
        : this(BotNexusMeters.Meter)
    {
    }

    /// <summary>
    /// Creates a facade over an explicit meter. Primarily for tests that want to observe
    /// instruments through a dedicated <see cref="MeterListener"/> without touching the
    /// process-wide canonical meter.
    /// </summary>
    /// <param name="meter">The meter instruments are created on.</param>
    public BotNexusMetrics(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
    }

    /// <inheritdoc />
    public Counter<T> CreateCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _meter.CreateCounter<T>(name, unit, description);
    }

    /// <inheritdoc />
    public Histogram<T> CreateHistogram<T>(string name, string? unit = null, string? description = null)
        where T : struct
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _meter.CreateHistogram<T>(name, unit, description);
    }

    /// <inheritdoc />
    public UpDownCounter<T> CreateUpDownCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _meter.CreateUpDownCounter<T>(name, unit, description);
    }

    /// <inheritdoc />
    public ObservableGauge<T> CreateObservableGauge<T>(
        string name,
        Func<T> observeValue,
        string? unit = null,
        string? description = null)
        where T : struct
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(observeValue);
        return _meter.CreateObservableGauge(name, observeValue, unit, description);
    }
}
