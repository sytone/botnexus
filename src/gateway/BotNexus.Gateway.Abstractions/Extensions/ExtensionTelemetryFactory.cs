using BotNexus.Gateway.Telemetry;
using BotNexus.Persistence.Sqlite.Telemetry;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Default <see cref="IExtensionTelemetryFactory"/> (#1852). Resolves the platform
/// <see cref="IMetrics"/> facade and the shared <see cref="IUsageTelemetry"/> store from DI once and
/// mints per-extension namespaced handles on demand. Registered as a singleton by the host; safe to
/// call from any extension because each returned handle is bound to the caller-supplied id.
/// </summary>
public sealed class ExtensionTelemetryFactory : IExtensionTelemetryFactory
{
    private readonly IMetrics _metrics;
    private readonly IUsageTelemetry _usage;

    /// <summary>
    /// Creates the factory over the platform metrics facade and the shared durable usage store.
    /// </summary>
    /// <param name="metrics">The platform <see cref="IMetrics"/> facade.</param>
    /// <param name="usage">The shared durable <see cref="IUsageTelemetry"/> store.</param>
    public ExtensionTelemetryFactory(IMetrics metrics, IUsageTelemetry usage)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(usage);
        _metrics = metrics;
        _usage = usage;
    }

    /// <inheritdoc />
    public IExtensionMetrics MetricsFor(string extensionId)
        => new ExtensionMetrics(extensionId, _metrics);

    /// <inheritdoc />
    public IExtensionUsageTelemetry UsageFor(string extensionId)
        => new ExtensionUsageTelemetry(extensionId, _usage);
}
