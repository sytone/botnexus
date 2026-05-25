using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Sanity gate for the <see cref="VirtualWorld"/> harness — proves the full
/// inbound → router → conversation → session → agent supervisor → fake provider →
/// outbound loop works in-process. If this scenario goes red, every other
/// VirtualWorld-backed scenario in the suite is suspect because the harness
/// itself is broken.
/// </summary>
public sealed class UserSendsMessageAgentRepliesScenario
{
    [Fact]
    public async Task User_SendsMessage_AgentReplies_OutboundDelivered_ToVirtualChannel()
    {
        // Arrange — boot the world with default options (streaming on, like a real
        // SignalR / Telegram channel).
        await using var world = await VirtualWorld.StartAsync();
        _ = await world.GivenAgentAsync("scenario-agent", systemPrompt: "You are a scenario test agent.");

        // Act — drive an inbound through the virtual channel just like a real channel.
        await world.WhenSendsAsync(fromUser: "amy", toAgent: "scenario-agent", content: "hello");

        // Assert — the agent's reply lands on the virtual channel addressed to the citizen,
        // whether the channel streams or delivers a single OutboundMessage.
        var reply = await world.WaitForReplyAsync(channelAddress: "amy");
        reply.Content.ShouldContain("ok");
        reply.ChannelAddress.ShouldBe("amy");

        // And the provider was actually invoked exactly once — proves the LLM round-trip
        // really happened end-to-end (vs. a swallowed exception or a silent skip path).
        world.Provider.TurnCount.ShouldBe(1);

        // And exactly one conversation now exists for the agent, with the binding pointing
        // at the user's virtual address.
        var conversations = await world.ListConversationsForAgentAsync("scenario-agent");
        var conversation = conversations.ShouldHaveSingleItem();
        conversation.AgentId.ShouldBe("scenario-agent");
        conversation.ChannelBindings.ShouldHaveSingleItem();
        conversation.ChannelBindings[0].ChannelAddress.ShouldBe("amy");
        conversation.ActiveSessionId.ShouldNotBeNull();

        // The fake provider was actually consulted exactly once — proving the LLM round-trip
        // really happened (a green test with TurnCount=0 would indicate fake-provider bypass).
        world.Provider.TurnCount.ShouldBe(1);
    }
}
