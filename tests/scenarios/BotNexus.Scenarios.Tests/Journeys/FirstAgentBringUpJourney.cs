using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Journeys;

/// <summary>
/// Journey: <b>platform install → first agent bring-up → first reply</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is the worked proof for the framework decision recorded in
/// <c>docs/development/scenario-test-framework-decision.md</c> (issue #1963): the
/// hand-rolled <see cref="VirtualWorld"/> harness on xUnit, rather than Reqnroll or TUnit.
/// The journey is expressed entirely in typed harness verbs and prose-shaped test names —
/// which is precisely the readability property a BDD framework would have been adopted to
/// buy, obtained here with no new dependency.
/// </para>
/// <para>
/// What makes this a <em>bring-up</em> journey rather than a message round-trip is that it
/// asserts the empty starting state first. A freshly installed platform has no agents, no
/// conversations and no sessions; the journey then proves that registering the very first
/// agent and sending it the very first message brings all three into existence.
/// </para>
/// </remarks>
public sealed class FirstAgentBringUpJourney
{
    [Fact]
    public async Task FreshPlatform_HasNoAgents_UntilTheFirstOneIsRegistered()
    {
        // Given a freshly installed platform — isolated temp home, nothing configured.
        await using var world = await VirtualWorld.StartAsync();

        // Then it starts out with no agents at all. This is the assertion that makes the
        // journey a bring-up: if the world arrived pre-populated, "the first agent" would
        // be meaningless and every downstream assertion would be untrustworthy.
        var beforeInstall = await world.ListAgentsAsync();
        beforeInstall.ShouldBeEmpty();

        // When the operator brings up their first agent.
        var agent = await world.GivenAgentAsync(
            "first-agent",
            systemPrompt: "You are the first agent on this platform.");

        // Then exactly that one agent is registered, under the id the operator chose.
        agent.AgentId.ShouldBe("first-agent");
        var afterInstall = await world.ListAgentsAsync();
        var registered = afterInstall.ShouldHaveSingleItem();
        registered.AgentId.ShouldBe("first-agent");
    }

    [Fact]
    public async Task Operator_BringsUpFirstAgent_AndItRespondsToTheFirstMessage()
    {
        // Given a freshly installed platform with no agents.
        await using var world = await VirtualWorld.StartAsync();
        (await world.ListAgentsAsync()).ShouldBeEmpty();

        // And the operator has brought up their first agent.
        await world.GivenAgentAsync(
            "first-agent",
            systemPrompt: "You are the first agent on this platform.");

        // And no conversation exists for it yet — bring-up registers the agent, it does not
        // manufacture conversations.
        (await world.ListConversationsForAgentAsync("first-agent")).ShouldBeEmpty();

        // When the operator sends it the very first message.
        await world.WhenSendsAsync(
            fromUser: "operator",
            toAgent: "first-agent",
            content: "are you there?");

        // Then the agent replies, delivered back to the operator on their channel address.
        var reply = await world.WaitForReplyAsync(channelAddress: "operator");
        reply.Content.ShouldContain("ok");
        reply.ChannelAddress.ShouldBe("operator");

        // And the LLM round-trip genuinely happened. Without this the journey could pass on
        // a short-circuit path that never consults the provider at all — a green test with
        // TurnCount 0 would be proving nothing about bring-up.
        world.Provider.TurnCount.ShouldBe(1);

        // And the platform materialised exactly one conversation as a side effect of that
        // first message, bound to the operator's channel address.
        var conversations = await world.ListConversationsForAgentAsync("first-agent");
        var conversation = conversations.ShouldHaveSingleItem();
        conversation.AgentId.ShouldBe("first-agent");
        var binding = conversation.ChannelBindings.ShouldHaveSingleItem();
        binding.ChannelAddress.ShouldBe("operator");

        // And an active session was opened inside that conversation, carrying the turn that
        // was just exchanged. Bring-up is only complete when all three exist: agent,
        // conversation, session.
        conversation.ActiveSessionId.ShouldNotBeNull();
        var session = await world.GetSessionAsync(conversation.ActiveSessionId!);
        session.ShouldNotBeNull();
        session!.AgentId.ShouldBe("first-agent");
        session.ConversationId.ShouldBe(conversation.ConversationId);
        session.HistoryCount.ShouldBeGreaterThan(0);
    }
}
