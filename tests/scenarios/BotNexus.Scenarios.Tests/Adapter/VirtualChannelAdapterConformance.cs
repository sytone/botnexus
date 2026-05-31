using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Adapter;

/// <summary>
/// Conformance scenarios for <see cref="VirtualChannelAdapter"/>: the foundational
/// piece of the scenario suite. If this adapter does not behave like a real
/// <see cref="IChannelAdapter"/>, every higher-level scenario built on top is suspect.
/// </summary>
public sealed class VirtualChannelAdapterConformance
{
    [Fact]
    public void VirtualChannelAdapter_ImplementsIChannelAdapter_AndReportsVirtualChannelType()
    {
        var adapter = new VirtualChannelAdapter();

        adapter.ShouldBeAssignableTo<IChannelAdapter>();
        adapter.ChannelType.ShouldBe(ChannelKey.From(VirtualChannelAdapter.VirtualChannelType));
        adapter.DisplayName.ShouldBe("Virtual");
        adapter.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void VirtualChannelAdapter_DefaultCapabilities_MatchInteractiveChannelExpectations()
    {
        var adapter = new VirtualChannelAdapter();

        adapter.SupportsStreaming.ShouldBeTrue();
        adapter.SupportsSteering.ShouldBeTrue();
        adapter.SupportsFollowUp.ShouldBeTrue();
        adapter.SupportsThinkingDisplay.ShouldBeFalse();
        adapter.SupportsToolDisplay.ShouldBeFalse();
        adapter.SupportsInboundImages.ShouldBeFalse();
    }

    [Fact]
    public void VirtualChannelAdapter_CapabilityOverrides_AreReflectedOnTheInterface()
    {
        var adapter = new VirtualChannelAdapter(new VirtualChannelAdapterOptions
        {
            SupportsStreaming = false,
            SupportsSteering = false,
            SupportsFollowUp = false,
            SupportsThinkingDisplay = true,
            SupportsToolDisplay = true,
            SupportsInboundImages = true,
            DisplayName = "Telegram-Like"
        });

        IChannelAdapter contract = adapter;
        contract.SupportsStreaming.ShouldBeFalse();
        contract.SupportsSteering.ShouldBeFalse();
        contract.SupportsFollowUp.ShouldBeFalse();
        contract.SupportsThinkingDisplay.ShouldBeTrue();
        contract.SupportsToolDisplay.ShouldBeTrue();
        contract.SupportsInboundImages.ShouldBeTrue();
        contract.DisplayName.ShouldBe("Telegram-Like");
    }

    [Fact]
    public void VirtualChannelAdapter_AdapterId_IsHonoredThroughInterfaceContract()
    {
        var adapter = new VirtualChannelAdapter(new VirtualChannelAdapterOptions { AdapterId = "vc-east" });

        IChannelAdapter contract = adapter;
        contract.AdapterId.ShouldBe("vc-east");

        var unset = new VirtualChannelAdapter();
        IChannelAdapter unsetContract = unset;
        unsetContract.AdapterId.ShouldBeNull();
    }

    [Fact]
    public async Task SimulateInbound_BeforeStart_Throws_BecauseDispatcherIsNotBound()
    {
        var adapter = new VirtualChannelAdapter();
        var inbound = NewInbound("hello");

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SimulateInboundAsync(inbound));
    }

    [Fact]
    public async Task SimulateInbound_AfterStart_DispatchesExactlyOnce_ThroughTheChannelDispatcher()
    {
        var adapter = new VirtualChannelAdapter();
        var dispatcher = new RecordingDispatcher();
        await adapter.StartAsync(dispatcher);

        var inbound = NewInbound("hello");
        await adapter.SimulateInboundAsync(inbound);

        dispatcher.Calls.Count.ShouldBe(1);
        dispatcher.Calls[0].ShouldBeSameAs(inbound);
        adapter.InboundDispatchCount.ShouldBe(1);
        adapter.InboundDispatched.ShouldHaveSingleItem().ShouldBeSameAs(inbound);
    }

    [Fact]
    public async Task SendAsync_RecordsOutboundMessages_InTheOrderTheyWereSent()
    {
        var adapter = new VirtualChannelAdapter();
        var first = NewOutbound("first");
        var second = NewOutbound("second");

        await adapter.SendAsync(first);
        await adapter.SendAsync(second);

        adapter.Outbound.Count.ShouldBe(2);
        adapter.Outbound[0].Content.ShouldBe("first");
        adapter.Outbound[1].Content.ShouldBe("second");
    }

    [Fact]
    public async Task SendStreamDelta_GroupsDeltas_ByConversationRoutingKey()
    {
        var adapter = new VirtualChannelAdapter();
        await adapter.SendStreamDeltaAsync(Target("conv-a"), "Hel");
        await adapter.SendStreamDeltaAsync(Target("conv-a"), "lo");
        await adapter.SendStreamDeltaAsync(Target("conv-b"), "Hi");

        adapter.StreamDeltas["conv-a"].ShouldBe(["Hel", "lo"]);
        adapter.StreamDeltas["conv-b"].ShouldBe(["Hi"]);
    }

    [Fact]
    public async Task SendStreamEvent_GroupsStructuredEvents_ByConversationRoutingKey()
    {
        var adapter = new VirtualChannelAdapter();
        IStreamEventChannelAdapter contract = adapter;

        var start = new AgentStreamEvent { Type = AgentStreamEventType.MessageStart };
        var delta = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "Hi" };
        var end = new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd };

        await contract.SendStreamEventAsync(Target("conv-a"), start);
        await contract.SendStreamEventAsync(Target("conv-a"), delta);
        await contract.SendStreamEventAsync(Target("conv-b"), end);

        adapter.StreamEvents["conv-a"].Select(e => e.Type)
            .ShouldBe([AgentStreamEventType.MessageStart, AgentStreamEventType.ContentDelta]);
        adapter.StreamEvents["conv-b"].Select(e => e.Type)
            .ShouldBe([AgentStreamEventType.MessageEnd]);
    }

    [Fact]
    public async Task WaitForOutbound_ResolvesAsSoonAsAMatchingMessageArrives()
    {
        var adapter = new VirtualChannelAdapter();
        var match = NewOutbound("important");
        var unrelated = NewOutbound("noise");
        await adapter.SendAsync(unrelated);

        var pending = adapter.WaitForOutboundAsync(m => m.Content == "important", TimeSpan.FromSeconds(1));
        await adapter.SendAsync(match);
        var resolved = await pending;

        resolved.ShouldBeSameAs(match);
    }

    [Fact]
    public async Task WaitForOutbound_TimesOut_WhenNoMatchingMessageArrives()
    {
        var adapter = new VirtualChannelAdapter();

        await Should.ThrowAsync<TimeoutException>(
            () => adapter.WaitForOutboundAsync(_ => false, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Reset_ClearsAllCapturedTraffic()
    {
        var adapter = new VirtualChannelAdapter();
        var dispatcher = new RecordingDispatcher();
        await adapter.StartAsync(dispatcher);

        await adapter.SimulateInboundAsync(NewInbound("in"));
        await adapter.SendAsync(NewOutbound("out"));
        await adapter.SendStreamDeltaAsync(Target("conv-a"), "delta");

        adapter.Reset();

        adapter.Outbound.ShouldBeEmpty();
        adapter.InboundDispatched.ShouldBeEmpty();
        adapter.StreamDeltas.ShouldBeEmpty();
        adapter.InboundDispatchCount.ShouldBe(0);
    }

    private static ChannelStreamTarget Target(string addressAndSessionKey) =>
        new(SessionId.From(addressAndSessionKey), ChannelAddress.From(addressAndSessionKey), null);

    private static InboundMessage NewInbound(string content) => new()
    {
        ChannelType = ChannelKey.From(VirtualChannelAdapter.VirtualChannelType),
        SenderId = "alice",
        Sender = CitizenId.Of(UserId.From("alice")),
        ChannelAddress = ChannelAddress.From("virtual:alice"),
        Content = content
    };

    private static OutboundMessage NewOutbound(string content) => new()
    {
        ChannelType = ChannelKey.From(VirtualChannelAdapter.VirtualChannelType),
        ChannelAddress = ChannelAddress.From("virtual:alice"),
        Content = content
    };

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        public List<InboundMessage> Calls { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            Calls.Add(message);
            return Task.CompletedTask;
        }
    }
}
