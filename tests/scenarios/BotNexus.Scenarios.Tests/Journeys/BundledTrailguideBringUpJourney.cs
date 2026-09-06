using BotNexus.Gateway.Configuration;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Journeys;

/// <summary>
/// Journey: <b>fresh home → gateway startup → bundled Trailguide is a live agent → first
/// platform-help conversation</b> (issue #3699, the code-plane half of #2639 AC1).
/// </summary>
/// <remarks>
/// <para>
/// Everything that existed before this journey stopped at the JSON.
/// <c>PlatformAgentReconciliationServiceTests</c> proves the entry is persisted and
/// <c>InitCommandTests</c> proves <c>init</c> emits a field-identical one —
/// but an entry is not an agent. Between the two lies <c>PlatformConfigAgentSource</c>,
/// <c>AgentDescriptorValidator</c> and <c>AgentConfigurationHostedService</c>, and a template that
/// is syntactically perfect but semantically unregistrable (a provider name nothing resolves, a key
/// the validator rejects, or a hosted-service ordering that runs registration before insertion)
/// would pass every one of those unit tests while shipping an onboarding agent that never appears.
/// </para>
/// <para>
/// The ordering claim is load-bearing and is asserted <em>within a single startup</em>: the
/// reconciler is registered ahead of <c>AgentConfigurationHostedService</c>
/// (<c>GatewayServiceCollectionExtensions</c>) precisely so the entry it inserts is visible to the
/// config agent source on the same boot. Asserting after a restart would prove the insert and
/// silently concede the ordering.
/// </para>
/// </remarks>
public sealed class BundledTrailguideBringUpJourney
{
    /// <summary>
    /// The world boots through the real configuration plane rather than the harness's
    /// programmatic registration, which is the only arrangement in which this journey means
    /// anything — see <see cref="VirtualWorldOptions.ReconcileBundledAgents"/>.
    /// </summary>
    private static VirtualWorldOptions FreshInstall() => new()
    {
        ReconcileBundledAgents = true,
        ResponseFactory = static (_, _) =>
            "BotNexus runs agents that hold conversations. Start by opening a conversation."
    };

    [Fact]
    public async Task FreshInstall_RegistersTheBundledTrailguide_InTheSameStartupThatInsertsIt()
    {
        // Given a fresh SQLite-enabled BotNexus home whose config store has never heard of the Trailguide, and a
        // gateway that boots over it exactly once.
        await using var world = await VirtualWorld.StartAsync(FreshInstall());

        // Then the bundled agent is a LIVE registered agent, not merely a config key. This is the
        // assertion the unit suite cannot make: it can only be satisfied by the reconciler's write
        // being read back, validated, and registered inside this same startup.
        var registered = await world.WaitForRegisteredAgentAsync(
            BundledPlatformAgents.TrailguideAgentId);
        registered.AgentId.ShouldBe(BundledPlatformAgents.TrailguideAgentId);
        registered.DisplayName.ShouldBe("Nexus Trailguide");

        // And it arrived alongside the operator's pre-existing agent rather than replacing it —
        // reconciliation is additive, and a journey that asserted only on the Trailguide would pass
        // against a boot that had wiped the user's own configuration.
        var agents = await world.ListAgentsAsync();
        agents.Select(a => a.AgentId).ShouldContain("seed-agent");
    }

    [Fact]
    public async Task NewUser_AsksTheBundledTrailguideForHelp_AndGetsAnAnswer()
    {
        // Given a fresh install whose Trailguide came up through the config plane.
        await using var world = await VirtualWorld.StartAsync(FreshInstall());
        await world.WaitForRegisteredAgentAsync(BundledPlatformAgents.TrailguideAgentId);

        // And no conversation exists with it yet — bring-up registers an agent, it does not
        // manufacture a conversation for one.
        (await world.ListConversationsForAgentAsync(BundledPlatformAgents.TrailguideAgentId))
            .ShouldBeEmpty();

        // When the new user asks it the question the agent exists to answer.
        await world.WhenSendsAsync(
            fromUser: "new-user",
            toAgent: BundledPlatformAgents.TrailguideAgentId,
            content: "what is BotNexus and how do I get started?");

        // Then it answers. Non-empty is asserted explicitly: an assertion set built only from "no
        // error was thrown" would pass against a gateway that registered nothing at all, which is
        // exactly the regression this journey exists to catch.
        var reply = await world.WaitForReplyAsync(channelAddress: "new-user");
        reply.Content.ShouldNotBeNullOrWhiteSpace();
        reply.ChannelAddress.ShouldBe("new-user");

        // And the reply came from a genuine LLM round-trip through the bundled agent's own
        // provider/model — a canned short-circuit would leave the provider at zero turns and prove
        // nothing about whether the inserted descriptor can actually reach a model.
        world.Provider.TurnCount.ShouldBe(1);

        // And the platform materialised the conversation under the bundled agent's id, so the
        // first-conversation half of the journey is bound to the Trailguide and not to some
        // fallback agent the router picked instead.
        var conversations = await world.ListConversationsForAgentAsync(
            BundledPlatformAgents.TrailguideAgentId);
        var conversation = conversations.ShouldHaveSingleItem();
        conversation.AgentId.ShouldBe(BundledPlatformAgents.TrailguideAgentId);
        conversation.ActiveSessionId.ShouldNotBeNull();

        var session = await world.GetSessionAsync(conversation.ActiveSessionId!);
        session.ShouldNotBeNull();
        session!.AgentId.ShouldBe(BundledPlatformAgents.TrailguideAgentId);
        session.HistoryCount.ShouldBeGreaterThan(0);
    }
}
