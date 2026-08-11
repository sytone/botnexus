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

        // Session.CreatedAt is write-once (#2316), so build the sessions directly rather than
        // creating-then-mutating; the in-memory store persists whatever SaveAsync is handed.
        var first = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-first"),
            ConversationId = conversationId,
            CreatedAt = Ts(0)
        });
        first.HydrateAgentId(AgentId.From("quill"));
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "in-first", Timestamp = Ts(1) });
        await sessions.SaveAsync(first);

        var second = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-second"),
            ConversationId = conversationId,
            CreatedAt = Ts(10)
        });
        second.HydrateAgentId(AgentId.From("quill"));
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
    public async Task AssembleAsync_SkipsGhostEmptyAssistantEntries_ButKeepsThinkingOnly()
    {
        // #2921 AC5: a contentless assistant row (no content, no thinking, no tool linkage) is a
        // ghost bubble and must not be replayed. A thinking-only row (#1198/#656) has something to
        // render and must survive - this test fails if the guard over-reaches and drops it.
        var conversationId = ConversationId.From("c_ghost");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "ping", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "real reply", Timestamp = Ts(1) });
        // The ghost: empty content, empty thinking, no tool linkage.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = string.Empty, Timestamp = Ts(2) });
        // Legitimate thinking-only entry - must be preserved.
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = string.Empty, ThinkingContent = "reasoning", Timestamp = Ts(3) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(3);
        result.Entries[0].Content.ShouldBe("ping");
        result.Entries[1].Content.ShouldBe("real reply");
        result.Entries[2].ThinkingContent.ShouldBe("reasoning");
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
    public async Task AssembleAsync_ReturnsFoldedHistoryEntries_FlaggedAsFolded()
    {
        // #2936 (AC1, AC3): compaction removes an entry from the LLM context window; it is not a
        // transcript deletion. Folded rows must be RETURNED (not filtered out of the candidate set)
        // and must carry an explicit flag so the client can render them collapsed.
        var conversationId = ConversationId.From("c_folded");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "pre-compaction", Timestamp = Ts(0), IsHistory = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "live", Timestamp = Ts(1) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(2);
        var folded = result.Entries.Single(e => e.Content == "pre-compaction");
        folded.IsFolded.ShouldBeTrue();
        folded.Kind.ShouldBe("message");
        result.Entries.Single(e => e.Content == "live").IsFolded.ShouldBeFalse();
    }

    [Fact]
    public async Task AssembleAsync_TotalCount_EqualsUnfilteredNonNoReplyTotal_WhenFolded()
    {
        // #2936 AC7: the regression guard. Before the fix the assembler dropped folded rows before
        // paging, so 96.6% of a real compacted transcript was not even a paging candidate. The
        // assembled count must now equal the unfiltered non-NO_REPLY, non-sentinel total.
        var conversationId = ConversationId.From("c_folded_total");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        for (var i = 0; i < 200; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"old-{i}", Timestamp = Ts(i), IsHistory = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "summary", Timestamp = Ts(200), IsCompactionSummary = true });
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"new-{i}", Timestamp = Ts(201 + i) });
        // Genuinely non-content rows that must STILL be dropped (AC4).
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "NO_REPLY", Timestamp = Ts(400) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "sentinel", Timestamp = Ts(401), IsCrashSentinel = true });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        // 200 folded + 1 summary + 10 live == 211. NO_REPLY and the crash sentinel stay excluded.
        result!.TotalCount.ShouldBe(211);
        result.Entries.ShouldNotContain(e => e.Content == "NO_REPLY");
        result.Entries.ShouldNotContain(e => e.Content == "sentinel");
    }

    [Fact]
    public async Task AssembleAsync_FoldedCompactionSummary_StillEmittedAsCompactionMarker()
    {
        // #2936 AC4: a superseded compaction summary is itself IsHistory = true. It must still be
        // projected with Kind = "compaction" (not as an ordinary message) and flagged folded.
        var conversationId = ConversationId.From("c_folded_summary");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "older summary", Timestamp = Ts(0), IsCompactionSummary = true, IsHistory = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "current summary", Timestamp = Ts(1), IsCompactionSummary = true });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.Entries.Count.ShouldBe(2);
        result.Entries.ShouldAllBe(e => e.Kind == "compaction" && e.Reason == "compaction");
        result.Entries.Single(e => e.Content == "older summary").IsFolded.ShouldBeTrue();
        result.Entries.Single(e => e.Content == "current summary").IsFolded.ShouldBeFalse();
    }

    [Fact]
    public async Task AssembleAsync_ExpandsContiguousFoldedRun_IntoASinglePage()
    {
        // #2936 AC6: paging cost. At a 20-row page a 2,736-row compacted transcript is ~137
        // sequential round trips. When a page lands inside a contiguous folded run the assembler
        // extends it backwards over the rest of the run so the client gets the collapsed block in
        // one response rather than walking it 20 rows at a time.
        var conversationId = ConversationId.From("c_folded_run");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        for (var i = 0; i < 300; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"old-{i}", Timestamp = Ts(i), IsHistory = true });
        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"new-{i}", Timestamp = Ts(300 + i) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        // First page: the 10 live rows plus the 10 newest folded rows -- the page boundary lands
        // inside the folded run, so the whole remaining run comes back with it.
        var page = await assembler.AssembleAsync(conversationId, limit: 20, offset: 0);

        page.ShouldNotBeNull();
        page!.TotalCount.ShouldBe(310);
        // The entire 300-row folded run is delivered in this one response instead of 15 more trips.
        page.Entries.Count.ShouldBe(310);
        page.Entries[0].Content.ShouldBe("old-0");
        page.Entries[^1].Content.ShouldBe("new-9");
        // One request reaches the start of the transcript -- far under the ~137 the bug implied.
        page.Entries.Count.ShouldBe(page.TotalCount);
    }

    [Fact]
    public async Task AssembleAsync_FoldedRunExpansion_IsBoundedByMaxFoldedPageEntries()
    {
        // #2936 AC6: the expansion must not let one request materialise an unbounded transcript.
        var conversationId = ConversationId.From("c_folded_bounded");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        const int foldedCount = 2000;
        for (var i = 0; i < foldedCount; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"old-{i}", Timestamp = Ts(i), IsHistory = true });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var page = await assembler.AssembleAsync(conversationId, limit: 20, offset: 0);

        page.ShouldNotBeNull();
        page!.TotalCount.ShouldBe(foldedCount);
        page.Entries.Count.ShouldBe(ConversationHistoryAssembler.MaxFoldedPageEntries);
        // Still anchored at the newest end, so the client's offset arithmetic is unchanged.
        page.Entries[^1].Content.ShouldBe($"old-{foldedCount - 1}");
        page.Entries[0].Content.ShouldBe($"old-{foldedCount - ConversationHistoryAssembler.MaxFoldedPageEntries}");
    }

    [Fact]
    public async Task AssembleAsync_SequentialPaging_ReachesStartOfCompactedTranscript()
    {
        // #2936 AC2 + AC6 end to end: walking backwards with offset += returned-count must reach
        // the very first row of a multi-thousand-row compacted transcript, and must do so in a
        // small number of requests rather than the ~137 a fixed 20-row page implies.
        var conversationId = ConversationId.From("c_walk");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        const int total = 2736;
        for (var i = 0; i < total - 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"row-{i}", Timestamp = Ts(i), IsHistory = true });
        for (var i = total - 10; i < total; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"row-{i}", Timestamp = Ts(i) });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var fetched = 0;
        var requests = 0;
        string? oldestSeen = null;
        while (true)
        {
            var page = await assembler.AssembleAsync(conversationId, limit: 20, offset: fetched);
            requests++;
            if (page!.Entries.Count == 0)
                break;

            oldestSeen = page.Entries[0].Content;
            fetched += page.Entries.Count;
            if (fetched >= page.TotalCount)
                break;

            requests.ShouldBeLessThan(50, "paging must not degenerate into a ~137-request walk");
        }

        fetched.ShouldBe(total);
        oldestSeen.ShouldBe("row-0");
        // 2,736 rows at a fixed 20-row page would be 137 requests; the folded-run expansion caps it.
        requests.ShouldBeLessThan(20);
    }

    [Fact]
    public async Task AssembleAsync_SkipsCrashSentinelEntries()
    {
        // #2936 AC4: crash sentinels are genuinely non-content recovery placeholders and stay out of
        // the transcript even though folded rows no longer do.
        var conversationId = ConversationId.From("c_sentinel");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "kept", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "sentinel", Timestamp = Ts(1), IsCrashSentinel = true });
        await sessions.SaveAsync(session);

        var assembler = await NewAssemblerAsync(conversationId, "quill", sessions);

        var result = await assembler.AssembleAsync(conversationId, limit: 50, offset: 0);

        result.ShouldNotBeNull();
        result!.TotalCount.ShouldBe(1);
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
