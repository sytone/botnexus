using System.Linq;
using BotNexus.Gateway.Telemetry;
using BotNexus.Gateway.Telemetry.Snapshot;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

/// <summary>
/// Tests the metrics read endpoint handler (#1853): <c>GET /api/telemetry/metrics</c>. Drives the
/// static handler directly (mirroring the skills telemetry endpoint tests) with a real
/// <see cref="MetricsSnapshotCollector"/> so no web host is required.
/// </summary>
public sealed class TelemetryEndpointContributorTests
{
    [Fact]
    public void GetMetrics_WithNullCollector_ReturnsEmptySnapshot()
    {
        var result = TelemetryEndpointContributor.GetMetrics(collector: null);

        var ok = result.ShouldBeOfType<Ok<MetricsSnapshotResponse>>();
        ok.Value!.Instruments.ShouldBeEmpty();
        ok.Value.Scope.ShouldBe(BotNexusMeters.Name);
    }

    [Fact]
    public void GetMetrics_EmptyCollector_ReturnsWellFormedSnapshot()
    {
        using var collector = new MetricsSnapshotCollector();

        var result = TelemetryEndpointContributor.GetMetrics(collector);

        var ok = result.ShouldBeOfType<Ok<MetricsSnapshotResponse>>();
        ok.Value!.Scope.ShouldBe(BotNexusMeters.Name);
        ok.Value.Instruments.ShouldNotBeNull();
    }

    [Fact]
    public void GetMetrics_PopulatedCollector_ReturnsRecordedInstruments()
    {
        using var collector = new MetricsSnapshotCollector();
        var hotPath = new HotPathMetrics(new BotNexusMetrics());

        var provider = "copilot-" + Guid.NewGuid().ToString("N");
        hotPath.RecordProviderRequest(provider: provider, model: "gpt-4", outcome: "success", durationMs: 50.0, inputTokens: 10, outputTokens: 4);

        var result = TelemetryEndpointContributor.GetMetrics(collector);

        var ok = result.ShouldBeOfType<Ok<MetricsSnapshotResponse>>();
        var requests = ok.Value!.Instruments.SingleOrDefault(i => i.Name == "botnexus.provider.requests");
        requests.ShouldNotBeNull();
        var row = requests.Measurements.Single(m => m.Tags.GetValueOrDefault("provider") == provider);
        row.Value.ShouldBe(1);

        var tokens = ok.Value.Instruments.Single(i => i.Name == "botnexus.provider.tokens");
        var tokenRows = tokens.Measurements.Where(m => m.Tags.GetValueOrDefault("provider") == provider).ToList();
        tokenRows.Sum(r => r.Value).ShouldBe(14);
    }
}
