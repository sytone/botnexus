using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Activity;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace BotNexus.Gateway.Tests;

public sealed class DefaultAgentRegistryTests
{
    [Fact]
    public void Register_WithValidDescriptor_AddsAgent()
    {
        var registry = CreateRegistry();
        var descriptor = CreateDescriptor("agent-a");

        registry.Register(descriptor);

        registry.Get(AgentId.From("agent-a")).ShouldBe(descriptor);
    }

    [Fact]
    public void Unregister_WithKnownAgent_RemovesAgent()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("agent-a"));

        registry.Unregister(AgentId.From("agent-a"));

        registry.Contains(AgentId.From("agent-a")).ShouldBeFalse();
    }

    [Fact]
    public void Register_WithDuplicateAgentId_ThrowsInvalidOperationException()
    {
        var registry = CreateRegistry();
        var descriptor = CreateDescriptor("agent-a");
        registry.Register(descriptor);

        Action act = () => registry.Register(CreateDescriptor("agent-a"));

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void Get_WithUnknownAgentId_ReturnsNull()
    {
        var registry = CreateRegistry();

        var agent = registry.Get(AgentId.From("unknown"));

        agent.ShouldBeNull();
    }

    [Fact]
    public void GetAll_WithMultipleAgents_ReturnsAllRegisteredAgents()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("agent-a"));
        registry.Register(CreateDescriptor("agent-b"));

        var agents = registry.GetAll();

        agents.Count().ShouldBe(2);
    }

    [Fact]
    public void Contains_WithRegisteredAndUnknownIds_ReportsCorrectMembership()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("agent-a"));

        var contains = registry.Contains(AgentId.From("agent-a")) && !registry.Contains(AgentId.From("unknown"));

        contains.ShouldBeTrue();
    }

    [Fact]
    public async Task Register_AndRead_FromConcurrentCalls_RemainsConsistent()
    {
        var registry = CreateRegistry();
        const int agentCount = 100;

        var tasks = Enumerable.Range(0, agentCount)
            .Select(i => Task.Run(() =>
            {
                var agentId = $"agent-{i}";
                registry.Register(CreateDescriptor(agentId));
                _ = registry.Get(AgentId.From(agentId));
                _ = registry.Contains(AgentId.From(agentId));
            }));

        await Task.WhenAll(tasks);

        registry.GetAll().Count().ShouldBe(agentCount);
    }

    [Fact]
    public void Register_PublishesAgentRegisteredActivity()
    {
        var broadcaster = new RecordingBroadcaster();
        var registry = CreateRegistry(broadcaster);

        registry.Register(CreateDescriptor("agent-a"));

        broadcaster.Activities.Where(activity =>
            activity.Type == GatewayActivityType.AgentRegistered &&
            activity.AgentId == "agent-a").ShouldHaveSingleItem();
    }

    [Fact]
    public void Unregister_PublishesAgentUnregisteredActivity()
    {
        var broadcaster = new RecordingBroadcaster();
        var registry = CreateRegistry(broadcaster);
        registry.Register(CreateDescriptor("agent-a"));
        broadcaster.Activities.Clear();

        registry.Unregister(AgentId.From("agent-a"));

        broadcaster.Activities.Where(activity =>
            activity.Type == GatewayActivityType.AgentUnregistered &&
            activity.AgentId == "agent-a").ShouldHaveSingleItem();
    }

    [Fact]
    public void Update_PublishesAgentConfigChangedActivity()
    {
        var broadcaster = new RecordingBroadcaster();
        var registry = CreateRegistry(broadcaster);
        registry.Register(CreateDescriptor("agent-a"));
        broadcaster.Activities.Clear();

        var updated = registry.Update(AgentId.From("agent-a"), CreateDescriptor("agent-a") with { DisplayName = "updated" });

        updated.ShouldBeTrue();
        broadcaster.Activities.Where(activity =>
            activity.Type == GatewayActivityType.AgentConfigChanged &&
            activity.AgentId == "agent-a").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Register_PublishesAgentRegisteredActivityThroughSubscriptionStream()
    {
        var broadcaster = new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance);
        var registry = CreateRegistry(broadcaster);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var readTask = subscription.MoveNextAsync().AsTask();
        await Task.Delay(20, cts.Token);

        registry.Register(CreateDescriptor("agent-a"));

        (await readTask).ShouldBeTrue();
        subscription.Current.Type.ShouldBe(GatewayActivityType.AgentRegistered);
        subscription.Current.AgentId.ShouldBe("agent-a");
    }

    [Fact]
    public async Task Unregister_PublishesAgentUnregisteredActivityThroughSubscriptionStream()
    {
        var broadcaster = new InMemoryActivityBroadcaster(NullLogger<InMemoryActivityBroadcaster>.Instance);
        var registry = CreateRegistry(broadcaster);
        registry.Register(CreateDescriptor("agent-a"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var readTask = subscription.MoveNextAsync().AsTask();
        await Task.Delay(20, cts.Token);

        registry.Unregister(AgentId.From("agent-a"));

        (await readTask).ShouldBeTrue();
        subscription.Current.Type.ShouldBe(GatewayActivityType.AgentUnregistered);
        subscription.Current.AgentId.ShouldBe("agent-a");
    }

    private static DefaultAgentRegistry CreateRegistry()
        => new(NullLogger<DefaultAgentRegistry>.Instance);

    private static DefaultAgentRegistry CreateRegistry(IActivityBroadcaster broadcaster)
        => new(NullLogger<DefaultAgentRegistry>.Instance, broadcaster);

    private static AgentDescriptor CreateDescriptor(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = $"{agentId}-display",
            ModelId = "test-model",
            ApiProvider = "test-provider"
        };

    private sealed class RecordingBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

public sealed class DefaultAgentRegistryOrderTests
{
    [Fact]
    public void GetAll_WithNoOrderSet_ReturnsSortedAlphabetically()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("b-agent", "Bravo"));
        registry.Register(CreateDescriptor("a-agent", "Alpha"));
        registry.Register(CreateDescriptor("c-agent", "Charlie"));

        var result = registry.GetAll();

        result.Select(a => a.DisplayName).ShouldBe(new[] { "Alpha", "Bravo", "Charlie" });
    }

    [Fact]
    public void GetAll_WithOrderSet_PutsOrderedAgentsFirst()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("unordered-agent", "Zulu", order: null));
        registry.Register(CreateDescriptor("second-agent", "Beta", order: 2));
        registry.Register(CreateDescriptor("first-agent", "Alpha", order: 1));

        var result = registry.GetAll();

        result[0].DisplayName.ShouldBe("Alpha"); // order 1
        result[1].DisplayName.ShouldBe("Beta");  // order 2
        result[2].DisplayName.ShouldBe("Zulu");  // no order -> alphabetical after
    }

    [Fact]
    public void GetAll_WithSameOrder_SortsAlphabeticallyAsSecondary()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("b-agent", "Bravo", order: 1));
        registry.Register(CreateDescriptor("a-agent", "Alpha", order: 1));

        var result = registry.GetAll();

        result[0].DisplayName.ShouldBe("Alpha");
        result[1].DisplayName.ShouldBe("Bravo");
    }

    [Fact]
    public void GetAll_WithNegativeOrder_SortsBeforePositiveOrder()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("positive-agent", "Positive", order: 5));
        registry.Register(CreateDescriptor("negative-agent", "Negative", order: -1));

        var result = registry.GetAll();

        result[0].DisplayName.ShouldBe("Negative"); // order -1
        result[1].DisplayName.ShouldBe("Positive"); // order 5
    }

    [Fact]
    public void GetAll_WithAllUnordered_ReturnsCaseInsensitiveAlphabetical()
    {
        var registry = CreateRegistry();
        registry.Register(CreateDescriptor("z-agent", "zebra"));
        registry.Register(CreateDescriptor("a-agent", "Alpha"));
        registry.Register(CreateDescriptor("m-agent", "mango"));

        var result = registry.GetAll();

        result.Select(a => a.DisplayName).ShouldBe(
            new[] { "Alpha", "mango", "zebra" },
            ignoreOrder: false);
    }

    private static DefaultAgentRegistry CreateRegistry()
        => new(Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultAgentRegistry>.Instance);

    private static AgentDescriptor CreateDescriptor(string agentId, string displayName, int? order = null)
        => new()
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From(agentId),
            DisplayName = displayName,
            ModelId = "test-model",
            ApiProvider = "test-provider",
            Order = order
        };
}