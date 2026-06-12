using BotNexus.Gateway.Contracts.Events;
using BotNexus.Gateway.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Events;

public class InMemoryWorldEventBusTests
{
    private readonly InMemoryWorldEventBus _bus;
    private readonly FakeDeliveryHandler _handler;

    public InMemoryWorldEventBusTests()
    {
        _handler = new FakeDeliveryHandler();
        _bus = new InMemoryWorldEventBus(_handler, NullLogger<InMemoryWorldEventBus>.Instance);
    }

    [Fact]
    public async Task PublishAsync_NoSubscribers_ReturnsZero()
    {
        var evt = WorldEvent.Create(WorldEventTypes.AgentRegistered);

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(0, count);
        Assert.Empty(_handler.Deliveries);
    }

    [Fact]
    public async Task PublishAsync_MatchingSubscriber_DeliversEvent()
    {
        _bus.SetSubscriptions("farnsworth", [new EventSubscription(WorldEventTypes.CronFailed)]);
        var evt = WorldEvent.Create(WorldEventTypes.CronFailed, sourceAgentId: "cron-service");

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(1, count);
        Assert.Single(_handler.Deliveries);
        Assert.Equal("farnsworth", _handler.Deliveries[0].AgentId);
        Assert.Equal(WorldEventTypes.CronFailed, _handler.Deliveries[0].Event.EventType);
    }

    [Fact]
    public async Task PublishAsync_NonMatchingSubscriber_DoesNotDeliver()
    {
        _bus.SetSubscriptions("farnsworth", [new EventSubscription(WorldEventTypes.CronFailed)]);
        var evt = WorldEvent.Create(WorldEventTypes.AgentRegistered);

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(0, count);
        Assert.Empty(_handler.Deliveries);
    }

    [Fact]
    public async Task PublishAsync_FilterMatch_DeliversEvent()
    {
        var filter = new Dictionary<string, string> { ["severity"] = "critical" };
        _bus.SetSubscriptions("farnsworth", [new EventSubscription(WorldEventTypes.AgentError, filter)]);

        var payload = new Dictionary<string, string> { ["severity"] = "critical", ["agentId"] = "broken-bot" };
        var evt = WorldEvent.Create(WorldEventTypes.AgentError, payload);

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PublishAsync_FilterMismatch_DoesNotDeliver()
    {
        var filter = new Dictionary<string, string> { ["severity"] = "critical" };
        _bus.SetSubscriptions("farnsworth", [new EventSubscription(WorldEventTypes.AgentError, filter)]);

        var payload = new Dictionary<string, string> { ["severity"] = "warning" };
        var evt = WorldEvent.Create(WorldEventTypes.AgentError, payload);

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(0, count);
        Assert.Empty(_handler.Deliveries);
    }

    [Fact]
    public async Task PublishAsync_MultipleSubscribers_DeliversToAll()
    {
        _bus.SetSubscriptions("agent-a", [new EventSubscription(WorldEventTypes.HealthDegraded)]);
        _bus.SetSubscriptions("agent-b", [new EventSubscription(WorldEventTypes.HealthDegraded)]);
        var evt = WorldEvent.Create(WorldEventTypes.HealthDegraded);

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(2, count);
        Assert.Equal(2, _handler.Deliveries.Count);
    }

    [Fact]
    public async Task PublishAsync_AgentWithMultipleMatchingSubs_DeliveredOnlyOnce()
    {
        _bus.SetSubscriptions("farnsworth", [
            new EventSubscription(WorldEventTypes.CronFailed),
            new EventSubscription(WorldEventTypes.CronFailed, new Dictionary<string, string> { ["jobId"] = "maintenance" })
        ]);
        var payload = new Dictionary<string, string> { ["jobId"] = "maintenance" };
        var evt = WorldEvent.Create(WorldEventTypes.CronFailed, payload);

        var count = await _bus.PublishAsync(evt);

        Assert.Equal(1, count); // Only once per agent
    }

    [Fact]
    public void SetSubscriptions_EmptyList_RemovesAgent()
    {
        _bus.SetSubscriptions("farnsworth", [new EventSubscription(WorldEventTypes.CronFailed)]);
        _bus.SetSubscriptions("farnsworth", []);

        var subs = _bus.GetSubscriptions("farnsworth");
        Assert.Empty(subs);
    }

    [Fact]
    public void GetSubscriptions_UnknownAgent_ReturnsEmpty()
    {
        var subs = _bus.GetSubscriptions("nonexistent");
        Assert.Empty(subs);
    }

    [Fact]
    public void GetSubscribers_ReturnsMatchingAgents()
    {
        _bus.SetSubscriptions("agent-a", [new EventSubscription(WorldEventTypes.CronFailed)]);
        _bus.SetSubscriptions("agent-b", [new EventSubscription(WorldEventTypes.AgentError)]);
        _bus.SetSubscriptions("agent-c", [new EventSubscription(WorldEventTypes.CronFailed)]);

        var subscribers = _bus.GetSubscribers(WorldEventTypes.CronFailed);

        Assert.Equal(2, subscribers.Count);
        Assert.Contains("agent-a", subscribers);
        Assert.Contains("agent-c", subscribers);
    }

    [Fact]
    public async Task PublishAsync_DeliveryFailure_ContinuesWithOtherSubscribers()
    {
        var failingHandler = new FailOnAgentDeliveryHandler("agent-a");
        var bus = new InMemoryWorldEventBus(failingHandler, NullLogger<InMemoryWorldEventBus>.Instance);

        bus.SetSubscriptions("agent-a", [new EventSubscription(WorldEventTypes.HealthDegraded)]);
        bus.SetSubscriptions("agent-b", [new EventSubscription(WorldEventTypes.HealthDegraded)]);
        var evt = WorldEvent.Create(WorldEventTypes.HealthDegraded);

        var count = await bus.PublishAsync(evt);

        // agent-a failed, agent-b succeeded
        Assert.Equal(1, count);
    }

    private sealed class FakeDeliveryHandler : IEventDeliveryHandler
    {
        public List<(string AgentId, WorldEvent Event)> Deliveries { get; } = [];

        public Task DeliverAsync(string agentId, WorldEvent worldEvent, CancellationToken cancellationToken = default)
        {
            Deliveries.Add((agentId, worldEvent));
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnAgentDeliveryHandler(string failAgentId) : IEventDeliveryHandler
    {
        public Task DeliverAsync(string agentId, WorldEvent worldEvent, CancellationToken cancellationToken = default)
        {
            if (agentId == failAgentId)
                throw new InvalidOperationException("Simulated delivery failure");
            return Task.CompletedTask;
        }
    }
}

public class EventSubscriptionTests
{
    [Fact]
    public void Matches_SameEventType_NoFilter_ReturnsTrue()
    {
        var sub = new EventSubscription(WorldEventTypes.CronFailed);
        var evt = WorldEvent.Create(WorldEventTypes.CronFailed);

        Assert.True(sub.Matches(evt));
    }

    [Fact]
    public void Matches_DifferentEventType_ReturnsFalse()
    {
        var sub = new EventSubscription(WorldEventTypes.CronFailed);
        var evt = WorldEvent.Create(WorldEventTypes.AgentRegistered);

        Assert.False(sub.Matches(evt));
    }

    [Fact]
    public void Matches_FilterAllPresent_ReturnsTrue()
    {
        var filter = new Dictionary<string, string> { ["severity"] = "critical", ["zone"] = "us-west" };
        var sub = new EventSubscription(WorldEventTypes.AgentError, filter);
        var payload = new Dictionary<string, string> { ["severity"] = "critical", ["zone"] = "us-west", ["extra"] = "ignored" };
        var evt = WorldEvent.Create(WorldEventTypes.AgentError, payload);

        Assert.True(sub.Matches(evt));
    }

    [Fact]
    public void Matches_FilterKeyMissing_ReturnsFalse()
    {
        var filter = new Dictionary<string, string> { ["severity"] = "critical" };
        var sub = new EventSubscription(WorldEventTypes.AgentError, filter);
        var payload = new Dictionary<string, string> { ["zone"] = "us-west" };
        var evt = WorldEvent.Create(WorldEventTypes.AgentError, payload);

        Assert.False(sub.Matches(evt));
    }

    [Fact]
    public void Matches_CaseInsensitiveEventType()
    {
        var sub = new EventSubscription("CRON.FAILED");
        var evt = WorldEvent.Create("cron.failed");

        Assert.True(sub.Matches(evt));
    }

    [Fact]
    public void Matches_CaseInsensitiveFilterValue()
    {
        var filter = new Dictionary<string, string> { ["severity"] = "CRITICAL" };
        var sub = new EventSubscription(WorldEventTypes.AgentError, filter);
        var payload = new Dictionary<string, string> { ["severity"] = "critical" };
        var evt = WorldEvent.Create(WorldEventTypes.AgentError, payload);

        Assert.True(sub.Matches(evt));
    }
}

public class WorldEventTests
{
    [Fact]
    public void Create_SetsTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var evt = WorldEvent.Create(WorldEventTypes.AgentRegistered);
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(evt.Timestamp, before, after);
    }

    [Fact]
    public void Create_NullPayload_DefaultsToEmptyDictionary()
    {
        var evt = WorldEvent.Create(WorldEventTypes.CronFailed);

        Assert.NotNull(evt.Payload);
        Assert.Empty(evt.Payload);
    }

    [Fact]
    public void Create_WithPayload_PreservesValues()
    {
        var payload = new Dictionary<string, string> { ["key"] = "value" };
        var evt = WorldEvent.Create(WorldEventTypes.CronFailed, payload, "farnsworth");

        Assert.Equal("value", evt.Payload["key"]);
        Assert.Equal("farnsworth", evt.SourceAgentId);
    }
}
