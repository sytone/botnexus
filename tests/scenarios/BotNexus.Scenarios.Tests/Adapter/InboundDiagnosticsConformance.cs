using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Dispatching;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Adapter;

public sealed class InboundDiagnosticsConformance
{
    [Fact]
    public async Task Inbound_CompletesLegacyDispatch_ButRetainsTypedNoRouteAndIdentity()
    {
        await using var world = await VirtualWorld.StartAsync();
        await world.WhenSendsAsync("diagnostic-user", "missing-agent", "unroutable-marker");

        var observed = world.Adapter.InboundOutcomes.ShouldHaveSingleItem();
        observed.Message.SenderId.ShouldBe("diagnostic-user");
        observed.Message.Content.ShouldBe("unroutable-marker");
        observed.Outcome.ShouldBe(BotNexus.Gateway.Channels.ChannelDispatchOutcome.Dispatched);
        observed.Error.ShouldBeNull();
        var acceptance = world.Adapter.InboundAcceptances.ShouldHaveSingleItem();
        acceptance.Message.ShouldBeSameAs(observed.Message);
        acceptance.Result.Status.ShouldBe(InboundDispatchStatus.NoRoute);
        world.Provider.TurnCount.ShouldBe(0);
        world.DescribeInboundProgress().ShouldContain("NoRoute");
    }

    [Fact]
    public async Task Inbound_DispatchException_IsRecordedAndRethrown_AndResetClearsDiagnostics()
    {
        var adapter = new VirtualChannelAdapter();
        var failure = new InvalidOperationException("pre-provider-marker");
        await ((IChannelAdapter)adapter).StartAsync(new ThrowingDispatcher(failure));
        var message = new InboundMessage
        {
            ChannelType = ChannelKey.From("virtual"),
            SenderId = "alice",
            Sender = CitizenId.Of(UserId.From("alice")),
            ChannelAddress = ChannelAddress.From("alice"),
            Content = "failed-marker"
        };

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => adapter.SimulateInboundAsync(message));
        thrown.ShouldBeSameAs(failure);
        var observed = adapter.InboundOutcomes.ShouldHaveSingleItem();
        observed.Message.ShouldBeSameAs(message);
        observed.Outcome.ShouldBeNull();
        observed.Error.ShouldBeSameAs(failure);
        adapter.Reset();
        adapter.InboundOutcomes.ShouldBeEmpty();
        adapter.InboundAcceptances.ShouldBeEmpty();
        await adapter.StopAsync();
    }

    [Fact]
    public void Provider_RecordsEntryIdentity_BeforeResponseFactoryThrows()
    {
        var provider = new ScenarioFakeApiProvider((_, _) => throw new InvalidOperationException("factory-marker"));
        var context = new Context(null, [new UserMessage("provider-marker", 0)]);

        Should.Throw<InvalidOperationException>(() => provider.StreamSimple(ScenarioFakeApiProvider.CreateModel(), context));

        provider.TurnCount.ShouldBe(1);
        var entry = provider.Invocations.ShouldHaveSingleItem();
        entry.TurnIndex.ShouldBe(0);
        entry.UserText.ShouldBe("provider-marker");
    }

    [Fact]
    public async Task Provider_CapturesConcurrentEntries_WithoutLosingOrDuplicatingIdentities()
    {
        var provider = new ScenarioFakeApiProvider();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            var context = new Context(null, [new UserMessage($"entry-{i}", 0)]);
            _ = await provider.Stream(ScenarioFakeApiProvider.CreateModel(), context).GetResultAsync();
        })));

        provider.TurnCount.ShouldBe(16);
        provider.Invocations.Select(i => i.TurnIndex).Order().ShouldBe(Enumerable.Range(0, 16));
        provider.Invocations.Select(i => i.UserText).Order().ShouldBe(Enumerable.Range(0, 16).Select(i => $"entry-{i}").Order());
    }

    private sealed class ThrowingDispatcher(Exception failure) : IChannelDispatcher
    {
        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => Task.FromException(failure);
    }
}
