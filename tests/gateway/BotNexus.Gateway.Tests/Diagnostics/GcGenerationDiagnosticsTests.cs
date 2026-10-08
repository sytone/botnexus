using System.Runtime.InteropServices;
using System.Text.Json;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class GcGenerationDiagnosticsTests
{
    [Fact]
    public void MapGenerationInfo_PreservesAllFiveSlotsAndInt64FieldsInOrder()
    {
        var source = CreateGenerationInfo(5);
        var result = MemoryPressureMonitor.MapGenerationInfo(42L, source);

        result.Count.ShouldBe(5);
        for (var slot = 0; slot < 5; slot++)
        {
            var expected = source[slot];
            result[slot].Slot.ShouldBe(slot);
            result[slot].SizeBeforeBytes.ShouldBe(expected.SizeBeforeBytes);
            result[slot].FragmentationBeforeBytes.ShouldBe(expected.FragmentationBeforeBytes);
            result[slot].SizeAfterBytes.ShouldBe(expected.SizeAfterBytes);
            result[slot].FragmentationAfterBytes.ShouldBe(expected.FragmentationAfterBytes);
            result[slot].SizeBeforeBytes.ShouldBeGreaterThan((long)int.MaxValue);
            result[slot].FragmentationBeforeBytes.ShouldBeGreaterThan((long)int.MaxValue);
            result[slot].SizeAfterBytes.ShouldBeGreaterThan((long)int.MaxValue);
            result[slot].FragmentationAfterBytes.ShouldBeGreaterThan((long)int.MaxValue);
        }
    }

    [Fact]
    public void MapGenerationInfo_CollectionIndexZero_ReturnsEmptyDespiteSuppliedValues()
    {
        MemoryPressureMonitor.MapGenerationInfo(0L, CreateGenerationInfo(5)).ShouldBeEmpty();
    }

    [Fact]
    public void MapGenerationInfo_EmptyInput_ReturnsEmpty()
    {
        MemoryPressureMonitor.MapGenerationInfo(1L, Array.Empty<GCGenerationInfo>()).ShouldBeEmpty();
    }

    [Fact]
    public void MapGenerationInfo_ExtraSlots_AreBoundedToFive()
    {
        MemoryPressureMonitor.MapGenerationInfo(1L, CreateGenerationInfo(8)).Count.ShouldBe(5);
    }

    [Fact]
    public void MapGenerationInfo_DefensiveCopy_CannotBeMutatedThroughSourceOrList()
    {
        var source = CreateGenerationInfo(5);
        var result = MemoryPressureMonitor.MapGenerationInfo(1L, source);
        var first = result[0];
        source[0] = default;
        result[0].ShouldBe(first);
        AssertReadOnly(result);
    }

    [Fact]
    public void Snapshot_DefaultAndSuppliedGenerations_AreBoundedImmutableCopies()
    {
        CreateSnapshot().GcGenerations.ShouldBeEmpty();
        var source = MemoryPressureMonitor.MapGenerationInfo(1L, CreateGenerationInfo(5)).ToList();
        source.AddRange(source);
        var snapshot = CreateSnapshot(source);
        var first = snapshot.GcGenerations[0];
        source.Clear();
        snapshot.GcGenerations.Count.ShouldBe(5);
        snapshot.GcGenerations[0].ShouldBe(first);
        AssertReadOnly(snapshot.GcGenerations);
    }

    [Fact]
    public void History_RetainsIndependentGenerationSnapshotsAfterLaterCaptureAndEviction()
    {
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 2);
        var source = MemoryPressureMonitor.MapGenerationInfo(42L, CreateGenerationInfo(5)).ToList();
        var original = CreateSnapshot(source);
        monitor.AddToHistory(original);
        var retainedHistory = monitor.GetHistory(1);
        source.Clear();
        monitor.CaptureSnapshot();
        monitor.CaptureSnapshot();

        monitor.SnapshotCount.ShouldBe(2);
        retainedHistory.ShouldHaveSingleItem().ShouldBeSameAs(original);
        original.GcGenerations.Count.ShouldBe(5);
        original.GcGenerations[4].FragmentationAfterBytes.ShouldBe(8000000043L);
        AssertReadOnly(original.GcGenerations);
    }

    [Fact]
    public void CurrentEndpoint_PreservesCapturedGenerationsInDtoAndWebJson_HistoryDoesNotResample()
    {
        var captures = 0;
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 100,
            () => { captures++; return new SqliteAllocatorSnapshot(false, null, null); });
        var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance,
            memoryMonitor: monitor);
        var dto = controller.GetMemoryPressure().ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureDto>();
        var snapshot = monitor.GetHistory(1).ShouldHaveSingleItem();
        dto.GcGenerations.ShouldBe(snapshot.GcGenerations);
        dto.GcCollectionIndex.ShouldBe(snapshot.GcCollectionIndex);
        AssertJson(dto, snapshot.GcGenerations);
        AssertReadOnly(dto.GcGenerations);

        var history = controller.GetMemoryPressureHistory(1).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        var historicalDto = history.Snapshots.ShouldHaveSingleItem();
        historicalDto.GcGenerations.ShouldBe(snapshot.GcGenerations);
        historicalDto.GcCollectionIndex.ShouldBe(snapshot.GcCollectionIndex);
        AssertJson(historicalDto, snapshot.GcGenerations);
        captures.ShouldBe(1);
        monitor.SnapshotCount.ShouldBe(1);
    }

    [Fact]
    public void HistoryEndpoint_PreservesDeterministicInt64GenerationsAndEmptyJsonWithoutCapture()
    {
        var captures = 0;
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 100,
            () => { captures++; return new SqliteAllocatorSnapshot(false, null, null); });
        var populated = CreateSnapshot(MemoryPressureMonitor.MapGenerationInfo(42L, CreateGenerationInfo(5)));
        monitor.AddToHistory(populated);
        var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance,
            memoryMonitor: monitor);
        var response = controller.GetMemoryPressureHistory(1).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        var dto = response.Snapshots.ShouldHaveSingleItem();
        dto.GcCollectionIndex.ShouldBe(42L);
        dto.GcGenerations.ShouldBe(populated.GcGenerations);
        AssertJson(dto, populated.GcGenerations);
        AssertReadOnly(dto.GcGenerations);

        monitor.AddToHistory(CreateSnapshot());
        response = controller.GetMemoryPressureHistory(2).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        var empty = response.Snapshots.Single(s => s.GcCollectionIndex == 0L);
        empty.GcGenerations.ShouldBeEmpty();
        AssertJson(empty, Array.Empty<GcGenerationSnapshot>());
        captures.ShouldBe(0);
        monitor.SnapshotCount.ShouldBe(2);
    }

    private static GCGenerationInfo[] CreateGenerationInfo(int count)
    {
        // .NET 10's sequential GCGenerationInfo consists of these four Int64 fields.
        // Construct only test input; production maps the public getters without reflection.
        var fields = Enumerable.Range(0, count).SelectMany(slot => new[]
        {
            8000000000L + slot * 10, 8000000001L + slot * 10,
            8000000002L + slot * 10, 8000000003L + slot * 10
        }).ToArray();
        var result = MemoryMarshal.Cast<long, GCGenerationInfo>(fields.AsSpan()).ToArray();
        result.Length.ShouldBe(count);
        result[0].SizeBeforeBytes.ShouldBe(8000000000L);
        result[0].FragmentationBeforeBytes.ShouldBe(8000000001L);
        result[0].SizeAfterBytes.ShouldBe(8000000002L);
        result[0].FragmentationAfterBytes.ShouldBe(8000000003L);
        return result;
    }

    private static MemoryPressureSnapshot CreateSnapshot(IReadOnlyList<GcGenerationSnapshot>? generations = null) => new()
    {
        CapturedAt = DateTimeOffset.UnixEpoch,
        WorkingSetBytes = 0L, GcCommittedBytes = 0L, TotalAvailableBytes = 0L,
        GcCollectionIndex = generations is null ? 0L : 42L,
        GcGenerations = generations ?? Array.Empty<GcGenerationSnapshot>(),
        Gen0Collections = 0, Gen1Collections = 0, Gen2Collections = 0,
        PressurePercent = 0, WorkingSetReadable = "0 B", GcCommittedReadable = "0 B",
        TotalAvailableReadable = "0 B", Level = MemoryPressureLevel.Normal, Guidance = "No action required."
    };

    private static void AssertReadOnly(IReadOnlyList<GcGenerationSnapshot> generations)
    {
        generations.ShouldNotBeOfType<GcGenerationSnapshot[]>();
        if (generations is IList<GcGenerationSnapshot> list)
        {
            list.IsReadOnly.ShouldBeTrue();
            Should.Throw<NotSupportedException>(() => list.Add(default));
            if (list.Count > 0)
                Should.Throw<NotSupportedException>(() => list[0] = default);
        }
    }

    private static void AssertJson(MemoryPressureDto dto, IReadOnlyList<GcGenerationSnapshot> expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        root.GetProperty("gcCollectionIndex").GetInt64().ShouldBe(dto.GcCollectionIndex);
        root.GetProperty("workingSetBytes").GetInt64().ShouldBe(dto.WorkingSetBytes);
        root.GetProperty("gcCommittedBytes").GetInt64().ShouldBe(dto.GcCommittedBytes);
        root.GetProperty("level").GetString().ShouldBe(dto.Level);
        var array = root.GetProperty("gcGenerations");
        array.ValueKind.ShouldBe(JsonValueKind.Array);
        array.GetArrayLength().ShouldBe(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            array[i].GetProperty("slot").GetInt32().ShouldBe(expected[i].Slot);
            array[i].GetProperty("sizeBeforeBytes").GetInt64().ShouldBe(expected[i].SizeBeforeBytes);
            array[i].GetProperty("fragmentationBeforeBytes").GetInt64().ShouldBe(expected[i].FragmentationBeforeBytes);
            array[i].GetProperty("sizeAfterBytes").GetInt64().ShouldBe(expected[i].SizeAfterBytes);
            array[i].GetProperty("fragmentationAfterBytes").GetInt64().ShouldBe(expected[i].FragmentationAfterBytes);
        }
    }
}
