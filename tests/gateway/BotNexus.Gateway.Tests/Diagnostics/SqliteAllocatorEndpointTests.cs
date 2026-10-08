using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class SqliteAllocatorEndpointTests
{
    [Theory]
    [InlineData(true, 0L, 0L)]
    [InlineData(true, 4294967297L, 8589934593L)]
    [InlineData(false, null, null)]
    public void Endpoints_CurrentAndHistory_PreserveAllocatorSnapshot(
        bool available, long? current, long? peak)
    {
        var captures = 0;
        var monitor = new MemoryPressureMonitor(
            NullLogger<MemoryPressureMonitor>.Instance, 100,
            () => { captures++; return new SqliteAllocatorSnapshot(available, current, peak); });
        var controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance, logBuffer: null, memoryMonitor: monitor);

        var dto = controller.GetMemoryPressure().ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureDto>();
        var snapshot = monitor.GetHistory(1).ShouldHaveSingleItem();
        snapshot.SqliteAllocatorAvailable.ShouldBe(available);
        snapshot.SqliteAllocatorCurrentBytes.ShouldBe(current);
        snapshot.SqliteAllocatorPeakBytes.ShouldBe(peak);
        AssertCounters(dto, available, current, peak);

        var history = controller.GetMemoryPressureHistory(1).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        AssertCounters(history.Snapshots.ShouldHaveSingleItem(), available, current, peak);
        captures.ShouldBe(1); // History must preserve stored counters, not resample SQLite.

        // JSON numeric fields must also retain the original Int64 rather than narrow to Int32.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        if (available)
        {
            json.RootElement.GetProperty(nameof(dto.SqliteAllocatorCurrentBytes)).GetInt64()
                .ShouldBe(current.GetValueOrDefault());
            json.RootElement.GetProperty(nameof(dto.SqliteAllocatorPeakBytes)).GetInt64()
                .ShouldBe(peak.GetValueOrDefault());
        }
        else
        {
            json.RootElement.GetProperty(nameof(dto.SqliteAllocatorCurrentBytes)).ValueKind
                .ShouldBe(JsonValueKind.Null);
            json.RootElement.GetProperty(nameof(dto.SqliteAllocatorPeakBytes)).ValueKind
                .ShouldBe(JsonValueKind.Null);
        }
    }

    private static void AssertCounters(MemoryPressureDto dto, bool available, long? current, long? peak)
    {
        dto.SqliteAllocatorAvailable.ShouldBe(available);
        dto.SqliteAllocatorCurrentBytes.ShouldBe(current);
        dto.SqliteAllocatorPeakBytes.ShouldBe(peak);
    }
}
