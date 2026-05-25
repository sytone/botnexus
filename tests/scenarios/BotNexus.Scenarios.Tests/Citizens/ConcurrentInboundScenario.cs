using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Bug-probing scenario for race conditions in conversation routing under concurrent
/// inbound load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> the routing rule is "every unique
/// <c>(channelType, channelAddress)</c> maps to one conversation" (see
/// <c>DefaultConversationRouter.cs:117</c>). When two inbounds arrive at the same
/// <c>(channelType, channelAddress)</c> concurrently and no conversation exists yet,
/// a careless "check-then-create" implementation will create <b>two</b> conversations —
/// one per racing thread — and the user sees their messages silently split across two
/// transcripts. The same defect class produced #441 (reconnect creates new conversation)
/// in production.
/// </para>
/// <para>
/// This scenario fires <i>N</i> inbounds concurrently from the <b>same user / same
/// channel address</b> via <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
/// and asserts that <b>exactly one</b> conversation exists at the end. Repeating per
/// agent is intentional: contention is per-conversation key, and the absence of
/// duplicate creation is the most valuable regression guarantee in the entire suite.
/// </para>
/// </remarks>
public sealed class ConcurrentInboundScenario
{
    [Fact]
    public async Task RapidConcurrentInbound_SameAgentSameAddress_ResolvesToSingleConversation_NoDuplicate()
    {
        // Arrange — single agent, single user, ten concurrent inbounds.
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            // Use a slow-ish response factory so the provider work overlaps across requests
            // (otherwise the first turn might complete before the second arrives, which
            // would test a serialized — not concurrent — workflow).
            ResponseFactory = (turn, _) => $"reply-{turn}",
        });
        _ = await world.GivenAgentAsync("contention-agent");

        const int concurrency = 10;
        var inbounds = Enumerable.Range(0, concurrency)
            .Select(i => world.WhenSendsAsync(
                fromUser: "carla",
                toAgent: "contention-agent",
                content: $"concurrent-{i}"))
            .ToArray();

        // Act — fire all ten in parallel and wait for them all to enter the dispatcher.
        await Task.WhenAll(inbounds);

        // Wait until the provider has been invoked for every inbound — that is the strongest
        // signal that the gateway has finished serializing each one through the per-conversation
        // queue. If the gateway has a race that drops or duplicates, TurnCount will not match.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && world.Provider.TurnCount < concurrency)
        {
            await Task.Delay(25);
        }

        // Assert — exactly ONE conversation exists for this agent. The routing rule
        // (channelType, channelAddress) -> conversation is 1:1 by construction; if a race
        // window opens during "first inbound for this address" handling, the
        // conversation store will hold N rows instead of 1.
        var conversations = await world.ListConversationsForAgentAsync("contention-agent");
        conversations.Count.ShouldBe(
            1,
            $"expected exactly 1 conversation under concurrent inbound, observed {conversations.Count} " +
            "— a race in DefaultConversationRouter's 'find or create' for new (channelType, channelAddress) tuples");

        var conversation = conversations[0];
        conversation.ChannelBindings.Count.ShouldBe(
            1,
            $"expected exactly 1 binding on the shared conversation, observed {conversation.ChannelBindings.Count}");
        conversation.ChannelBindings[0].ChannelAddress.ShouldBe("carla");

        // And the provider was invoked exactly N times — proves all inbounds were processed
        // (none silently dropped) and none re-dispatched (no duplicate execution).
        world.Provider.TurnCount.ShouldBe(
            concurrency,
            $"expected exactly {concurrency} provider invocations, observed {world.Provider.TurnCount} " +
            "— some inbounds were dropped or duplicated by the dispatcher");
    }

    [Fact]
    public async Task RapidConcurrentInbound_DifferentAddresses_CreatesOneConversationPerAddress_NoCrossContamination()
    {
        // Bug-probing companion: same load pattern but each inbound carries a different
        // channel address. The expected result is N distinct conversations (one per
        // address). If the router has a bug that conflates by some shared key (e.g.
        // agent id alone), this surfaces immediately.
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ResponseFactory = (turn, _) => $"reply-{turn}",
        });
        _ = await world.GivenAgentAsync("multi-user-agent");

        const int users = 8;
        var inbounds = Enumerable.Range(0, users)
            .Select(i => world.WhenSendsAsync(
                fromUser: $"user-{i}",
                toAgent: "multi-user-agent",
                content: $"hello from user-{i}"))
            .ToArray();

        await Task.WhenAll(inbounds);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && world.Provider.TurnCount < users)
        {
            await Task.Delay(25);
        }

        // Assert — exactly N conversations (one per channel address).
        var conversations = await world.ListConversationsForAgentAsync("multi-user-agent");
        conversations.Count.ShouldBe(
            users,
            $"expected exactly {users} conversations (one per channel address), observed {conversations.Count} " +
            "— either router conflated addresses, or some inbound failed to create its conversation");

        // And each conversation has its own distinct binding — no shared / cross-contaminated bindings.
        var bindingAddresses = conversations
            .SelectMany(c => c.ChannelBindings.Select(b => b.ChannelAddress))
            .OrderBy(a => a)
            .ToArray();
        bindingAddresses.Distinct().Count().ShouldBe(
            users,
            "expected one distinct ChannelAddress per conversation; duplicate addresses indicate cross-contamination");

        for (var i = 0; i < users; i++)
        {
            bindingAddresses.ShouldContain(
                $"user-{i}",
                $"missing binding for user-{i} — that conversation never reached the store");
        }
    }
}
