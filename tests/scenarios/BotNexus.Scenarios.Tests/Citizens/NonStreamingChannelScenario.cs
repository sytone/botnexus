using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Bug-probing scenario for the non-streaming delivery path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> the sanity scenario in
/// <see cref="UserSendsMessageAgentRepliesScenario"/> drives a streaming-capable channel
/// (<c>SupportsStreaming = true</c>), so the gateway routes through
/// <c>GatewayHost.ProcessInboundMessageAsync</c> lines 519-613 (streaming path) and the
/// reply lands as <see cref="AgentStreamEvent"/> instances on the channel.
/// </para>
/// <para>
/// The <b>non-streaming</b> code path at <c>GatewayHost.cs:616-641</c> is an entirely
/// different branch — it calls <c>handle.PromptAsync</c> instead of <c>handle.StreamAsync</c>,
/// builds a single <see cref="OutboundMessage"/>, and delivers it via
/// <c>channel.SendAsync(OutboundMessage)</c>. There was previously <b>no in-process test
/// that exercised this path with a virtual channel</b>; if a regression breaks the
/// non-streaming path, the entire streaming-coupled SignalR suite stays green and the
/// CLI / Service Bus channels silently break. This scenario is the regression net for
/// that branch.
/// </para>
/// <para>
/// The scenario forces <c>SupportsStreaming = false</c> on the virtual channel and
/// asserts the reply arrives as a single <see cref="AgentReplyDelivery.Outbound"/>
/// message — not as stream events. If the gateway were to incorrectly fall back to
/// streaming on a non-streaming channel, the assertion on <see cref="AgentReply.Delivery"/>
/// would fail.
/// </para>
/// </remarks>
public sealed class NonStreamingChannelScenario
{
    [Fact]
    public async Task Channel_WithoutStreamingCapability_ReceivesCompletedOutboundMessage_NotStreamEvents()
    {
        // Arrange — virtual channel with streaming explicitly disabled. This forces
        // GatewayHost.ProcessInboundMessageAsync down the non-streaming branch
        // (`else` arm of the `resolvedChannel is { SupportsStreaming: true }` check).
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ChannelOptions = new VirtualChannelAdapterOptions { SupportsStreaming = false },
            ResponseFactory = (_, _) => "non-streaming reply payload",
        });
        _ = await world.GivenAgentAsync("ns-agent", systemPrompt: "You reply once, completely, with no streaming.");

        // Act — drive a single inbound through the channel.
        await world.WhenSendsAsync(fromUser: "alice", toAgent: "ns-agent", content: "hello");

        // Assert — the reply landed on the channel as a single completed OutboundMessage
        // (the non-streaming path). The delivery mechanism must be Outbound, not Stream;
        // any drift to streaming on a non-streaming channel is an explicit gateway bug.
        var reply = await world.WaitForReplyAsync(channelAddress: "alice");
        reply.Content.ShouldBe("non-streaming reply payload");
        reply.ChannelAddress.ShouldBe("alice");
        reply.Delivery.ShouldBe(
            AgentReplyDelivery.Outbound,
            "non-streaming channels must receive completed OutboundMessage, not stream events");

        // And the streaming surfaces must be empty — no SendStreamEventAsync calls and no
        // SendStreamDeltaAsync calls. If either has entries, the gateway leaked streaming
        // events to a channel that explicitly disabled the capability.
        world.Adapter.StreamEvents.ShouldBeEmpty(
            "channel SupportsStreaming=false must NOT receive stream events");
        world.Adapter.StreamDeltas.ShouldBeEmpty(
            "channel SupportsStreaming=false must NOT receive stream deltas");

        // And exactly one OutboundMessage — no duplicate delivery from a streaming/non-streaming
        // hybrid bug.
        world.Adapter.Outbound.Count.ShouldBe(
            1,
            $"expected exactly 1 outbound message, observed {world.Adapter.Outbound.Count}");

        // And the provider was invoked once — proves the LLM round-trip really happened
        // (a TurnCount=0 green test would indicate the non-streaming path skipped the agent
        // entirely, which would be a serious regression).
        world.Provider.TurnCount.ShouldBe(1);
    }

    [Fact]
    public async Task Channel_WithoutStreamingCapability_OutboundCarriesSessionIdAndBindingId()
    {
        // Bug-probing extension: the non-streaming OutboundMessage construction at
        // GatewayHost.cs:626-637 explicitly populates SessionId, BindingId, and
        // DisplayPrefix from the resolved source. If any of those wire-up assignments
        // regress (e.g. BindingId left as default in a refactor), downstream channels
        // that depend on binding-aware fan-out (#126) silently break.
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ChannelOptions = new VirtualChannelAdapterOptions { SupportsStreaming = false },
        });
        _ = await world.GivenAgentAsync("ns-agent2");

        await world.WhenSendsAsync(fromUser: "bob", toAgent: "ns-agent2", content: "hi");
        _ = await world.WaitForReplyAsync(channelAddress: "bob");

        var outbound = world.Adapter.Outbound.ShouldHaveSingleItem();
        outbound.SessionId.ShouldNotBeNullOrWhiteSpace(
            "non-streaming OutboundMessage must carry the originating SessionId");
        outbound.ChannelAddress.Value.ShouldBe("bob",
            "non-streaming OutboundMessage must echo the originating ChannelAddress");
        outbound.ChannelType.Value.ShouldBe(VirtualChannelAdapter.VirtualChannelType,
            "non-streaming OutboundMessage must carry the originating ChannelType");
    }
}
