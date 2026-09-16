using System.Diagnostics.Metrics;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Telemetry.Tests;

public sealed class StoreMetricsTests
{
    [Fact]
    public async Task MeasureReadAsync_RecordsDurationAndReturnedRowCountWithBoundedTags()
    {
        using var meter = new Meter("BotNexus.Test.StoreMetrics");
        var observed = new List<(string Name, double Value, IReadOnlyDictionary<string, string> Tags)>();
        using var listener = Listen(meter, observed);
        var metrics = new StoreMetrics(new BotNexusMetrics(meter));

        var rows = await metrics.MeasureReadAsync(
            "conversation",
            "list",
            static _ => Task.FromResult<IReadOnlyList<int>>([1, 2, 3]),
            static result => result.Count,
            CancellationToken.None);

        rows.Count.ShouldBe(3);
        var duration = observed.Single(item => item.Name == StoreMetrics.DurationInstrumentName);
        duration.Value.ShouldBeGreaterThanOrEqualTo(0);
        duration.Tags.ShouldBe(new Dictionary<string, string>
        {
            ["operation"] = "list",
            ["outcome"] = "success",
            ["store"] = "conversation"
        }, ignoreOrder: true);
        var rowCount = observed.Single(item => item.Name == StoreMetrics.RowsInstrumentName);
        rowCount.Value.ShouldBe(3);
        rowCount.Tags.ShouldBe(new Dictionary<string, string>
        {
            ["operation"] = "list",
            ["outcome"] = "success",
            ["store"] = "conversation"
        }, ignoreOrder: true);
    }

    [Fact]
    public async Task MeasureAsync_RecordsFailureAndPreservesTheStoreException()
    {
        using var meter = new Meter("BotNexus.Test.StoreMetrics.Failure");
        var observed = new List<(string Name, double Value, IReadOnlyDictionary<string, string> Tags)>();
        using var listener = Listen(meter, observed);
        var metrics = new StoreMetrics(new BotNexusMetrics(meter));
        var expected = new InvalidOperationException("store failed");

        var actual = await Should.ThrowAsync<InvalidOperationException>(() => metrics.MeasureAsync<int>(
            "session",
            "get",
            _ => Task.FromException<int>(expected),
            CancellationToken.None));

        actual.ShouldBeSameAs(expected);
        observed.Single(item => item.Name == StoreMetrics.DurationInstrumentName)
            .Tags["outcome"].ShouldBe("failure");
    }

    [Fact]
    public async Task MetricsFailure_DoesNotAlterSuccessfulStoreResult()
    {
        var metrics = new StoreMetrics(new ThrowingMetrics());

        var result = await metrics.MeasureReadAsync(
            "conversation",
            "list",
            static _ => Task.FromResult<IReadOnlyList<int>>([7, 8]),
            static rows => rows.Count,
            CancellationToken.None);

        result.ShouldBe(new[] { 7, 8 });
    }

    [Fact]
    public async Task SqliteConversationList_ReportsTheRowsActuallyReturned()
    {
        using var collector = new BotNexus.Gateway.Telemetry.Snapshot.MetricsSnapshotCollector();
        var metrics = new StoreMetrics(new BotNexusMetrics());
        var directory = Path.Combine(Path.GetTempPath(), nameof(StoreMetricsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteConversationStore(
                $"Data Source={Path.Combine(directory, "conversations.db")};Pooling=False",
                NullLogger<SqliteConversationStore>.Instance,
                worldContext: null,
                storeMetrics: metrics);
            await store.CreateAsync(NewConversation("first"));
            await store.CreateAsync(NewConversation("second"));

            var rows = await store.ListAsync();

            rows.Count.ShouldBe(2);
            var measurement = collector.Snapshot().Instruments
                .Single(item => item.Name == StoreMetrics.RowsInstrumentName)
                .Measurements.Single(item => item.Tags.GetValueOrDefault("store") == "conversation"
                    && item.Tags.GetValueOrDefault("operation") == "list");
            measurement.Value.ShouldBe(2);
            measurement.Tags.Keys.ShouldBe(new[] { "operation", "outcome", "store" }, ignoreOrder: true);
        }
        finally
        {
            BotNexus.Testing.SqlitePoolCleanup.ClearPoolsUnder(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteSessionList_ReportsTheRowsActuallyReturned()
    {
        using var collector = new BotNexus.Gateway.Telemetry.Snapshot.MetricsSnapshotCollector();
        var metrics = new StoreMetrics(new BotNexusMetrics());
        var directory = Path.Combine(Path.GetTempPath(), nameof(StoreMetricsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var conversations = new InMemoryConversationStore();
            var store = new SqliteSessionStore(
                $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False",
                NullLogger<SqliteSessionStore>.Instance,
                conversations,
                storeMetrics: metrics);
            await store.SaveAsync(new GatewaySession { SessionId = SessionId.From("first"), AgentId = AgentId.From("agent") });
            await store.SaveAsync(new GatewaySession { SessionId = SessionId.From("second"), AgentId = AgentId.From("agent") });

            var rows = await store.ListAsync();

            rows.Count.ShouldBe(2);
            var measurement = collector.Snapshot().Instruments
                .Single(item => item.Name == StoreMetrics.RowsInstrumentName)
                .Measurements.Single(item => item.Tags.GetValueOrDefault("store") == "session"
                    && item.Tags.GetValueOrDefault("operation") == "list");
            measurement.Value.ShouldBe(2);
            measurement.Tags.Keys.ShouldBe(new[] { "operation", "outcome", "store" }, ignoreOrder: true);
        }
        finally
        {
            BotNexus.Testing.SqlitePoolCleanup.ClearPoolsUnder(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_ContainsStoreInstrumentsWithoutAnOtlpExporter()
    {
        using var collector = new BotNexus.Gateway.Telemetry.Snapshot.MetricsSnapshotCollector();
        var metrics = new StoreMetrics(new BotNexusMetrics());
        var operation = "list-" + Guid.NewGuid().ToString("N");

        _ = await metrics.MeasureReadAsync(
            "conversation",
            operation,
            static _ => Task.FromResult<IReadOnlyList<int>>([1, 2]),
            static rows => rows.Count,
            CancellationToken.None);

        var snapshot = collector.Snapshot();
        snapshot.Instruments.Single(item => item.Name == StoreMetrics.DurationInstrumentName)
            .Measurements.ShouldContain(row => row.Tags.GetValueOrDefault("operation") == operation);
        snapshot.Instruments.Single(item => item.Name == StoreMetrics.RowsInstrumentName)
            .Measurements.Single(row => row.Tags.GetValueOrDefault("operation") == operation)
            .Value.ShouldBe(2);
    }

    private static Conversation NewConversation(string title)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("agent"),
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static MeterListener Listen(
        Meter meter,
        List<(string Name, double Value, IReadOnlyDictionary<string, string> Tags)> observed)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter == meter)
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            observed.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            observed.Add((instrument.Name, value, ToDictionary(tags)));
        });
        listener.Start();
        return listener;
    }

    private static IReadOnlyDictionary<string, string> ToDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in tags)
            result[pair.Key] = pair.Value?.ToString() ?? string.Empty;
        return result;
    }

    private sealed class ThrowingMetrics : IMetrics
    {
        public Counter<T> CreateCounter<T>(string name, string? unit = null, string? description = null) where T : struct
            => throw new InvalidOperationException("metrics unavailable");

        public Histogram<T> CreateHistogram<T>(string name, string? unit = null, string? description = null) where T : struct
            => throw new InvalidOperationException("metrics unavailable");

        public UpDownCounter<T> CreateUpDownCounter<T>(string name, string? unit = null, string? description = null) where T : struct
            => throw new InvalidOperationException("metrics unavailable");

        public ObservableGauge<T> CreateObservableGauge<T>(string name, Func<T> observeValue, string? unit = null, string? description = null) where T : struct
            => throw new InvalidOperationException("metrics unavailable");
    }
}
