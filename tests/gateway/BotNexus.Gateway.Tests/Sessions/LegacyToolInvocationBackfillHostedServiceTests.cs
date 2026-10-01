using BotNexus.Gateway.Sessions;
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
    public async Task StartAsync_NonSqliteStore_DoesNothing()
    {
        var calls = 0;
        var delays = 0;
        var service = new LegacyToolInvocationBackfillHostedService(
            new InMemorySessionStore(),
            (_, _) => { calls++; return new(0, 0, 0, false, true); },
            (_, _) => { delays++; return Task.CompletedTask; },
            NullLogger<LegacyToolInvocationBackfillHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        calls.ShouldBe(0);
        delays.ShouldBe(0);
    }

    private static LegacyToolInvocationBackfillHostedService CreateSqliteService(
        Func<SqliteSessionStore, int, LegacyToolInvocationBackfillReport> runBatch,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        var store = new SqliteSessionStore(
            "Data Source=:memory:",
            NullLogger<SqliteSessionStore>.Instance,
            new InMemoryConversationStore());
        return new(store, runBatch, delay, NullLogger<LegacyToolInvocationBackfillHostedService>.Instance);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
