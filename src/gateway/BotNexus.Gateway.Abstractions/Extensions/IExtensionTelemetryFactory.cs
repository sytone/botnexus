using BotNexus.Gateway.Telemetry;
using BotNexus.Persistence.Sqlite.Telemetry;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// The single DI entry point an extension resolves to obtain its namespaced telemetry handles
/// (#1852). Because the extension id is only known to the extension itself (not to the host at
/// container-build time), the host registers this factory as a singleton and each extension calls
/// <see cref="ForExtension(string)"/> with its own id to get an <see cref="IExtensionMetrics"/>
/// (auto-prefixed to <c>botnexus.ext.&lt;id&gt;.*</c>) and an <see cref="IExtensionUsageTelemetry"/>
/// (namespaced to the id over the shared durable store). This is the sanctioned, non-privileged
/// path: it wraps the same <see cref="IMetrics"/> and <see cref="IUsageTelemetry"/> the platform
/// core uses, with the extension guardrails applied.
/// </summary>
public interface IExtensionTelemetryFactory
{
    /// <summary>
    /// Returns a namespaced metrics handle for <paramref name="extensionId"/>. Every instrument the
    /// handle creates is prefixed to <c>botnexus.ext.&lt;extensionId&gt;.*</c> and validated so it
    /// cannot collide with a platform instrument.
    /// </summary>
    IExtensionMetrics MetricsFor(string extensionId);

    /// <summary>
    /// Returns a durable usage-telemetry handle for <paramref name="extensionId"/> bound to the
    /// consumer namespace equal to the id, backed by the shared store (no per-extension SQLite file).
    /// </summary>
    IExtensionUsageTelemetry UsageFor(string extensionId);
}
