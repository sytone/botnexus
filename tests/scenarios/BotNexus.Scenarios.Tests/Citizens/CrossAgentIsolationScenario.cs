using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Bug-probing scenario for the cross-agent isolation boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters:</b> in a multi-tenant deployment with N agents registered,
/// a message addressed to agent A must <i>only</i> reach agent A. A bug in the
/// supervisor lookup, the conversation router, or the dispatch fan-out could:
/// </para>
/// <list type="bullet">
///   <item><description>Deliver agent A's prompt to agent B's LLM (cross-tenant data leak).</description></item>
///   <item><description>Insert agent A's user turn into agent B's session history (silent context contamination).</description></item>
///   <item><description>Cause agent B to reply on agent A's behalf (impersonation).</description></item>
/// </list>
/// <para>
/// All three are catastrophic in a multi-user deployment and easy to introduce
/// when refactoring the router or the supervisor. This scenario pins the
/// invariant that <see cref="ScenarioFakeApiProvider.TurnCount"/> and per-agent
/// session history are <i>partitioned by agent id</i> with zero bleed-through.
/// </para>
/// <para>
/// <b>The probe:</b> register two agents. User sends three messages, alternating
/// between them. After each send, assert the receiving agent's session
/// accumulated the turn and the non-receiving agent's session did not.
/// </para>
/// </remarks>
public sealed class CrossAgentIsolationScenario
{
    [Fact]
    public async Task TwoAgents_SameUser_EachAgentReceivesOnlyItsOwnTurns_NoCrossContamination()
    {
        var capturedByAgent = new System.Collections.Concurrent.ConcurrentDictionary<
            string,
            List<List<string>>>();
        var observedSystemPrompts = new System.Collections.Concurrent.ConcurrentBag<string>();

        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ResponseFactory = (turn, ctx) =>
            {
                // Capture the user-visible content of every UserMessage in the LLM context,
                // keyed by which agent's prompt triggered this turn. The system prompt
                // tells us which agent — see GivenAgentAsync below.
                var systemPrompt = ctx.SystemPrompt ?? string.Empty;
                observedSystemPrompts.Add(systemPrompt);
                var agentKey = systemPrompt.Contains("agent:alpha", StringComparison.Ordinal) ? "alpha"
                    : systemPrompt.Contains("agent:bravo", StringComparison.Ordinal) ? "bravo"
                    : "unknown";

                var userTexts = ctx.Messages
                    .OfType<UserMessage>()
                    .Select(m => m.Content.IsText
                        ? m.Content.Text ?? string.Empty
                        : string.Concat(m.Content.Blocks?.OfType<TextContent>().Select(t => t.Text) ?? []))
                    .ToList();

                capturedByAgent.AddOrUpdate(
                    agentKey,
                    _ => new List<List<string>> { userTexts },
                    (_, list) => { list.Add(userTexts); return list; });

                return $"reply-from-{agentKey}-turn-{turn}";
            },
        });

        _ = await world.GivenAgentAsync("alpha", systemPrompt: "agent:alpha");
        _ = await world.GivenAgentAsync("bravo", systemPrompt: "agent:bravo");

        // Three sends, alternating agents — same user, same channel address (so the
        // only discriminator is TargetAgentId on the inbound).
        await world.WhenSendsAsync(
            fromUser: "isolation-user",
            toAgent: "alpha",
            content: "ALPHA-SECRET-1");
        _ = await world.WaitForReplyAsync(channelAddress: "isolation-user");

        await world.WhenSendsAsync(
            fromUser: "isolation-user",
            toAgent: "bravo",
            content: "BRAVO-SECRET-1");
        _ = await world.WaitForReplyAsync(channelAddress: "isolation-user");

        await world.WhenSendsAsync(
            fromUser: "isolation-user",
            toAgent: "alpha",
            content: "ALPHA-SECRET-2");
        _ = await world.WaitForReplyAsync(channelAddress: "isolation-user");

        // Provider was invoked once per inbound — three total.
        world.Provider.TurnCount.ShouldBe(3);

        // Each agent saw exactly the user turns addressed to it.
        capturedByAgent.ShouldContainKey(
            "alpha",
            $"alpha never received a turn — supervisor routing regression. Observed system prompts: [{string.Join("] [", observedSystemPrompts)}]");
        capturedByAgent.ShouldContainKey(
            "bravo",
            $"bravo never received a turn — supervisor routing regression. Observed system prompts: [{string.Join("] [", observedSystemPrompts)}]");

        var alphaInvocations = capturedByAgent["alpha"];
        var bravoInvocations = capturedByAgent["bravo"];

        alphaInvocations.Count.ShouldBe(
            2,
            $"alpha should have been invoked twice, observed {alphaInvocations.Count}");
        bravoInvocations.Count.ShouldBe(
            1,
            $"bravo should have been invoked once, observed {bravoInvocations.Count}");

        // CRITICAL: alpha's LLM context must NEVER contain BRAVO-SECRET-* and vice versa.
        // This is the cross-tenant data leak probe.
        foreach (var (turnIndex, alphaContext) in alphaInvocations.Select((c, i) => (i, c)))
        {
            var alphaCombined = string.Join("|", alphaContext);
            alphaCombined.ShouldNotContain(
                "BRAVO-SECRET",
                customMessage: $"alpha turn #{turnIndex} contains BRAVO-SECRET — cross-agent data leak: [{alphaCombined}]");
        }

        foreach (var (turnIndex, bravoContext) in bravoInvocations.Select((c, i) => (i, c)))
        {
            var bravoCombined = string.Join("|", bravoContext);
            bravoCombined.ShouldNotContain(
                "ALPHA-SECRET",
                customMessage: $"bravo turn #{turnIndex} contains ALPHA-SECRET — cross-agent data leak: [{bravoCombined}]");
        }

        // Alpha's second invocation should have ALPHA-SECRET-1 visible (history
        // accumulated within its session) AND ALPHA-SECRET-2 as the latest user turn.
        var alphaSecondContext = alphaInvocations[1];
        alphaSecondContext.Any(t => t.Contains("ALPHA-SECRET-1")).ShouldBeTrue(
            $"alpha's second turn lost ALPHA-SECRET-1 from history — session continuity broken: [{string.Join("|", alphaSecondContext)}]");
        alphaSecondContext.Any(t => t.Contains("ALPHA-SECRET-2")).ShouldBeTrue(
            $"alpha's second turn missing ALPHA-SECRET-2 as latest user message: [{string.Join("|", alphaSecondContext)}]");

        // Each agent owns its own conversation — verifies the supervisor/router
        // created separate conversations rather than reusing one across agents.
        var alphaConversations = await world.ListConversationsForAgentAsync("alpha");
        var bravoConversations = await world.ListConversationsForAgentAsync("bravo");
        alphaConversations.Count.ShouldBe(1, $"alpha should own exactly 1 conversation, observed {alphaConversations.Count}");
        bravoConversations.Count.ShouldBe(1, $"bravo should own exactly 1 conversation, observed {bravoConversations.Count}");
        alphaConversations[0].ConversationId.ShouldNotBe(
            bravoConversations[0].ConversationId,
            "two agents must not share a single conversation — multi-tenant isolation regression");
    }
}
