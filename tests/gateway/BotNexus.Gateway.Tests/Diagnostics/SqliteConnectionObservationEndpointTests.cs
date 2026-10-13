using System.Text.Json;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class SqliteConnectionObservationEndpointTests
{
    [Fact]
    public void CurrentEndpoint_CapturesObservationAndHistoryPreservesSameImmutableValue()
    {
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance);
        var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance, memoryMonitor: monitor);
        var dto = controller.GetMemoryPressure().ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureDto>();
        var stored = monitor.GetHistory(1).ShouldHaveSingleItem();
        dto.SqliteConnections.ShouldNotBeNull();
        dto.SqliteConnections.ShouldBeSameAs(stored.SqliteConnections);
        var history = controller.GetMemoryPressureHistory(1).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        history.Snapshots.ShouldHaveSingleItem().SqliteConnections.ShouldBeSameAs(stored.SqliteConnections);
        AssertWebJson(dto);
    }

    [Fact]
    public void HistoryEndpoint_DoesNotResampleConnectionTransitions()
    {
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance);
        var stored = monitor.CaptureSnapshot();
        using var connection = SqliteConnectionFactory.Create("Data Source=:memory:;Pooling=False");
        connection.Open();
        try
        {
            var current = SqliteConnectionFactory.GetConnectionObservation();
            current.OpenTransitions.ShouldBeGreaterThan(stored.SqliteConnections.ShouldNotBeNull().OpenTransitions);
            var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance, memoryMonitor: monitor);
            var response = controller.GetMemoryPressureHistory(1).ShouldBeOfType<OkObjectResult>()
                .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
            response.Snapshots.ShouldHaveSingleItem().SqliteConnections.ShouldBeSameAs(stored.SqliteConnections);
            monitor.SnapshotCount.ShouldBe(1);
        }
        finally { connection.Close(); }
    }

    private static void AssertWebJson(MemoryPressureDto dto)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var observation = json.RootElement.GetProperty("sqliteConnections");
        var expected = dto.SqliteConnections.ShouldNotBeNull();
        observation.GetProperty("currentObservedOpenConnections").GetInt64().ShouldBe(expected.CurrentObservedOpenConnections);
        observation.GetProperty("peakObservedOpenConnections").GetInt64().ShouldBe(expected.PeakObservedOpenConnections);
        observation.GetProperty("openTransitions").GetInt64().ShouldBe(expected.OpenTransitions);
        observation.GetProperty("closeTransitions").GetInt64().ShouldBe(expected.CloseTransitions);
        observation.GetProperty("poolingEnabledObservedOpenConnections").GetInt64().ShouldBe(expected.PoolingEnabledObservedOpenConnections);
        observation.GetProperty("poolingDisabledObservedOpenConnections").GetInt64().ShouldBe(expected.PoolingDisabledObservedOpenConnections);
    }
}
