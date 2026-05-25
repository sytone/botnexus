using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Citizens;

/// <summary>
/// Bug-probing scenario for the canonical session reset sequence (Phase 3c —
/// <c>IConversationResetService.ResetActiveSessionAsync</c>, shipped in PR #538).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> reset is the most error-prone operation in the entire
/// conversation lifecycle. The plan's F-2c finding was: the SignalR path used to flush
/// memory before sealing, the REST path did not, and that asymmetry meant the agent
/// silently lost context across reset on one channel but not the other. PR #538
/// introduced <c>IConversationResetService.ResetActiveSessionAsync</c> as the
/// <b>single canonical reset path</b> both transports route through.
/// </para>
/// <para>
/// This scenario asserts the canonical post-reset invariant: the OLD session is
/// <c>Sealed</c>, the conversation's <c>ActiveSessionId</c> is cleared, the next
/// inbound creates a <b>new empty session</b> in the same Conversation, and the LLM
/// context for the next turn does <b>not</b> contain any history from the sealed
/// session. The fake provider captures the post-reset context so we can prove the
/// new session genuinely starts from empty — a silent "reset doesn't actually reset"
/// regression would leak the old history into the new context and break long-running
/// conversations after their first reset.
/// </para>
/// </remarks>
public sealed class ConversationResetScenario
{
    [Fact]
    public async Task ResetActiveSession_SealsOldSession_ClearsActiveSessionId_NextInboundCreatesEmptyNewSession()
    {
        // Arrange — response factory captures the LLM context for each turn so the
        // post-reset context can be inspected for old-history leakage.
        var capturedContexts = new List<Context>();
        await using var world = await VirtualWorld.StartAsync(new VirtualWorldOptions
        {
            ResponseFactory = (turn, ctx) =>
            {
                capturedContexts.Add(ctx);
                return $"reply-{turn}";
            },
        });
        _ = await world.GivenAgentAsync("reset-agent");

        // Act 1 — establish a conversation with two turns of history.
        await world.WhenSendsAsync(fromUser: "fern", toAgent: "reset-agent", content: "secret-phrase-pre-reset-A");
        _ = await world.WaitForReplyAsync(channelAddress: "fern");
        await world.WhenSendsAsync(fromUser: "fern", toAgent: "reset-agent", content: "secret-phrase-pre-reset-B");
        _ = await world.WaitForReplyAsync(channelAddress: "fern");

        var preResetConversation = (await world.ListConversationsForAgentAsync("reset-agent")).Single();
        var oldSessionId = preResetConversation.ActiveSessionId.ShouldNotBeNull(
            "conversation must have an active session before reset is meaningful");
        var oldSession = await world.GetSessionAsync(oldSessionId);
        oldSession.ShouldNotBeNull();
        oldSession.HistoryCount.ShouldBeGreaterThan(
            0,
            "pre-reset session must have history for the reset assertion to mean anything");

        // Act 2 — drive the canonical reset.
        await world.ResetSessionAsync(preResetConversation.ConversationId);

        // Assert post-reset state:
        // - The OLD session must be Sealed (no longer accepting writes).
        // - The conversation's ActiveSessionId must be cleared (lazy new-session per PR #538).
        var postResetConversation = (await world.ListConversationsForAgentAsync("reset-agent")).Single();
        postResetConversation.ConversationId.ShouldBe(
            preResetConversation.ConversationId,
            "reset must not destroy the Conversation; only seal its active session");
        postResetConversation.ActiveSessionId.ShouldBeNull(
            "post-reset ActiveSessionId must be cleared so the next inbound creates a fresh session " +
            "(PR #538 canonical reset sequence — F-2c)");

        var sealedSession = await world.GetSessionAsync(oldSessionId);
        sealedSession.ShouldNotBeNull("the old session must NOT be deleted — only sealed");
        sealedSession.Status.ShouldBe(
            "Sealed",
            $"old session status must be 'Sealed' after reset (was '{sealedSession.Status}')");
        sealedSession.HistoryCount.ShouldBe(
            oldSession.HistoryCount,
            "sealed session must retain its history — sealing is non-destructive (F-2a sibling property)");

        // Act 3 — next inbound should create a brand-new session in the SAME conversation
        // with EMPTY history. The next-turn LLM context must NOT see the sealed history.
        capturedContexts.Clear();
        await world.WhenSendsAsync(fromUser: "fern", toAgent: "reset-agent", content: "first-post-reset-turn");
        _ = await world.WaitForReplyAsync(channelAddress: "fern");

        // Assert post-reset:
        // - One fresh provider invocation.
        // - The provider's context contains the NEW user message but NOT the sealed-session content.
        capturedContexts.Count.ShouldBe(1, "first post-reset turn should invoke the provider exactly once");

        var newContext = capturedContexts[0];
        var allUserContent = string.Concat(newContext.Messages
            .OfType<UserMessage>()
            .Select(m => m.Content.IsText
                ? m.Content.Text
                : string.Concat(m.Content.Blocks?.OfType<TextContent>().Select(t => t.Text) ?? [])));

        allUserContent.ShouldContain(
            "first-post-reset-turn",
            customMessage: "the new user message must reach the LLM context");
        allUserContent.ShouldNotContain(
            "secret-phrase-pre-reset-A",
            customMessage: "reset MUST NOT leak sealed session history into the new session's LLM context — " +
            "this would be a silent reset-failure regression that breaks every reset-once-then-continue workflow");
        allUserContent.ShouldNotContain(
            "secret-phrase-pre-reset-B",
            customMessage: "reset MUST NOT leak sealed session history into the new session's LLM context");

        // And the assistant's prior replies must also not leak.
        var allAssistantContent = string.Concat(newContext.Messages
            .OfType<AssistantMessage>()
            .SelectMany(m => m.Content)
            .OfType<TextContent>()
            .Select(t => t.Text));
        allAssistantContent.ShouldNotContain(
            "reply-0",
            customMessage: "reset MUST NOT leak prior assistant replies into the new session's LLM context");
        allAssistantContent.ShouldNotContain(
            "reply-1",
            customMessage: "reset MUST NOT leak prior assistant replies into the new session's LLM context");

        // And the conversation now has a NEW active session that is NOT the old one.
        var finalConversation = (await world.ListConversationsForAgentAsync("reset-agent")).Single();
        var newSessionId = finalConversation.ActiveSessionId.ShouldNotBeNull(
            "next inbound after reset must create a fresh active session");
        newSessionId.ShouldNotBe(
            oldSessionId,
            "the new active session must NOT be the resurrected sealed session — reset created a new one");

        // And the system prompt is still loaded on the FIRST turn of the new session.
        newContext.SystemPrompt.ShouldNotBeNull(
            "system prompt must be loaded on the first turn of the new session — " +
            "F-3d invariant: empty history implies first-call, instruction loading is unconditional");
    }
}
