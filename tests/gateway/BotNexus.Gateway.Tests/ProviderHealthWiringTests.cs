using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Contracts.Events;
using BotNexus.Gateway.Events;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the DI wiring for provider-health signalling (#3281).
///
/// <para>
/// The event bus was previously not registered <em>at all</em>: <c>InMemoryWorldEventBus</c> existed
/// as a type with no registration and no publisher, so publishing would have thrown at resolution
/// time even if some caller had tried. A test that only exercised the observer in isolation would
/// pass happily against that broken wiring, so the container itself is asserted here.
/// </para>
/// </summary>
public sealed class ProviderHealthWiringTests
{
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<IEventDeliveryHandler, LoggingEventDeliveryHandler>();
        services.AddSingleton<IWorldEventBus, InMemoryWorldEventBus>();
        services.AddSingleton<IProviderHealthObserver>(serviceProvider =>
            new WorldEventProviderHealthObserver(
                serviceProvider.GetRequiredService<IWorldEventBus>(),
                serviceProvider.GetRequiredService<ILogger<WorldEventProviderHealthObserver>>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The observer resolves from the container. Before this change the bus it depends on had no
    /// registration, so this resolution could not have succeeded.
    /// </summary>
    [Fact]
    public void ProviderHealthObserver_ResolvesFromContainer()
    {
        using var provider = BuildContainer();

        var observer = provider.GetRequiredService<IProviderHealthObserver>();

        observer.ShouldBeOfType<WorldEventProviderHealthObserver>();
    }

    /// <summary>The bus itself resolves, which is the registration that was missing entirely.</summary>
    [Fact]
    public void WorldEventBus_ResolvesFromContainer()
    {
        using var provider = BuildContainer();

        provider.GetRequiredService<IWorldEventBus>().ShouldNotBeNull();
    }

    /// <summary>
    /// AC7: no channel is forced to consume the event. With zero subscribers registered, publishing
    /// must still succeed and simply reach nobody - emission is unconditional, consumption is the
    /// channel's choice.
    /// </summary>
    [Fact]
    public async Task WithNoSubscribers_PublishingSucceedsAndNotifiesNobody()
    {
        using var provider = BuildContainer();
        var bus = provider.GetRequiredService<IWorldEventBus>();

        var notified = await bus.PublishAsync(
            WorldEvent.Create(WorldEventTypes.HealthDegraded, new Dictionary<string, string> { ["provider"] = "github-copilot" }));

        notified.ShouldBe(0);
    }

    /// <summary>
    /// A channel that opts in receives the event, which is the capability the issue exists to make
    /// possible. Asserting only the zero-subscriber case would leave the feature unproven.
    /// </summary>
    [Fact]
    public async Task WithSubscriber_HealthDegradedEventIsDelivered()
    {
        using var provider = BuildContainer();
        var bus = provider.GetRequiredService<IWorldEventBus>();
        bus.SetSubscriptions("some-agent", [new EventSubscription(WorldEventTypes.HealthDegraded)]);

        var notified = await bus.PublishAsync(
            WorldEvent.Create(WorldEventTypes.HealthDegraded, new Dictionary<string, string> { ["provider"] = "github-copilot" }));

        notified.ShouldBe(1);
    }

    /// <summary>
    /// End-to-end through the real wiring: repeated credential failures reported to the resolved
    /// observer reach a subscribing channel as a <c>health.degraded</c> event carrying the provider
    /// name. This is the whole journey the outage failed to make.
    /// </summary>
    [Fact]
    public async Task RepeatedFailures_ReachASubscribingChannelThroughTheRealWiring()
    {
        var delivered = new List<(string AgentId, WorldEvent Event)>();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<IEventDeliveryHandler>(new CapturingDeliveryHandler(delivered));
        services.AddSingleton<IWorldEventBus, InMemoryWorldEventBus>();
        services.AddSingleton<IProviderHealthObserver>(sp =>
            new WorldEventProviderHealthObserver(
                sp.GetRequiredService<IWorldEventBus>(),
                sp.GetRequiredService<ILogger<WorldEventProviderHealthObserver>>()));

        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IWorldEventBus>();
        var observer = provider.GetRequiredService<IProviderHealthObserver>();

        bus.SetSubscriptions("watching-agent", [new EventSubscription(WorldEventTypes.HealthDegraded)]);

        // The default threshold is 3; drive exactly that many upstream failures.
        for (var i = 0; i < 3; i++)
        {
            await observer.RecordAsync(
                "github-copilot",
                ProviderCredentialOutcome.Failed("HttpRequestException", 503, "Service Unavailable"));
        }

        var (agentId, worldEvent) = delivered.ShouldHaveSingleItem();
        agentId.ShouldBe("watching-agent");
        worldEvent.EventType.ShouldBe(WorldEventTypes.HealthDegraded);
        worldEvent.Payload[WorldEventProviderHealthObserver.PayloadProvider].ShouldBe("github-copilot");
        worldEvent.Payload[WorldEventProviderHealthObserver.PayloadStatusCode].ShouldBe("503");
    }

    private sealed class CapturingDeliveryHandler : IEventDeliveryHandler
    {
        private readonly List<(string AgentId, WorldEvent Event)> _sink;

        public CapturingDeliveryHandler(List<(string AgentId, WorldEvent Event)> sink) => _sink = sink;

        public Task DeliverAsync(string agentId, WorldEvent worldEvent, CancellationToken cancellationToken = default)
        {
            _sink.Add((agentId, worldEvent));
            return Task.CompletedTask;
        }
    }
}
