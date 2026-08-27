using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Behavioural tests for durable <c>/sync</c> cursor persistence (#3595): the token survives a
/// gateway restart, is written only after a batch is fully processed, and a store fault degrades to
/// in-memory continuity rather than stopping the sync loop.
/// </summary>
/// <remarks>
/// Each test is named for the acceptance clause it holds, so a regression that removes the
/// load-on-start or the write-after-process fails a test that says so by name rather than producing
/// an anonymous red.
/// </remarks>
public sealed class MatrixSyncCursorPersistenceTests
{
    private const string AgentUser = "@farnsworth:example.com";
    private const string HumanUser = "@jon:example.com";
    private const string Room = "!room1:example.com";
    private const string Account = "farnsworth";
    private const string AgentId = "farnsworth";

    private static MatrixChannelOptions BuildOptions()
    {
        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents[Account] = new MatrixAccountConfig
        {
            UserId = AgentUser,
            AccessToken = "syt_fake_token",
            AgentId = AgentId,
        };
        return options;
    }

    private static MatrixChannelAdapter CreateAdapter(
        FakeMatrixClientFactory factory,
        IMatrixSyncCursorStore cursorStore) =>
        new(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            factory,
            configuration: null,
            cursorStore: cursorStore);

    private static MatrixSyncResponse BatchWithMessage(string nextBatch) => new()
    {
        NextBatch = nextBatch,
        Rooms = new MatrixSyncRooms
        {
            Join = new Dictionary<string, MatrixJoinedRoom>
            {
                [Room] = new()
                {
                    Timeline = new MatrixTimeline
                    {
                        Events =
                        [
                            new MatrixEvent
                            {
                                Type = "m.room.message",
                                Sender = HumanUser,
                                EventId = "$evt1",
                                OriginServerTs = 1_700_000_000_000,
                                Content = new MatrixMessageContent
                                {
                                    MsgType = "m.text",
                                    Body = "hello",
                                },
                            },
                        ],
                    },
                },
            },
        },
    };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    /// <summary>
    /// Clause 2: an account with a stored token resumes <c>/sync</c> from it. Removing the
    /// load-on-start makes this test fail by name.
    /// </summary>
    [Fact]
    public async Task Clause2_Start_ResumesSyncFromThePersistedSinceToken()
    {
        var store = new FakeMatrixSyncCursorStore();
        store.Seed(AgentId, Account, "s_before_restart");

        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        var adapter = CreateAdapter(factory, store);

        await adapter.StartAsync(new NoOpDispatcher());
        await WaitForAsync(() => client.SinceTokens.Count > 0);
        await adapter.StopAsync();

        client.SinceTokens.ShouldNotBeEmpty();

        // The very FIRST wire call must already carry the stored token. Asserting a later call would
        // pass even if the adapter performed a fresh initial sync first, which is the exact defect.
        client.SinceTokens[0].ShouldBe("s_before_restart");
    }

    /// <summary>
    /// Clause 2: an account with no stored token performs an initial sync exactly as before #3595.
    /// </summary>
    [Fact]
    public async Task Clause2_Start_WithNoStoredToken_PerformsAnInitialSync()
    {
        var store = new FakeMatrixSyncCursorStore();
        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        var adapter = CreateAdapter(factory, store);

        await adapter.StartAsync(new NoOpDispatcher());
        await WaitForAsync(() => client.SinceTokens.Count > 0);
        await adapter.StopAsync();

        client.SinceTokens.ShouldNotBeEmpty();
        client.SinceTokens[0].ShouldBeNull();
    }

    /// <summary>
    /// Clause 1 and 3: the token is persisted, keyed by agent id and account name, after the batch
    /// that produced it has been fully processed. Removing the write-after-process makes this test
    /// fail by name.
    /// </summary>
    [Fact]
    public async Task Clause1_Start_PersistsTheSinceTokenAfterProcessingABatch()
    {
        var store = new FakeMatrixSyncCursorStore();
        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        client.EnqueueSync(BatchWithMessage("s_next"));

        var adapter = CreateAdapter(factory, store);

        await adapter.StartAsync(new NoOpDispatcher());
        await WaitForAsync(() => store.WriteSnapshot().Count > 0);
        await adapter.StopAsync();

        var writes = store.WriteSnapshot();
        writes.ShouldNotBeEmpty();
        writes[0].Key.ShouldBe($"{AgentId}/{Account}");
        writes[0].Token.ShouldBe("s_next");

        // And the persisted value is what a restart would read back.
        (await store.GetAsync(AgentId, Account)).ShouldBe("s_next");
    }

    /// <summary>
    /// Clause 3: a batch that fails mid-processing must not advance the durable cursor, so the
    /// restart replays the batch rather than skipping the events it contained.
    /// </summary>
    [Fact]
    public async Task Clause3_MidBatchFailure_DoesNotPersistTheSinceToken()
    {
        var store = new FakeMatrixSyncCursorStore();
        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        client.EnqueueSync(BatchWithMessage("s_should_not_persist"));

        var adapter = CreateAdapter(factory, store);

        // The dispatcher throwing is a mid-batch failure: the batch's event never completes
        // processing, so the cursor that would skip it must never reach the store.
        await adapter.StartAsync(new ThrowingDispatcher());
        await WaitForAsync(() => client.SinceTokens.Count > 0);

        // Give the loop ample room to (wrongly) write the cursor before asserting its absence.
        await Task.Delay(300);
        var writes = store.WriteSnapshot();

        await adapter.StopAsync();

        writes.ShouldBeEmpty();
        (await store.GetAsync(AgentId, Account)).ShouldBeNull();
    }

    /// <summary>
    /// Clause 4: a store write failure degrades to in-memory continuity and must not stop the sync
    /// loop.
    /// </summary>
    [Fact]
    public async Task Clause4_StoreWriteFailure_LeavesTheSyncLoopRunningWithInMemoryContinuity()
    {
        var store = new FakeMatrixSyncCursorStore
        {
            WriteFailure = new InvalidOperationException("cursor store is unavailable"),
        };

        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        client.EnqueueSync(new MatrixSyncResponse { NextBatch = "s_a" });
        client.EnqueueSync(new MatrixSyncResponse { NextBatch = "s_b" });

        var adapter = CreateAdapter(factory, store);

        await adapter.StartAsync(new NoOpDispatcher());
        await WaitForAsync(() => client.SinceTokens.Count >= 3);
        await adapter.StopAsync();

        // The loop kept polling despite every write throwing, and the in-memory token still advanced
        // across batches - that is what "degrades to in-memory continuity" means.
        client.SinceTokens.Count.ShouldBeGreaterThanOrEqualTo(3);
        client.SinceTokens[0].ShouldBeNull();
        client.SinceTokens[1].ShouldBe("s_a");
        client.SinceTokens[2].ShouldBe("s_b");
    }

    /// <summary>
    /// Clause 4: a store READ failure on start must not stop the loop either; it falls back to the
    /// pre-#3595 initial sync.
    /// </summary>
    [Fact]
    public async Task Clause4_StoreReadFailure_FallsBackToAnInitialSyncWithoutStoppingTheLoop()
    {
        var store = new FakeMatrixSyncCursorStore
        {
            ReadFailure = new InvalidOperationException("cursor store is unavailable"),
        };

        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        client.EnqueueSync(new MatrixSyncResponse { NextBatch = "s_a" });

        var adapter = CreateAdapter(factory, store);

        await adapter.StartAsync(new NoOpDispatcher());
        await WaitForAsync(() => client.SinceTokens.Count >= 2);
        await adapter.StopAsync();

        client.SinceTokens.Count.ShouldBeGreaterThanOrEqualTo(2);
        client.SinceTokens[0].ShouldBeNull();
        client.SinceTokens[1].ShouldBe("s_a");
    }

    /// <summary>
    /// A host that registers no cursor store keeps the pre-#3595 behaviour rather than failing to
    /// start, which is what makes the dependency genuinely optional.
    /// </summary>
    [Fact]
    public async Task NoCursorStoreRegistered_SyncLoopStillRuns()
    {
        var factory = new FakeMatrixClientFactory();
        var client = factory.ClientFor(Account);
        client.EnqueueSync(new MatrixSyncResponse { NextBatch = "s_a" });

        var adapter = new MatrixChannelAdapter(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(BuildOptions()),
            factory);

        await adapter.StartAsync(new NoOpDispatcher());
        await WaitForAsync(() => client.SinceTokens.Count >= 2);
        await adapter.StopAsync();

        client.SinceTokens.Count.ShouldBeGreaterThanOrEqualTo(2);
        client.SinceTokens[1].ShouldBe("s_a");
    }

    private sealed class NoOpDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingDispatcher : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("dispatch failed mid-batch");
    }
}
