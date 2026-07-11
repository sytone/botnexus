using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Gateway.Telemetry.Snapshot;

/// <summary>
/// Registers the metrics read surface (#1853) as minimal API routes under <c>/api/telemetry</c>.
/// Exposes a JSON snapshot of current instrument values so operators and the portal can inspect the
/// PBI3 hot-path metrics locally without an external OpenTelemetry collector.
/// </summary>
/// <remarks>
/// Wired via the endpoint-contributor pattern (resolved by
/// <c>AssemblyLoadContextExtensionLoader.MapExtensionEndpoints</c> from DI) so the read endpoint is
/// added without editing <c>Program.cs</c> - keeping this change disjoint from the in-flight
/// telemetry-ext-seam PR (#1920) that owns <c>Program.cs</c>.
/// </remarks>
public sealed class TelemetryEndpointContributor : IEndpointContributor
{
    /// <inheritdoc />
    public void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/telemetry");
        group.MapGet("/metrics", (MetricsSnapshotCollector? collector) => GetMetrics(collector));
    }

    /// <summary>
    /// Returns the current metrics snapshot as JSON (#1853). Returns an empty, well-formed snapshot when
    /// no collector is registered (telemetry disabled) so the endpoint is always safe for UI consumers.
    /// </summary>
    internal static IResult GetMetrics(MetricsSnapshotCollector? collector)
    {
        if (collector is null)
        {
            return Results.Ok(new MetricsSnapshotResponse
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Scope = BotNexusMeters.Name,
                Instruments = []
            });
        }

        return Results.Ok(collector.Snapshot());
    }
}
