using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class DefaultSubAgentManagerActivityTests
{
    [Fact]
    public async Task SpawnAsync_PublishesSpawnedEvent()
    {
        var manager = CreateManager(CreateHangingHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await WaitUntilAsync(() => activity.Activities.Any(HasLifecycleEvent("subagent_spawned")), TimeSpan.FromSeconds(2));

        activity.Activities.ShouldContain(activity =>
            activity.Type == GatewayActivityType.SubAgentSpawned &&
            activity.SessionId == "parent-session" &&
            HasLifecycleEvent("subagent_spawned")(activity));
        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task RunSubAgentAsync_OnSuccess_PublishesCompletedEvent()
    {
        var manager = CreateManager(CreateSuccessfulHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        // Wait on the lifecycle event, not on Status == Completed — the status can flip before
        // the "subagent_completed" activity is published (see the failure test for the same
        // window).
        await WaitUntilAsync(
            () => activity.Activities.Any(HasLifecycleEvent("subagent_completed")),
            TimeSpan.FromSeconds(2));

        activity.Activities.Any(HasLifecycleEvent("subagent_completed")).ShouldBeTrue();
        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Completed);
    }

    [Fact]
    public async Task RunSubAgentAsync_OnFailure_PublishesFailedEvent()
    {
        var manager = CreateManager(CreateFailingHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        // Wait on the lifecycle event itself, not on Status == Failed. The manager flips the
        // status to Failed in the catch block *before* OnCompletedAsync publishes the
        // "subagent_failed" activity, so polling on status can return while the event is still
        // in flight — an intermittent failure. Polling on the event (as the spawned/killed
        // tests do) removes that window.
        await WaitUntilAsync(
            () => activity.Activities.Any(HasLifecycleEvent("subagent_failed")),
            TimeSpan.FromSeconds(2));

        activity.Activities.Any(HasLifecycleEvent("subagent_failed")).ShouldBeTrue();
        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Failed);
    }

    [Fact]
    public async Task KillAsync_WhenSuccessful_PublishesKilledEvent()
    {
        var manager = CreateManager(CreateHangingHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("parent-session"));

        killed.ShouldBeTrue();
        await WaitUntilAsync(() => activity.Activities.Any(HasLifecycleEvent("subagent_killed")), TimeSpan.FromSeconds(2));
        activity.Activities.Any(HasLifecycleEvent("subagent_killed")).ShouldBeTrue();
    }

    [Fact]
    public async Task ActiveSubAgentCount_CountsRunningAcrossDifferentParents_ExcludesFinished()
    {
        // Platform-wide aggregate (issue #1692): the manager registry holds running sub-agents for
        // every parent session, so ActiveSubAgentCount must total Running records across DISTINCT
        // parents - unlike ListAsync, which is parent-scoped. Hanging handles keep each spawn in the
        // Running state until we explicitly kill one, so we can assert the count moves with real
        // in-flight work (a kill drops the active count by one).
        var manager = CreateManager(CreateHangingHandle(), out _, out _);

        manager.ActiveSubAgentCount.ShouldBe(0);

        var first = await manager.SpawnAsync(CreateSpawnRequestFor("parent-session"));
        await manager.SpawnAsync(CreateSpawnRequestFor("parent-session-2"));

        // Two running sub-agents under two different parent sessions -> the platform-wide count is 2,
        // even though each parent's ListAsync would only see one.
        await WaitUntilAsync(() => manager.ActiveSubAgentCount == 2, TimeSpan.FromSeconds(2));
        manager.ActiveSubAgentCount.ShouldBe(2);
        (await manager.ListAsync(SessionId.From("parent-session"))).Count.ShouldBe(1);
        (await manager.ListAsync(SessionId.From("parent-session-2"))).Count.ShouldBe(1);

        // Killing one completes that record; the platform-wide active count must exclude it.
        (await manager.KillAsync(first.SubAgentId, SessionId.From("parent-session"))).ShouldBeTrue();

        await WaitUntilAsync(() => manager.ActiveSubAgentCount == 1, TimeSpan.FromSeconds(2));
        manager.ActiveSubAgentCount.ShouldBe(1);
    }
    private static SubAgentSpawnRequest CreateSpawnRequestFor(string parentSessionId)
        => new()
        {
            ParentAgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From(parentSessionId),
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };
    private static DefaultSubAgentManager CreateManager(
        Mock<IAgentHandle> childHandle,
        out Mock<IAgentSupervisor> supervisor,
        out RecordingActivityBroadcaster activityBroadcaster)
    {
        supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<BotNexus.Domain.Primitives.AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<BotNexus.Domain.Primitives.SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("parent-agent"), BotNexus.Domain.Primitives.SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessfulHandle().Object);
        supervisor
            .Setup(s => s.StopAsync(
                It.Is<BotNexus.Domain.Primitives.AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<BotNexus.Domain.Primitives.SessionId>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(BotNexus.Domain.Primitives.AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        activityBroadcaster = new RecordingActivityBroadcaster();
        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activityBroadcaster,
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = BotNexus.Domain.Primitives.AgentId.From("parent-agent"),
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
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

    private static Mock<IAgentHandle> CreateFailingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
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

    private static Func<GatewayActivity, bool> HasLifecycleEvent(string eventName)
        => activity =>
            activity.Data is not null &&
            activity.Data.TryGetValue("event", out var value) &&
            string.Equals(value as string, eventName, StringComparison.Ordinal);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        private readonly Lock _sync = new();
        private readonly List<GatewayActivity> _activities = [];

        public IReadOnlyList<GatewayActivity> Activities
        {
            get
            {
                lock (_sync)
                {
                    return [.. _activities];
                }
            }
        }

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _activities.Add(activity);
            }

            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken);
                yield break;
            }
        }
    }
}
