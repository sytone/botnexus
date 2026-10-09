using System.Text.Json;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class LastGcCollectionDiagnosticsTests
{
    [Fact]
    public void MapLastGcCollection_CollectionIndexZero_ReturnsNullDespiteSuppliedValues()
    {
        MemoryPressureMonitor.MapLastGcCollection(0L, 2, true, true, 8000000000L, 8000000001L)
            .ShouldBeNull();
        CreateSnapshot().LastGcCollection.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    public void MapLastGcCollection_PreservesGenerationFlagsAndInt64Counts(int generation, bool compacted, bool concurrent)
    {
        var result = MemoryPressureMonitor.MapLastGcCollection(42L, generation, compacted, concurrent,
            8000000000L, 8000000001L).ShouldNotBeNull();
        result.Generation.ShouldBe(generation);
        result.Compacted.ShouldBe(compacted);
        result.Concurrent.ShouldBe(concurrent);
        result.PinnedObjectsCount.ShouldBe(8000000000L);
        result.FinalizationPendingCount.ShouldBe(8000000001L);
    }

    [Fact]
    public void MapLastGcCollection_AvailableZeroCounts_RemainAnObservation()
    {
        var result = MemoryPressureMonitor.MapLastGcCollection(1L, 0, false, false, 0L, 0L).ShouldNotBeNull();
        result.PinnedObjectsCount.ShouldBe(0L);
        result.FinalizationPendingCount.ShouldBe(0L);
    }

    [Fact]
    public void LastGcCollection_RecordHasOnlyGetterProperties_CloneDoesNotChangeOriginal()
    {
        typeof(LastGcCollectionSnapshot).GetProperties().Length.ShouldBe(5);
        typeof(LastGcCollectionSnapshot).GetProperties().ShouldAllBe(property => property.SetMethod == null);
        var original = CreateCollection();
        var copy = original with { };
        copy.ShouldBe(original);
        copy.ShouldNotBeSameAs(original);
        original.PinnedObjectsCount.ShouldBe(8000000000L);
    }

    [Fact]
    public void History_RetainsImmutableCollectionAfterLaterCaptureAndEviction()
    {
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 2,
            () => new SqliteAllocatorSnapshot(false, null, null));
        var collection = CreateCollection();
        var original = CreateSnapshot(collection);
        monitor.AddToHistory(original);
        var retained = monitor.GetHistory(1);
        monitor.CaptureSnapshot();
        monitor.CaptureSnapshot();

        monitor.SnapshotCount.ShouldBe(2);
        retained.ShouldHaveSingleItem().ShouldBeSameAs(original);
        original.LastGcCollection.ShouldBeSameAs(collection);
        original.LastGcCollection.ShouldNotBeNull().FinalizationPendingCount.ShouldBe(8000000001L);
    }

    [Fact]
    public void CaptureSnapshot_StableCollectionIndex_UsesRuntimeCollectionCharacteristics()
    {
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 2,
            () => new SqliteAllocatorSnapshot(false, null, null));
        var before = GC.GetGCMemoryInfo();
        var snapshot = monitor.CaptureSnapshot();
        var after = GC.GetGCMemoryInfo();
        // Background GC completion can report a lower index than an earlier foreground GC.
        // Compare values only when the surrounding runtime observation is stable.
        if (before.Index == after.Index)
            snapshot.GcCollectionIndex.ShouldBe(before.Index);
        if (snapshot.GcCollectionIndex == 0L)
        {
            snapshot.LastGcCollection.ShouldBeNull();
        }
        else
        {
            var collection = snapshot.LastGcCollection.ShouldNotBeNull();
            if (before.Index == after.Index)
            {
                collection.Generation.ShouldBe(before.Generation);
                collection.Compacted.ShouldBe(before.Compacted);
                collection.Concurrent.ShouldBe(before.Concurrent);
                collection.PinnedObjectsCount.ShouldBe(before.PinnedObjectsCount);
                collection.FinalizationPendingCount.ShouldBe(before.FinalizationPendingCount);
            }
        }
    }

    [Fact]
    public void CurrentEndpoint_PreservesCapturedCollectionInWebJson_HistoryDoesNotResample()
    {
        var captures = 0;
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 100,
            () => { captures++; return new SqliteAllocatorSnapshot(false, null, null); });
        var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance, memoryMonitor: monitor);
        var dto = controller.GetMemoryPressure().ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureDto>();
        var snapshot = monitor.GetHistory(1).ShouldHaveSingleItem();
        dto.LastGcCollection.ShouldBeSameAs(snapshot.LastGcCollection);
        dto.GcCollectionIndex.ShouldBe(snapshot.GcCollectionIndex);
        AssertJson(dto, snapshot.LastGcCollection);

        var history = controller.GetMemoryPressureHistory(1).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        var historicalDto = history.Snapshots.ShouldHaveSingleItem();
        historicalDto.LastGcCollection.ShouldBeSameAs(snapshot.LastGcCollection);
        historicalDto.GcCollectionIndex.ShouldBe(snapshot.GcCollectionIndex);
        AssertJson(historicalDto, snapshot.LastGcCollection);
        captures.ShouldBe(1);
        monitor.SnapshotCount.ShouldBe(1);
    }

    [Fact]
    public void HistoryEndpoint_PreservesInt64CollectionAndNullWebJsonWithoutCapture()
    {
        var captures = 0;
        var monitor = new MemoryPressureMonitor(NullLogger<MemoryPressureMonitor>.Instance, 100,
            () => { captures++; return new SqliteAllocatorSnapshot(false, null, null); });
        var collection = CreateCollection();
        monitor.AddToHistory(CreateSnapshot(collection));
        monitor.AddToHistory(CreateSnapshot());
        var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance, memoryMonitor: monitor);
        var response = controller.GetMemoryPressureHistory(2).ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<MemoryPressureHistoryResponse>();
        var populated = response.Snapshots.Single(s => s.GcCollectionIndex == 42L);
        populated.LastGcCollection.ShouldBeSameAs(collection);
        AssertJson(populated, collection);
        var empty = response.Snapshots.Single(s => s.GcCollectionIndex == 0L);
        empty.LastGcCollection.ShouldBeNull();
        AssertJson(empty, null);
        captures.ShouldBe(0);
        monitor.SnapshotCount.ShouldBe(2);
    }

    private static LastGcCollectionSnapshot CreateCollection() => new(2, true, false, 8000000000L, 8000000001L);

    private static MemoryPressureSnapshot CreateSnapshot(LastGcCollectionSnapshot? collection = null) => new()
    {
        CapturedAt = DateTimeOffset.UnixEpoch,
        WorkingSetBytes = 0L, GcCommittedBytes = 0L, TotalAvailableBytes = 0L,
        GcCollectionIndex = collection is null ? 0L : 42L,
        LastGcCollection = collection,
        Gen0Collections = 0, Gen1Collections = 0, Gen2Collections = 0,
        PressurePercent = 0, WorkingSetReadable = "0 B", GcCommittedReadable = "0 B",
        TotalAvailableReadable = "0 B", Level = MemoryPressureLevel.Normal, Guidance = "No action required."
    };

    private static void AssertJson(MemoryPressureDto dto, LastGcCollectionSnapshot? expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        root.GetProperty("gcCollectionIndex").GetInt64().ShouldBe(dto.GcCollectionIndex);
        var collection = root.GetProperty("lastGcCollection");
        if (expected is null)
        {
            collection.ValueKind.ShouldBe(JsonValueKind.Null);
            return;
        }
        collection.EnumerateObject().Count().ShouldBe(5);
        collection.GetProperty("generation").GetInt32().ShouldBe(expected.Generation);
        collection.GetProperty("compacted").GetBoolean().ShouldBe(expected.Compacted);
        collection.GetProperty("concurrent").GetBoolean().ShouldBe(expected.Concurrent);
        collection.GetProperty("pinnedObjectsCount").GetInt64().ShouldBe(expected.PinnedObjectsCount);
        collection.GetProperty("finalizationPendingCount").GetInt64().ShouldBe(expected.FinalizationPendingCount);
    }
}
