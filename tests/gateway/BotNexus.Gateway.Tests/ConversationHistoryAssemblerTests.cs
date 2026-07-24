using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="ConversationHistoryAssembler"/>. These exercise the history-assembly
/// state machine (boundary markers, NO_REPLY/fold filtering, compaction projection, #732 fallback,
/// newest-first paging) in isolation -- no MVC pipeline required -- which is the whole point of
/// having extracted it out of <see cref="ConversationsController.GetHistory"/> (#1389).
/// </summary>
public sealed class ConversationHistoryAssemblerTests
{
    [Fact]
    public async Task AssembleAsync_UnknownConversation_ReturnsNull()
    {
        var sessions = new InMemorySessionStore();
        var assembler = new ConversationHistoryAssembler(new InMemoryConversationStore(), sessions);

        var result = await assembler.AssembleAsync(ConversationId.From("c_missing"), limit: 50, offset: 0);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task AssembleAsync_SingleSession_ReturnsMessagesInOrder_NoBoundary()
    {
        var conversationId = ConversationId.From("c_single");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "hi there", Timestamp = Ts(1) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(2);
        result.Entries.Count.ShouldBe(2);
        result.Entries.ShouldAllBe(e => e.Kind == "message");
        result.Entries[0].Content.ShouldBe("hello");
        result.Entries[0].Role.ShouldBe("user");
        result.Entries[1].Content.ShouldBe("hi there");
        result.Entries[1].Role.ShouldBe("assistant");
    }

    [Fact]
    public async Task AssembleAsync_MultipleSessions_InsertsBoundaryMarkerBetweenThem()
    {
        var conversationId = ConversationId.From("c_two_sessions");
        var sessions = new InMemorySessionStore();

        var first = await sessions.GetOrCreateAsync(SessionId.From("s-first"), AgentId.From("quill"));
        first.Session.ConversationId = conversationId;
        first.Session.CreatedAt = Ts(0);
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "in-first", Timestamp = Ts(1) });
        await sessions.SaveAsync(first);

        var second = await sessions.GetOrCreateAsync(SessionId.From("s-second"), AgentId.From("quill"));
        second.Session.ConversationId = conversationId;
        second.Session.CreatedAt = Ts(10);
        second.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "in-second", Timestamp = Ts(11) });
        await sessions.SaveAsync(second);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        // first message, boundary, second message
        result!.Entries.Count.ShouldBe(3);
        result.Entries[0].Kind.ShouldBe("message");
        result.Entries[0].Content.ShouldBe("in-first");
        result.Entries[1].Kind.ShouldBe("boundary");
        result.Entries[1].Reason.ShouldBe("session_end");
        result.Entries[1].SessionId.ShouldBe("s-first"); // boundary attributes to the PREVIOUS session
        result.Entries[2].Kind.ShouldBe("message");
        result.Entries[2].Content.ShouldBe("in-second");
    }

    [Fact]
    public async Task AssembleAsync_SkipsNoReplyAssistantEntries()
    {
        var conversationId = ConversationId.From("c_no_reply");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "ping", Timestamp = Ts(0) });
        // Deliberate cron no-op -- must be dropped (#773). Padded with whitespace to verify trimming.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "  NO_REPLY  ", Timestamp = Ts(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "real reply", Timestamp = Ts(2) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(2);
        result.Entries.ShouldNotContain(e => e.Content != null && e.Content.Contains("NO_REPLY"));
        result.Entries[0].Content.ShouldBe("ping");
        result.Entries[1].Content.ShouldBe("real reply");
    }

    [Fact]
    public async Task AssembleAsync_DoesNotSkipUserContentThatHappensToSayNoReply()
    {
        // The NO_REPLY drop only applies to ASSISTANT entries. A user literally typing "NO_REPLY"
        // must still appear in history.
        var conversationId = ConversationId.From("c_user_no_reply");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "NO_REPLY", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(1);
        result.Entries[0].Role.ShouldBe("user");
        result.Entries[0].Content.ShouldBe("NO_REPLY");
    }

    [Fact]
    public async Task AssembleAsync_SkipsFoldedHistoryEntries()
    {
        var conversationId = ConversationId.From("c_folded");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "kept", Timestamp = Ts(0) });
        // Folded entry superseded by a compaction summary -- must be skipped.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "folded-away", Timestamp = Ts(1), IsHistory = true });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(1);
        result.Entries.ShouldNotContain(e => e.Content == "folded-away");
        result.Entries[0].Content.ShouldBe("kept");
    }

    [Fact]
    public async Task AssembleAsync_ProjectsCompactionSummaryAsCompactionMarker()
    {
        var conversationId = ConversationId.From("c_compaction");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "before", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "summary of earlier turns", Timestamp = Ts(1), IsCompactionSummary = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "after", Timestamp = Ts(2) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(3);
        var markers = result.Entries.Where(e => e.Kind == "compaction").ToList();
        markers.Count.ShouldBe(1);
        markers[0].Reason.ShouldBe("compaction");
        markers[0].Content.ShouldBe("summary of earlier turns");
        markers[0].SessionId.ShouldBe("s-1");
        result.Entries[0].Content.ShouldBe("before");
        result.Entries[2].Content.ShouldBe("after");
    }

    [Fact]
    public async Task AssembleAsync_PagesFromNewest_ByDefault()
    {
        var conversationId = ConversationId.From("c_paging");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}", Timestamp = Ts(i) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 3, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(10);
        result.Entries.Count.ShouldBe(3);
        result.Entries[0].Content.ShouldBe("m-7");
        result.Entries[^1].Content.ShouldBe("m-9");
    }

    [Fact]
    public async Task AssembleAsync_PagesBackwardWithOffset()
    {
        var conversationId = ConversationId.From("c_paging_offset");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}", Timestamp = Ts(i) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 3, offset: 3);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(10);
        result.Entries.Count.ShouldBe(3);
        result.Entries[0].Content.ShouldBe("m-4");
        result.Entries[^1].Content.ShouldBe("m-6");
    }

    [Fact]
    public async Task AssembleAsync_OffsetBeyondTotal_ReturnsEmptyPage_ButReportsTotal()
    {
        var conversationId = ConversationId.From("c_offset_beyond");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        for (var i = 0; i < 3; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}", Timestamp = Ts(i) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 100);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(3);
        result.Entries.ShouldBeEmpty();
        result.Offset.ShouldBe(100);
    }

    [Fact]
    public async Task AssembleAsync_FallsBackToActiveSessionId_WhenNoLinkedSessions()
    {
        // #732: orphaned session (no conversation_id stamp) must surface via ActiveSessionId fallback.
        var conversationId = ConversationId.From("c_orphan");
        var orphanSessionId = SessionId.From("s-orphan");
        var sessions = new InMemorySessionStore();

        var session = await sessions.GetOrCreateAsync(orphanSessionId, AgentId.From("aurum"));
        // Deliberately do NOT stamp session.Session.ConversationId.
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "orphan-msg", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(CreateConversation(conversationId, "aurum", orphanSessionId));
        var assembler = new ConversationHistoryAssembler(conversations, sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(1);
        result.Entries[0].Content.ShouldBe("orphan-msg");
    }

    [Fact]
    public async Task AssembleAsync_NoLinkedSessions_NoActiveSessionId_ReturnsEmptyNotNull()
    {
        var conversationId = ConversationId.From("c_empty");
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(CreateConversation(conversationId, "aurum")); // no active session
        var assembler = new ConversationHistoryAssembler(conversations, sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(0);
        result.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AssembleAsync_ProjectsTypedMessageKind_ForCompletionAndResponseAndOrdinary()
    {
        // #2149: the conversation history projection must expose the orthogonal typed kind so
        // replay recovers the message / subagent-completion / subagent-response distinction; a
        // legacy/unstamped entry projects as "message".
        var conversationId = ConversationId.From("c_kind");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-kind"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "completion", Timestamp = Ts(0), Kind = MessageKind.SubAgentCompletion });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "parent reply", Timestamp = Ts(1), Kind = MessageKind.SubAgentResponse });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "ordinary", Timestamp = Ts(2) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);
        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        var byContent = result!.Entries.Where(e => e.Content != null).ToDictionary(e => e.Content!);
        // The history-envelope Kind stays "message" (not "boundary"); the orthogonal presentation
        // kind is carried on the new MessageKind field so replay recovers it.
        byContent["completion"].Kind.ShouldBe("message");
        byContent["completion"].MessageKind.ShouldBe("subagent-completion");
        byContent["parent reply"].MessageKind.ShouldBe("subagent-response");
        // Role stays the LLM role, orthogonal to the presentation kind.
        byContent["parent reply"].Role.ShouldBe("assistant");
        byContent["ordinary"].MessageKind.ShouldBe("message");
    }

    [Fact]
    public async Task AssembleAsync_ChannelProjection_CanSuppressSubAgentResponse_ByTypedKind()
    {
        // #2149: a channel/UI projection must be able to decide to suppress or specially render a
        // subagent-response using the typed kind ALONE - never by parsing role, ids, or text.
        var conversationId = ConversationId.From("c_suppress");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-suppress"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hi", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "internal parent reply", Timestamp = Ts(1), Kind = MessageKind.SubAgentResponse });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);
        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        // Emulate a channel projection choosing to hide subagent-response entries using ONLY the
        // typed kind. It must be able to do so without inspecting role, sender/session ids, or text.
        var visible = result!.Entries
            .Where(e => e.MessageKind != MessageKind.SubAgentResponse.Value)
            .ToList();

        visible.ShouldContain(e => e.Content == "hi");
        visible.ShouldNotContain(e => e.Content == "internal parent reply");
    }

    private static DateTimeOffset Ts(int minutes) => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static async Task<ConversationHistoryAssembler> NewAssemblerAsync(ConversationId conversationId, string agentId, InMemorySessionStore sessions)
    {
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(CreateConversation(conversationId, agentId));
        return new ConversationHistoryAssembler(conversations, sessions);
    }

    private static Conversation CreateConversation(ConversationId conversationId, string agentId, SessionId? activeSessionId = null)
        => new()
        {
            ConversationId = conversationId,
            AgentId = AgentId.From(agentId),
            Title = "Default",
            IsDefault = true,
            Status = ConversationStatus.Active,
            ActiveSessionId = activeSessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
