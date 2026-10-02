using System.Diagnostics.Metrics;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class LegacyToolInvocationBackfillHostedServiceTests
{
    [Fact]
    public async Task StartAsync_InitialDelayIsPending_DoesNotBlockStartup()
    {
        var delayReached = NewSignal();
        var releaseDelay = NewSignal();
        var calls = 0;
        var service = CreateSqliteService(
            (_, _) => { calls++; return new(0, 0, 0, false, true); },
            (_, _) => { delayReached.TrySetResult(); return releaseDelay.Task; });

        await service.StartAsync(CancellationToken.None);
        await delayReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.ShouldBe(0);
        releaseDelay.TrySetResult();

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_HasMore_RunsBoundedBatchesWithDelayBetweenThem()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var completed = NewSignal();
        var service = CreateSqliteService(
            (_, batchSize) =>
            {
                batchSize.ShouldBe(LegacyToolInvocationBackfillHostedService.BatchSize);
                calls++;
                if (calls == 3) completed.TrySetResult();
                return new(1, 1, 1, calls < 3, true);
            },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        await service.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        calls.ShouldBe(3);
        delays.ShouldBe([
            LegacyToolInvocationBackfillHostedService.InitialDelay,
            LegacyToolInvocationBackfillHostedService.BatchDelay,
            LegacyToolInvocationBackfillHostedService.BatchDelay]);
    }

    [Fact]
    public async Task StopAsync_DuringInitialDelay_CancelsWithoutRunningBatch()
    {
        var delayReached = NewSignal();
        var calls = 0;
        var service = CreateSqliteService(
            (_, _) => { calls++; return new(0, 0, 0, false, true); },
            async (_, cancellationToken) =>
            {
                delayReached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await service.StartAsync(CancellationToken.None);
        await delayReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        calls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_BatchFails_RetriesAfterRetryDelay()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var completed = NewSignal();
        var service = CreateSqliteService(
            (_, _) =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("transient");
                completed.TrySetResult();
                return new(0, 0, 0, false, true);
            },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        await service.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        calls.ShouldBe(2);
        delays.ShouldBe([
            LegacyToolInvocationBackfillHostedService.InitialDelay,
            LegacyToolInvocationBackfillHostedService.RetryDelay]);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsPayloadFreeProgressAndFailures()
    {
        using var meter = new Meter("legacy-backfill-test");
        var measurements = new List<(string Name, long Value, string? Outcome, bool? HasMore)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter == meter)
                    activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? outcome = null;
            bool? hasMore = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString();
                if (tag.Key == "has_more" && tag.Value is bool current) hasMore = current;
            }
            measurements.Add((instrument.Name, value, outcome, hasMore));
        });
        listener.Start();

        var calls = 0;
        var completed = NewSignal();
        var service = CreateSqliteService(
            (_, _) =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("payload must not escape");
                completed.TrySetResult();
                return new(7, 5, 3, false, true);
            },
            (_, _) => Task.CompletedTask,
            new BotNexusMetrics(meter));

        await service.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        measurements.ShouldContain((LegacyToolInvocationBackfillMetrics.BatchesInstrumentName, 1, "failed", null));
        measurements.ShouldContain((LegacyToolInvocationBackfillMetrics.BatchesInstrumentName, 1, "committed", false));
        measurements.ShouldContain((LegacyToolInvocationBackfillMetrics.ScannedRowsInstrumentName, 7, null, null));
        measurements.ShouldContain((LegacyToolInvocationBackfillMetrics.LinkedRowsInstrumentName, 5, null, null));
        measurements.ShouldContain((LegacyToolInvocationBackfillMetrics.InvocationsInstrumentName, 3, null, null));
        measurements.SelectMany(item => new[] { item.Name, item.Outcome }).ShouldNotContain("payload must not escape");
    }

    [Fact]
    public async Task StartAsync_NonSqliteStore_DoesNothing()
    {
        var calls = 0;
        var delays = 0;
        var service = new LegacyToolInvocationBackfillHostedService(
            new InMemorySessionStore(),
            (_, _) => { calls++; return new(0, 0, 0, false, true); },
            (_, _) => { delays++; return Task.CompletedTask; },
            new BotNexusMetrics(),
            NullLogger<LegacyToolInvocationBackfillHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        calls.ShouldBe(0);
        delays.ShouldBe(0);
    }

    private static LegacyToolInvocationBackfillHostedService CreateSqliteService(
        Func<SqliteSessionStore, int, LegacyToolInvocationBackfillReport> runBatch,
        Func<TimeSpan, CancellationToken, Task> delay,
        IMetrics? metrics = null)
    {
        var store = new SqliteSessionStore(
            "Data Source=:memory:",
            NullLogger<SqliteSessionStore>.Instance,
            new InMemoryConversationStore());
        return new(
            store,
            runBatch,
            delay,
            metrics ?? new BotNexusMetrics(),
            NullLogger<LegacyToolInvocationBackfillHostedService>.Instance);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
