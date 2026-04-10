using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
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

        activity.Activities.Should().Contain(activity =>
            activity.Type == GatewayActivityType.SubAgentSpawned &&
            activity.SessionId == "parent-session" &&
            HasLifecycleEvent("subagent_spawned")(activity));
        (await manager.GetAsync(spawned.SubAgentId))!.Status.Should().Be(SubAgentStatus.Running);
    }

    [Fact]
    public async Task RunSubAgentAsync_OnSuccess_PublishesCompletedEvent()
    {
        var manager = CreateManager(CreateSuccessfulHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await WaitUntilAsync(
            async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.Completed,
            TimeSpan.FromSeconds(2));

        activity.Activities.Any(HasLifecycleEvent("subagent_completed")).Should().BeTrue();
    }

    [Fact]
    public async Task RunSubAgentAsync_OnFailure_PublishesFailedEvent()
    {
        var manager = CreateManager(CreateFailingHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await WaitUntilAsync(
            async () => (await manager.GetAsync(spawned.SubAgentId))?.Status == SubAgentStatus.Failed,
            TimeSpan.FromSeconds(2));

        activity.Activities.Any(HasLifecycleEvent("subagent_failed")).Should().BeTrue();
    }

    [Fact]
    public async Task KillAsync_WhenSuccessful_PublishesKilledEvent()
    {
        var manager = CreateManager(CreateHangingHandle(), out _, out var activity);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, "parent-session");

        killed.Should().BeTrue();
        await WaitUntilAsync(() => activity.Activities.Any(HasLifecycleEvent("subagent_killed")), TimeSpan.FromSeconds(2));
        activity.Activities.Any(HasLifecycleEvent("subagent_killed")).Should().BeTrue();
    }

    private static DefaultSubAgentManager CreateManager(
        Mock<IAgentHandle> childHandle,
        out Mock<IAgentSupervisor> supervisor,
        out RecordingActivityBroadcaster activityBroadcaster)
    {
        supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("parent-agent", It.Is<string>(id => id.Contains("::subagent::", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.GetOrCreateAsync("parent-agent", "parent-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessfulHandle().Object);
        supervisor
            .Setup(s => s.StopAsync("parent-agent", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get("parent-agent"))
            .Returns(new AgentDescriptor
            {
                AgentId = "parent-agent",
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        activityBroadcaster = new RecordingActivityBroadcaster();
        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activityBroadcaster,
            Options.Create(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = "parent-agent",
            ParentSessionId = "parent-session",
            Task = "Do background work"
        };

    private static Mock<IAgentHandle> CreateSuccessfulHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("parent-agent");
        handle.SetupGet(h => h.SessionId).Returns("session");
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "completed" });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    private static Mock<IAgentHandle> CreateFailingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("parent-agent");
        handle.SetupGet(h => h.SessionId).Returns("session");
        handle.SetupGet(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns("parent-agent");
        handle.SetupGet(h => h.SessionId).Returns("session");
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
