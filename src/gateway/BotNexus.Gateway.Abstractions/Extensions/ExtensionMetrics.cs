using System.Diagnostics.Metrics;
using BotNexus.Gateway.Telemetry;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Default <see cref="IExtensionMetrics"/> implementation. A thin decorator over the platform
/// <see cref="IMetrics"/> facade that auto-prefixes and validates every instrument name into the
/// owning extension's <c>botnexus.ext.&lt;id&gt;.*</c> namespace via
/// <see cref="ExtensionMeters.InstrumentName(string, string)"/>. Instruments still land on the
/// single canonical <see cref="BotNexusMeters"/> scope, so exporters observe them exactly as they
/// observe platform instruments - the seam is not privileged, only namespaced.
/// </summary>
public sealed class ExtensionMetrics : IExtensionMetrics
{
    private readonly string _extensionId;
    private readonly IMetrics _inner;

    /// <summary>
    /// Creates a namespaced metrics handle for <paramref name="extensionId"/> over the platform
    /// <paramref name="inner"/> facade.
    /// </summary>
    /// <param name="extensionId">The owning extension id (validated).</param>
    /// <param name="inner">The platform metrics facade instruments are created on.</param>
    public ExtensionMetrics(string extensionId, IMetrics inner)
    {
        _extensionId = ExtensionMeters.ValidateExtensionId(extensionId);
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string ExtensionId => _extensionId;

    /// <inheritdoc />
    public Counter<T> CreateCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct
        => _inner.CreateCounter<T>(ExtensionMeters.InstrumentName(_extensionId, name), unit, description);

    /// <inheritdoc />
    public Histogram<T> CreateHistogram<T>(string name, string? unit = null, string? description = null)
        where T : struct
        => _inner.CreateHistogram<T>(ExtensionMeters.InstrumentName(_extensionId, name), unit, description);

    /// <inheritdoc />
    public UpDownCounter<T> CreateUpDownCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct
        => _inner.CreateUpDownCounter<T>(ExtensionMeters.InstrumentName(_extensionId, name), unit, description);

    /// <inheritdoc />
    public ObservableGauge<T> CreateObservableGauge<T>(
        string name,
        Func<T> observeValue,
        string? unit = null,
        string? description = null)
        where T : struct
        => _inner.CreateObservableGauge(ExtensionMeters.InstrumentName(_extensionId, name), observeValue, unit, description);
}
