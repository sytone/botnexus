using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Covers the bounded-retention eviction of finished sub-agent records (#1505): a completed record
/// is retained for the configured window for status queries, then swept; running records are never
/// evicted; and the per-record timeout source is disposed on cleanup.
/// </summary>
public sealed class DefaultSubAgentManagerRetentionTests
{
    [Fact]
    public async Task CompletedRecord_IsRetainedWithinWindow_ThenEvictedAfterRetentionElapses()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var options = new SubAgentOptions { CompletedRecordRetentionMinutes = 15, MaxRetainedCompletedRecords = 0 };
        var manager = CreateManager(CreateSuccessfulHandle(), options, clock);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        // Let the background run finish and cleanup stamp the retention clock.
        await WaitUntilAsync(async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        // Within the window the record is still queryable.
        clock.Advance(TimeSpan.FromMinutes(10));
        var stillThere = await manager.GetAsync(spawned.SubAgentId);
        stillThere.ShouldNotBeNull();
        stillThere!.Status.ShouldBe(SubAgentStatus.Completed);

        // Past the window the record is reaped on the next read.
        clock.Advance(TimeSpan.FromMinutes(6)); // total 16 min > 15 min window
        var afterRetention = await manager.GetAsync(spawned.SubAgentId);
        afterRetention.ShouldBeNull();
    }

    [Fact]
    public async Task RunningRecord_IsNeverEvicted_EvenAfterRetentionWindow()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var options = new SubAgentOptions { CompletedRecordRetentionMinutes = 1, MaxRetainedCompletedRecords = 0 };
        var manager = CreateManager(CreateHangingHandle(), options, clock);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Running);

        // Advancing well past the retention window must not touch a still-running record.
        clock.Advance(TimeSpan.FromHours(1));

        var afterAdvance = await manager.GetAsync(spawned.SubAgentId);
        afterAdvance.ShouldNotBeNull();
        afterAdvance!.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task CountCap_EvictsOldestCompletedRecords_BeyondTheCap()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        // Disable the time window so only the count cap drives eviction; cap = 1.
        var options = new SubAgentOptions { CompletedRecordRetentionMinutes = 0, MaxRetainedCompletedRecords = 1 };
        var manager = CreateManager(CreateSuccessfulHandle(), options, clock);

        var first = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilAsync(async () => (await manager.GetAsync(first.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(1)); // make the second strictly newer
        var second = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilAsync(async () => (await manager.GetAsync(second.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        // The spawn of `second` already reaps; with cap=1 the older `first` is evicted, `second` kept.
        var olderAfter = await manager.GetAsync(first.SubAgentId);
        var newerAfter = await manager.GetAsync(second.SubAgentId);
        olderAfter.ShouldBeNull();
        newerAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task CountCap_WhenRetirementTimestampsTie_EvictsOldestSpawnedDeterministically()
    {
        // Regression for the flaky CountCap eviction (#1654): when two retired records share an
        // identical RetiredAt (the clock is NOT advanced between the two spawns, so both retire at
        // the same instant), the count-cap eviction must still be a *total* order -- the
        // oldest-spawned record is evicted, deterministically. Before the monotonic spawn-sequence
        // tie-break, OrderBy(RetiredAt) tied and the ConcurrentDictionary enumeration order decided
        // the victim, so this could evict the *newer* record ~half the time.
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var options = new SubAgentOptions { CompletedRecordRetentionMinutes = 0, MaxRetainedCompletedRecords = 1 };
        var manager = CreateManager(CreateSuccessfulHandle(), options, clock);

        var first = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilAsync(async () => (await manager.GetAsync(first.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        // Deliberately do NOT advance the clock -- `second` retires at the same instant as `first`,
        // forcing an exact RetiredAt tie that only the spawn-order tie-break can resolve.
        var second = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilAsync(async () => (await manager.GetAsync(second.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        // Wait until the cap-driven reap has fired with *both* records retired (exactly one of the
        // two survives under cap=1). Each GetAsync triggers a reap, so this also drives it.
        await WaitUntilAsync(
            async () =>
            {
                var firstPresent = (await manager.GetAsync(first.SubAgentId)) is not null;
                var secondPresent = (await manager.GetAsync(second.SubAgentId)) is not null;
                return !(firstPresent && secondPresent); // one has been evicted
            },
            TimeSpan.FromSeconds(2));

        // The oldest-spawned record (`first`) must be the one evicted, every run.
        var olderAfter = await manager.GetAsync(first.SubAgentId);
        var newerAfter = await manager.GetAsync(second.SubAgentId);
        olderAfter.ShouldBeNull();
        newerAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Kill_AfterCompletionCleanup_DoesNotThrow_TimeoutSourceDisposedIdempotently()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T00:00:00Z"));
        var options = new SubAgentOptions { CompletedRecordRetentionMinutes = 60, MaxRetainedCompletedRecords = 0 };
        var manager = CreateManager(CreateSuccessfulHandle(), options, clock);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());
        await WaitUntilAsync(async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        // Cleanup already disposed the timeout source; a subsequent kill must not throw
        // ObjectDisposedException (the CTS dispose paths are idempotent). Reaching the assertion
        // without an exception proves the idempotency.
        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("parent-session"));

        // A finished sub-agent cannot be killed again.
        killed.ShouldBeFalse();
    }

    private static DefaultSubAgentManager CreateManager(
        Mock<IAgentHandle> childHandle,
        SubAgentOptions subAgentOptions,
        TimeProvider timeProvider)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.GetOrCreateAsync(AgentId.From("parent-agent"), SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessfulHandle().Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        var gatewayOptions = new GatewayOptions { SubAgents = subAgentOptions };

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(gatewayOptions),
            NullLogger<DefaultSubAgentManager>.Instance,
            timeProvider: timeProvider);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static Mock<IAgentHandle> CreateSuccessfulHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "completed" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        handle.Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        handle.Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>
    /// A minimal mutable <see cref="TimeProvider"/> test double so the retention sweep can be driven
    /// deterministically without wall-clock waits (mirrors FakeTimeProvider without the extra package).
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
