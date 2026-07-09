using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Canonical telemetry identifiers for the BotNexus platform. All platform metrics
/// and traces flow through the single named <see cref="Meter"/> and
/// <see cref="ActivitySource"/> defined here so that exporters can subscribe by a
/// stable, well-known scope name rather than each component minting its own.
/// </summary>
/// <remarks>
/// Instrument names follow the convention <c>botnexus.&lt;area&gt;.&lt;instrument&gt;</c>
/// (e.g. <c>botnexus.host.starts</c>). The scope name intentionally has no dots so it
/// reads as a product identifier; the dotted convention applies to instruments only.
/// </remarks>
public static class BotNexusMeters
{
    /// <summary>
    /// The canonical meter/activity scope name. Exporters subscribe with
    /// <c>AddMeter("BotNexus")</c> / <c>AddSource("BotNexus")</c>.
    /// </summary>
    public const string Name = "BotNexus";

    /// <summary>
    /// Instrumentation scope version, pinned to the telemetry assembly version so
    /// dashboards can distinguish instrument-shape changes across releases.
    /// </summary>
    public static readonly string Version =
        typeof(BotNexusMeters).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BotNexusMeters).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// The single platform <see cref="System.Diagnostics.Metrics.Meter"/>. Prefer resolving
    /// <see cref="IMetrics"/> from DI in application code; this instance exists for the facade
    /// and for static call sites that cannot take a constructor dependency.
    /// </summary>
    public static readonly Meter Meter = new(Name, Version);

    /// <summary>
    /// The single platform <see cref="System.Diagnostics.ActivitySource"/> for tracing.
    /// No custom spans are emitted yet; this is the scaffold PBI2+ will build on.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(Name, Version);

    /// <summary>
    /// Builds a convention-compliant instrument name of the form
    /// <c>botnexus.&lt;area&gt;.&lt;instrument&gt;</c>.
    /// </summary>
    /// <param name="area">Logical subsystem, e.g. <c>host</c>, <c>session</c>.</param>
    /// <param name="instrument">Instrument leaf name, e.g. <c>starts</c>.</param>
    public static string InstrumentName(string area, string instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        return $"botnexus.{area}.{instrument}";
    }
}
