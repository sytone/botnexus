using System.Diagnostics.Metrics;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Thin, injectable facade over the canonical BotNexus <see cref="Meter"/>. Application
/// code depends on this interface rather than newing up its own <see cref="Meter"/> so that
/// (a) every instrument lands in the single well-known instrumentation scope exporters
/// subscribe to, and (b) call sites remain mockable in unit tests.
/// </summary>
/// <remarks>
/// Instrument names must follow the <c>botnexus.&lt;area&gt;.&lt;instrument&gt;</c> convention;
/// use <see cref="BotNexusMeters.InstrumentName(string, string)"/> to build them.
/// </remarks>
public interface IMetrics
{
    /// <summary>
    /// Creates a monotonically increasing counter (e.g. total host starts, messages processed).
    /// </summary>
    /// <typeparam name="T">The measurement value type (must be a struct).</typeparam>
    /// <param name="name">Convention-compliant instrument name.</param>
    /// <param name="unit">Optional UCUM unit string.</param>
    /// <param name="description">Optional human-readable description.</param>
    Counter<T> CreateCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct;

    /// <summary>
    /// Creates a histogram for recording a distribution of values (e.g. request latency).
    /// </summary>
    /// <typeparam name="T">The measurement value type (must be a struct).</typeparam>
    /// <param name="name">Convention-compliant instrument name.</param>
    /// <param name="unit">Optional UCUM unit string.</param>
    /// <param name="description">Optional human-readable description.</param>
    Histogram<T> CreateHistogram<T>(string name, string? unit = null, string? description = null)
        where T : struct;

    /// <summary>
    /// Creates an up/down counter for values that can increase and decrease
    /// (e.g. active sessions, queue depth).
    /// </summary>
    /// <typeparam name="T">The measurement value type (must be a struct).</typeparam>
    /// <param name="name">Convention-compliant instrument name.</param>
    /// <param name="unit">Optional UCUM unit string.</param>
    /// <param name="description">Optional human-readable description.</param>
    UpDownCounter<T> CreateUpDownCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct;

    /// <summary>
    /// Registers an observable gauge whose value is sampled on demand from
    /// <paramref name="observeValue"/> at collection time.
    /// </summary>
    /// <typeparam name="T">The measurement value type (must be a struct).</typeparam>
    /// <param name="name">Convention-compliant instrument name.</param>
    /// <param name="observeValue">Callback invoked by the SDK to sample the current value.</param>
    /// <param name="unit">Optional UCUM unit string.</param>
    /// <param name="description">Optional human-readable description.</param>
    ObservableGauge<T> CreateObservableGauge<T>(
        string name,
        Func<T> observeValue,
        string? unit = null,
        string? description = null)
        where T : struct;
}
