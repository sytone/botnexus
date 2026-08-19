using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Unit tests for <see cref="ExportDocumentAssembler"/> (issue #3278). These cover the summary
/// header contract (AC1), the session-scope parent-conversation attachment (AC4), the control-marker
/// exclusions (AC6) and the four conversation shapes named by AC8: empty, archived, multi-session
/// and compacted.
/// </summary>
public sealed class ExportDocumentAssemblerTests
{
    [Fact]
    public async Task AssembleConversationAsync_UnknownConversation_ReturnsNull()
    {
        var assembler = new ExportDocumentAssembler(new InMemoryConversationStore(), new InMemorySessionStore());

        var document = await assembler.AssembleConversationAsync(ConversationId.From("c_missing"));

        document.ShouldBeNull();
    }

    [Fact]
    public async Task AssembleConversationAsync_PopulatesEveryHeaderFieldRequiredByAc1()
    {
        var conversationId = ConversationId.From("c_header");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Tool,
            ToolName = "read",
            ToolArgs = "{\"path\":\"a.txt\"}",
            ToolCallId = "tc-1",
            Content = string.Empty,
            Timestamp = Ts(1)
        });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("quill"),
            Title = "Quarterly planning",
            Purpose = "Plan the quarter",
            Status = ConversationStatus.Active,
            Instructions = "Be concise.",
            ModelOverride = "claude-opus-5",
            ThinkingOverride = "high",
            ContextWindowOverride = 128000,
            CreatedAt = Ts(0),
            UpdatedAt = Ts(5)
        });

        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var document = await assembler.AssembleConversationAsync(conversationId);

        document.ShouldNotBeNull();
        document!.Scope.ShouldBe(ExportScope.Conversation);
        document.ConversationId.ShouldBe("c_header");
        document.Title.ShouldBe("Quarterly planning");
        document.Purpose.ShouldBe("Plan the quarter");
        document.Status.ShouldBe("Active");
        document.CreatedAt.ShouldBe(Ts(0));
        document.UpdatedAt.ShouldBe(Ts(5));
        document.AgentId.ShouldBe("quill");
        document.Instructions.ShouldBe("Be concise.");
        document.ModelOverride.ShouldBe("claude-opus-5");
        document.ThinkingOverride.ShouldBe("high");
        document.ContextWindowOverride.ShouldBe(128000);
        document.Sessions.Count.ShouldBe(1);
        document.Sessions[0].SessionId.ShouldBe("s-1");
        document.Sessions[0].AgentId.ShouldBe("quill");
        document.Sessions[0].MessageCount.ShouldBe(2);
        document.MessageCount.ShouldBe(2);
        document.ToolCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task AssembleConversationAsync_EmptyConversation_ReturnsDocumentWithHeaderAndNoEntries()
    {
        // AC8: an empty conversation is a defined behaviour, not a 404 and not a crash. The user
        // gets a valid document describing the conversation with an explicitly empty transcript.
        var conversationId = ConversationId.From("c_empty");
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Nothing here yet"));

        var assembler = new ExportDocumentAssembler(conversations, new InMemorySessionStore());

        var document = await assembler.AssembleConversationAsync(conversationId);

        document.ShouldNotBeNull();
        document!.Title.ShouldBe("Nothing here yet");
        document.Entries.ShouldBeEmpty();
        document.Sessions.ShouldBeEmpty();
        document.MessageCount.ShouldBe(0);
        document.ToolCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task AssembleConversationAsync_ArchivedConversation_ExportsWithArchivedStatus()
    {
        // AC8: archiving is a soft delete. The transcript must remain exportable - that is arguably
        // the moment a user most wants a copy - and the header must state the archived status
        // rather than silently presenting it as an active conversation.
        var conversationId = ConversationId.From("c_archived");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-arch"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "before archive", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        var conversation = NewConversation(conversationId, "Closed thread");
        conversation.Status = ConversationStatus.Archived;
        await conversations.CreateAsync(conversation);

        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var document = await assembler.AssembleConversationAsync(conversationId);

        document.ShouldNotBeNull();
        document!.Status.ShouldBe("Archived");
        document.Entries.Count.ShouldBe(1);
        document.Entries[0].Content.ShouldBe("before archive");
    }

    [Fact]
    public async Task AssembleConversationAsync_MultiSession_EmitsBoundaryMarkerAndPerSessionMetadata()
    {
        // AC8 (multi-session) + AC2 (visible session boundary markers).
        var conversationId = ConversationId.From("c_multi");
        var sessions = new InMemorySessionStore();

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
        second.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "reply", Timestamp = Ts(12) });
        await sessions.SaveAsync(second);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Two sessions"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var document = await assembler.AssembleConversationAsync(conversationId);

        document.ShouldNotBeNull();
        document!.Sessions.Count.ShouldBe(2);
        document.Sessions[0].SessionId.ShouldBe("s-first");
        document.Sessions[0].MessageCount.ShouldBe(1);
        document.Sessions[1].SessionId.ShouldBe("s-second");
        document.Sessions[1].MessageCount.ShouldBe(2);

        document.Entries.Count(e => e.Kind == "boundary").ShouldBe(1);
        // Boundary markers are structural, so they must NOT inflate the message total.
        document.MessageCount.ShouldBe(3);
    }

    [Fact]
    public async Task AssembleConversationAsync_CompactedConversation_KeepsFoldedHistoryAndCompactionMarker()
    {
        // AC8 (compacted): compaction removes an entry from the LLM context window, it does not
        // delete it. An export is a fidelity artifact, so the folded pre-compaction turns must all
        // be present and the summary must be projected as its own marker, not as a plain message.
        var conversationId = ConversationId.From("c_compacted");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-c"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        for (var i = 0; i < 5; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"old-{i}", Timestamp = Ts(i), IsHistory = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "summary of the above", Timestamp = Ts(10), IsCompactionSummary = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "after compaction", Timestamp = Ts(11) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Long thread"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var document = await assembler.AssembleConversationAsync(conversationId);

        document.ShouldNotBeNull();
        // 5 folded + 1 compaction marker + 1 live entry.
        document!.Entries.Count.ShouldBe(7);
        document.Entries.Count(e => e.IsFolded).ShouldBe(5);
        var compaction = document.Entries.Single(e => e.Kind == "compaction");
        compaction.Content.ShouldBe("summary of the above");
        document.Entries.ShouldContain(e => e.Content == "after compaction");
    }

    [Fact]
    public async Task AssembleConversationAsync_ExcludesSentinelsNoReplyAndGhostRows()
    {
        // AC6: seed each excluded shape explicitly so the assertion fails if any one of the three
        // filters is lost. Asserting only "the good message is present" would pass over a
        // regression that also leaked the sentinel.
        var conversationId = ConversationId.From("c_markers");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-m"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "real question", Timestamp = Ts(0) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "NO_REPLY", Timestamp = Ts(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "  NO_REPLY  ", Timestamp = Ts(2) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "crash placeholder", Timestamp = Ts(3), IsCrashSentinel = true });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "", Timestamp = Ts(4) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "real answer", Timestamp = Ts(5) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Markers"));

        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var document = await assembler.AssembleConversationAsync(conversationId);

        document.ShouldNotBeNull();
        document!.Entries.Count.ShouldBe(2);
        document.Entries.ShouldNotContain(e => e.Content!.Contains("NO_REPLY", StringComparison.Ordinal));
        document.Entries.ShouldNotContain(e => e.Content == "crash placeholder");
        document.Entries.ShouldNotContain(e => string.IsNullOrWhiteSpace(e.Content));
        document.Entries[0].Content.ShouldBe("real question");
        document.Entries[1].Content.ShouldBe("real answer");
    }

    [Fact]
    public async Task AssembleSessionAsync_LinkedSession_IncludesParentConversationSummary()
    {
        // AC4.
        var conversationId = ConversationId.From("c_parent");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-linked"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hi", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var conversations = new InMemoryConversationStore();
        var conversation = NewConversation(conversationId, "Parent thread");
        conversation.Purpose = "the parent purpose";
        conversation.Instructions = "parent instructions";
        await conversations.CreateAsync(conversation);

        var assembler = new ExportDocumentAssembler(conversations, sessions);

        var document = await assembler.AssembleSessionAsync(SessionId.From("s-linked"));

        document.ShouldNotBeNull();
        document!.Scope.ShouldBe(ExportScope.Session);
        document.ConversationId.ShouldBe("c_parent");
        document.Title.ShouldBe("Parent thread");
        document.Purpose.ShouldBe("the parent purpose");
        document.Instructions.ShouldBe("parent instructions");
        document.Sessions.Count.ShouldBe(1);
        document.Sessions[0].SessionId.ShouldBe("s-linked");
    }

    [Fact]
    public async Task AssembleSessionAsync_OrphanSession_ExportsWithoutConversationSummary()
    {
        // Sad path for AC4: a session with no conversation link (#732 shape) must still export,
        // with the conversation-derived header fields simply absent rather than the call failing.
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-orphan"), AgentId.From("quill"));
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "orphan turn", Timestamp = Ts(0) });
        await sessions.SaveAsync(session);

        var assembler = new ExportDocumentAssembler(new InMemoryConversationStore(), sessions);

        var document = await assembler.AssembleSessionAsync(SessionId.From("s-orphan"));

        document.ShouldNotBeNull();
        document!.Title.ShouldBeNull();
        document.Purpose.ShouldBeNull();
        document.Status.ShouldBeNull();
        document.AgentId.ShouldBe("quill");
        document.Entries.Count.ShouldBe(1);
        document.Entries[0].Content.ShouldBe("orphan turn");
    }

    [Fact]
    public async Task AssembleSessionAsync_UnknownSession_ReturnsNull()
    {
        var assembler = new ExportDocumentAssembler(new InMemoryConversationStore(), new InMemorySessionStore());

        var document = await assembler.AssembleSessionAsync(SessionId.From("s-missing"));

        document.ShouldBeNull();
    }

    [Fact]
    public async Task AssembleConversationAsync_MatchesConversationHistoryAssemblerOutput()
    {
        // AC9, the anti-drift clause, asserted behaviourally: for the same conversation the export
        // document's entries must be exactly what the portal history endpoint assembles. This is
        // what makes "there is one transcript assembly path" a checked property rather than a
        // comment - reintroducing a second interpretation in either consumer reddens this test.
        var conversationId = ConversationId.From("c_parity");
        var sessions = new InMemorySessionStore();

        var first = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-p1"),
            ConversationId = conversationId,
            CreatedAt = Ts(0)
        });
        first.HydrateAgentId(AgentId.From("quill"));
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "q1", Timestamp = Ts(1) });
        first.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "NO_REPLY", Timestamp = Ts(2) });
        first.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "sum", Timestamp = Ts(3), IsCompactionSummary = true });
        await sessions.SaveAsync(first);

        var second = new GatewaySession(new Session
        {
            SessionId = SessionId.From("s-p2"),
            ConversationId = conversationId,
            CreatedAt = Ts(10)
        });
        second.HydrateAgentId(AgentId.From("quill"));
        second.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "q2", Timestamp = Ts(11) });
        await sessions.SaveAsync(second);

        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(NewConversation(conversationId, "Parity"));

        var exportDocument = await new ExportDocumentAssembler(conversations, sessions)
            .AssembleConversationAsync(conversationId);
        var history = await new ConversationHistoryAssembler(conversations, sessions)
            .AssembleAsync(conversationId, limit: 1000, offset: 0);

        exportDocument.ShouldNotBeNull();
        history.ShouldNotBeNull();
        exportDocument!.Entries.Count.ShouldBe(history!.TotalCount);
        exportDocument.Entries
            .Select(e => (e.Kind, e.SessionId, e.Role, e.Content, e.Timestamp))
            .ShouldBe(history.Entries.Select(e => (e.Kind, e.SessionId, e.Role, e.Content, e.Timestamp)));
    }

    private static DateTimeOffset Ts(int minutes)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static Conversation NewConversation(ConversationId conversationId, string title)
        => new()
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("quill"),
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = Ts(0),
            UpdatedAt = Ts(1)
        };
}
