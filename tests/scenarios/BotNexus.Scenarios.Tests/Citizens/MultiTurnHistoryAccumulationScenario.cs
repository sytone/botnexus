using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Bug-probing scenario for multi-turn history accumulation and the LLM-context projection
/// (Phase 3b — <c>SessionContextProjector</c>, shipped in PR #535).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> the canonical "history -> LLM context" projection is the single
/// most load-bearing piece of code in the conversation lifecycle. If it filters too
/// aggressively the agent loses memory mid-conversation; if it filters too leniently
/// historical / sentinel entries leak back into prompts. Before PR #535 the projection
/// was inlined in <c>InProcessIsolationStrategy</c>; now it lives in
/// <c>BotNexus.Gateway.Sessions.SessionContextProjector</c> and is supposed to be the
/// single source of truth for "what does the LLM see".
/// </para>
/// <para>
/// This scenario sends N turns to one conversation and uses the fake provider's response
/// factory to <b>capture every <see cref="Context"/> the gateway hands to the LLM</b>,
/// then asserts:
/// <list type="number">
/// <item>The LLM-visible message count <b>grows monotonically</b> across turns (history
///       accumulates correctly).</item>
/// <item>The user content of each prior turn is faithfully reproduced in subsequent
///       contexts — no silent message loss or reordering.</item>
/// <item>The session store's <c>History.Count</c> matches the on-the-wire growth (history
///       is durably persisted, not just held in memory for one turn).</item>
/// <item>The system prompt the agent declared is present in every context (instruction
///       loading is consistent — no "system prompt only loaded once" regression).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class MultiTurnHistoryAccumulationScenario
{
    [Fact]
    public async Task MultiTurn_HistoryAccumulates_AndProviderSeesGrowingContext_WithSystemPromptOnEveryTurn()
    {
        // Arrange — a response factory that records the LLM-visible context for every turn.
        // This is the single most valuable instrumentation in the harness: it lets a
        // scenario inspect EXACTLY what the LLM saw, which is the only honest definition
        // of "the agent received this history".
        var capturedContexts = new List<Context>();
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ResponseFactory = (turn, context) =>
            {
                capturedContexts.Add(context);
                return $"assistant-reply-{turn}";
            },
        });
        _ = await world.GivenAgentAsync("multi-turn-agent", systemPrompt: "I am a multi-turn test agent.");

        // Act — three sequential turns from the same user (await each so the LLM round-trips
        // serialize correctly and we get a deterministic context sequence).
        await world.WhenSendsAsync(fromUser: "diana", toAgent: "multi-turn-agent", content: "turn 1 — hello");
        _ = await world.WaitForReplyAsync(channelAddress: "diana");

        await world.WhenSendsAsync(fromUser: "diana", toAgent: "multi-turn-agent", content: "turn 2 — what did I say first?");
        _ = await world.WaitForReplyAsync(channelAddress: "diana");

        await world.WhenSendsAsync(fromUser: "diana", toAgent: "multi-turn-agent", content: "turn 3 — and after that?");
        _ = await world.WaitForReplyAsync(channelAddress: "diana");

        // Assert — three turns dispatched, three contexts captured.
        world.Provider.TurnCount.ShouldBe(3);
        capturedContexts.Count.ShouldBe(3, "expected one Context capture per LLM round-trip");

        // INVARIANT 1: every context carries the agent's system prompt. If the prompt was
        // only loaded on the first turn (which is what F-3d "systemPromptInitialized" used
        // to gate before PR #538), the agent would forget its identity mid-conversation.
        for (var i = 0; i < capturedContexts.Count; i++)
        {
            capturedContexts[i].SystemPrompt.ShouldNotBeNull($"turn {i + 1} lost the system prompt");
            capturedContexts[i].SystemPrompt!.ShouldContain(
                "multi-turn test agent",
                customMessage: $"turn {i + 1} system prompt was rewritten or truncated; agent identity drifted");
        }

        // INVARIANT 2: the LLM-visible message count grows monotonically. If history is
        // wiped between turns the agent has no idea what was said before; if it grows
        // discontinuously (e.g. +3 between turns when it should be +2) some artifact like
        // tool sentinels are leaking into the projection.
        //
        // NOTE: turn 1's exact count is NOT asserted to be 1 because the gateway is allowed
        // to inject a session-start system entry / instructional preamble. The contract we
        // care about is GROWTH (no message loss across turns) — not the exact starting count.
        var turn1Messages = capturedContexts[0].Messages.Count;
        var turn2Messages = capturedContexts[1].Messages.Count;
        var turn3Messages = capturedContexts[2].Messages.Count;

        turn1Messages.ShouldBeGreaterThanOrEqualTo(
            1,
            $"turn 1 must include at least the new user message, got {turn1Messages}");
        turn2Messages.ShouldBeGreaterThan(
            turn1Messages,
            $"turn 2 context ({turn2Messages} msgs) did not grow over turn 1 ({turn1Messages} msgs) — history was not accumulated");
        turn3Messages.ShouldBeGreaterThan(
            turn2Messages,
            $"turn 3 context ({turn3Messages} msgs) did not grow over turn 2 ({turn2Messages} msgs) — history was not accumulated");

        // Helper: human-readable rendering of a Context for failure diagnostics.
        static string Render(Context ctx) => string.Join(" | ", ctx.Messages.Select(m => m switch
        {
            UserMessage u => "user:" + (u.Content.IsText
                ? u.Content.Text
                : string.Concat(u.Content.Blocks?.OfType<TextContent>().Select(t => t.Text) ?? [])),
            AssistantMessage a => "assistant:" + string.Concat(a.Content.OfType<TextContent>().Select(t => t.Text)),
            ToolResultMessage t => $"tool({t.ToolName}):{t.IsError}",
            _ => m.GetType().Name,
        }));

        // Diagnostic: dump turn 1 message roles + content prefixes so any future surprise in
        // pre-roll content is immediately legible in the test output.
        var turn1Diagnostic = Render(capturedContexts[0]);
        turn1Diagnostic.ShouldContain(
            "turn 1 — hello",
            customMessage: $"turn 1 LLM context did not contain the originating user message; observed: [{turn1Diagnostic}]");

        // INVARIANT 3: the user's earlier message text is preserved verbatim in later contexts.
        // The projector must not silently truncate, summarise, or re-order historical messages
        // without an explicit compaction event.
        var turn2Render = Render(capturedContexts[1]);
        var turn3Render = Render(capturedContexts[2]);

        var turn2UserContent = string.Concat(capturedContexts[1].Messages
            .OfType<UserMessage>()
            .Select(m => m.Content.IsText
                ? m.Content.Text
                : string.Concat(m.Content.Blocks?.OfType<TextContent>().Select(t => t.Text) ?? [])));
        turn2UserContent.ShouldContain(
            "turn 1 — hello",
            customMessage: $"turn 1's user message was not visible in turn 2 LLM context — projector dropped historical user input.{Environment.NewLine}Turn 2 context: [{turn2Render}]");

        var turn3UserContent = string.Concat(capturedContexts[2].Messages
            .OfType<UserMessage>()
            .Select(m => m.Content.IsText
                ? m.Content.Text
                : string.Concat(m.Content.Blocks?.OfType<TextContent>().Select(t => t.Text) ?? [])));
        turn3UserContent.ShouldContain(
            "turn 1 — hello",
            customMessage: $"turn 1's user message was not visible in turn 3 LLM context.{Environment.NewLine}Turn 3 context: [{turn3Render}]");
        turn3UserContent.ShouldContain(
            "turn 2 — what did I say first?",
            customMessage: $"turn 2's user message was not visible in turn 3 LLM context.{Environment.NewLine}Turn 3 context: [{turn3Render}]");

        // INVARIANT 4: the conversation has exactly one session, and the session store
        // holds the durable record of all six entries (3 user + 3 assistant).
        var conversations = await world.ListConversationsForAgentAsync("multi-turn-agent");
        var conversation = conversations.ShouldHaveSingleItem();
        conversation.ActiveSessionId.ShouldNotBeNull();

        var session = await world.GetSessionAsync(conversation.ActiveSessionId!);
        session.ShouldNotBeNull();
        session.HistoryCount.ShouldBeGreaterThanOrEqualTo(
            6,
            $"expected at least 6 session entries (3 user + 3 assistant), session has {session.HistoryCount} — " +
            "some turns were dropped from durable history");
    }

    [Fact]
    public async Task MultiTurn_AssistantPriorReplies_AreVisible_InSubsequentContexts()
    {
        // Companion bug-probe: prove the assistant's PRIOR replies are part of the context
        // the LLM sees on subsequent turns. A regression that excludes assistant messages
        // from the projection makes every reply look like a single-turn response from the
        // model's perspective — which silently breaks any multi-turn reasoning.
        var capturedContexts = new List<Context>();
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ResponseFactory = (turn, context) =>
            {
                capturedContexts.Add(context);
                return $"the-assistant-spoke-on-turn-{turn}";
            },
        });
        _ = await world.GivenAgentAsync("multi-turn-asst");

        await world.WhenSendsAsync(fromUser: "ed", toAgent: "multi-turn-asst", content: "first user");
        _ = await world.WaitForReplyAsync(channelAddress: "ed");

        await world.WhenSendsAsync(fromUser: "ed", toAgent: "multi-turn-asst", content: "second user");
        _ = await world.WaitForReplyAsync(channelAddress: "ed");

        // Turn 2's context must contain turn 1's assistant reply.
        var turn2 = capturedContexts[1];
        var hasAssistant = turn2.Messages.OfType<AssistantMessage>().Any();
        hasAssistant.ShouldBeTrue(
            "turn 2 LLM context contained NO AssistantMessage entries — the projector excluded prior assistant replies");

        var assistantContent = string.Concat(turn2.Messages
            .OfType<AssistantMessage>()
            .SelectMany(m => m.Content)
            .OfType<TextContent>()
            .Select(t => t.Text));
        assistantContent.ShouldContain(
            "the-assistant-spoke-on-turn-0",
            customMessage: "turn 1 assistant content was not visible in turn 2 LLM context");
    }
}
