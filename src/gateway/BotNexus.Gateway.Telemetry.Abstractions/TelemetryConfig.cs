namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Binds the <c>telemetry</c> configuration section. This PBI lands only the in-process
/// metrics/tracing foundation; exporter (OTLP) configuration is deferred to a later PBI,
/// so there is deliberately no egress/endpoint surface here yet.
/// </summary>
public sealed class TelemetryConfig
{
    /// <summary>Configuration section name under the root config.</summary>
    public const string SectionName = "telemetry";

    /// <summary>
    /// Whether the in-process OpenTelemetry metrics/tracing plane is active. Defaults to
    /// <see langword="true"/>. When <see langword="false"/>, <c>AddBotNexusTelemetry</c>
    /// still registers <see cref="IMetrics"/> (so call sites resolve) but does not wire the
    /// OpenTelemetry MeterProvider/TracerProvider.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
