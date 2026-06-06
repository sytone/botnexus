using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationsControllerHistoryTests
{
    [Fact]
    public async Task GetHistory_WithLargeConversation_ReturnsLatestPageByDefault()
    {
        var conversationId = ConversationId.From("c_history_latest_page");
        var sessions = new InMemorySessionStore();
        var conversationStore = new StubConversationStore(CreateConversation(conversationId, "quill"));
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-history-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        for (var i = 0; i < 250; i++)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = $"m-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }

        await sessions.SaveAsync(session);
        var controller = new ConversationsController(conversationStore, sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(250);
        response.Entries.Count.ShouldBe(200);
        response.Entries[0].Content.ShouldBe("m-50");
        response.Entries[^1].Content.ShouldBe("m-249");
    }

    [Fact]
    public async Task GetHistory_WithOffset_PagesBackwardFromMostRecentEntries()
    {
        var conversationId = ConversationId.From("c_history_offset_page");
        var sessions = new InMemorySessionStore();
        var conversationStore = new StubConversationStore(CreateConversation(conversationId, "quill"));
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-history-2"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        for (var i = 0; i < 250; i++)
        {
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.User,
                Content = $"m-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }

        await sessions.SaveAsync(session);
        var controller = new ConversationsController(conversationStore, sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 50, offset: 25, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(250);
        response.Entries.Count.ShouldBe(50);
        response.Entries[0].Content.ShouldBe("m-175");
        response.Entries[^1].Content.ShouldBe("m-224");
    }

    [Fact]
    public async Task GetHistory_WithMissingRealConversation_ReturnsNotFound()
    {
        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(new InMemoryConversationStore(), sessions);

        var actionResult = await controller.GetHistory("c_missing_history", limit: 200, offset: 0, CancellationToken.None);

        actionResult.ShouldBeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Regression test for #732: a cron session whose conversation_id column is NULL
    /// in the sessions table is invisible to ListByConversationAsync. The history endpoint
    /// must fall back to conversation.ActiveSessionId so the user can see messages that
    /// were written before the conversation-linkage migration (or by cron paths that do not
    /// yet stamp the foreign key).
    /// </summary>
    [Fact]
    public async Task GetHistory_OrphanedSession_FallsBackToActiveSessionId_WhenLinkedSessionsEmpty()
    {
        var conversationId = ConversationId.From("c_history_orphan");
        var orphanSessionId = SessionId.From("s-orphan-no-conv-id");
        var sessions = new InMemorySessionStore();

        // Session has NO conversation_id stamp — simulates the pre-migration / cron-path gap.
        var session = await sessions.GetOrCreateAsync(orphanSessionId, AgentId.From("aurum"));
        // Deliberately do NOT set session.Session.ConversationId — leaves it as default (uninitialized)
        for (var i = 0; i < 5; i++)
        {
            session.AddEntry(new SessionEntry
            {
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = $"orphan-msg-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }
        await sessions.SaveAsync(session);

        // Conversation points to the orphaned session via ActiveSessionId
        var conversation = CreateConversation(conversationId, "aurum", orphanSessionId);
        var controller = new ConversationsController(new StubConversationStore(conversation), sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(5);
        response.Entries.Count.ShouldBe(5);
        response.Entries[0].Content.ShouldBe("orphan-msg-0");
        response.Entries[^1].Content.ShouldBe("orphan-msg-4");
    }

    [Fact]
    public async Task GetHistory_OrphanedSession_ReturnsEmpty_WhenNoActiveSessionId()
    {
        // Conversation has no sessions AND no ActiveSessionId — must return 0 entries, not 404.
        var conversationId = ConversationId.From("c_history_orphan_no_fallback");
        var sessions = new InMemorySessionStore();
        var conversation = CreateConversation(conversationId, "aurum"); // no activeSessionId
        var controller = new ConversationsController(new StubConversationStore(conversation), sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(0);
        response.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetHistory_OrphanedSession_DoesNotDuplicateEntries_WhenSessionAlsoLinked()
    {
        // Edge case: session has both a conversation_id stamp AND appears as ActiveSessionId.
        // Entries must not be doubled.
        var conversationId = ConversationId.From("c_history_no_dup");
        var sessionId = SessionId.From("s-linked-and-active");
        var sessions = new InMemorySessionStore();

        var session = await sessions.GetOrCreateAsync(sessionId, AgentId.From("quill"));
        session.Session.ConversationId = conversationId; // properly linked
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "msg-1", Timestamp = DateTimeOffset.UtcNow });
        await sessions.SaveAsync(session);

        var conversation = CreateConversation(conversationId, "quill", sessionId);
        var controller = new ConversationsController(new StubConversationStore(conversation), sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(1); // must not double-count
        response.Entries[0].Content.ShouldBe("msg-1");
    }

    [Fact]
    public async Task Archive_SealsActiveSessionWithoutDeletingIt()
    {
        var conversationId = ConversationId.From("c_archive_preserve_session");
        var sessionId = SessionId.From("s-archive-preserve");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(sessionId, AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "keep-me",
            Timestamp = DateTimeOffset.UtcNow
        });
        await sessions.SaveAsync(session);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.CreateAsync(CreateConversation(conversationId, "quill", sessionId));
        var controller = new ConversationsController(conversationStore, sessions);

        var actionResult = await controller.Archive(conversationId.Value, CancellationToken.None);

        actionResult.ShouldBeOfType<NoContentResult>();
        var archivedSession = await sessions.GetAsync(sessionId);
        archivedSession.ShouldNotBeNull();
        archivedSession!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        archivedSession.History.ShouldContain(entry => entry.Content == "keep-me");
        var archivedConversation = await conversationStore.GetAsync(conversationId);
        archivedConversation.ShouldNotBeNull();
        archivedConversation!.Status.ShouldBe(ConversationStatus.Archived);
        archivedConversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Archive_SealsOnlyActiveSession_LeavesOtherSessionsUntouched()
    {
        // Phase 3c contract: Archive trusts Conversation.ActiveSessionId and only seals that
        // session via the reset service. Older sessions in a conversation are already sealed
        // by the normal lifecycle; unrelated sessions belong to other conversations and must
        // not be touched. The pre-3c "walk every session matching ConversationId and seal"
        // pass was a workaround for not trusting ActiveSessionId and is intentionally removed.
        var conversationId = ConversationId.From("c_archive_seals_active_only");
        var sessions = new InMemorySessionStore();

        var activeSession = await sessions.GetOrCreateAsync(SessionId.From("s-active"), AgentId.From("assistant"));
        activeSession.Session.ConversationId = conversationId;
        activeSession.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Active;
        await sessions.SaveAsync(activeSession);

        var previouslySealed = await sessions.GetOrCreateAsync(SessionId.From("s-old-sealed"), AgentId.From("assistant"));
        previouslySealed.Session.ConversationId = conversationId;
        previouslySealed.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed;
        await sessions.SaveAsync(previouslySealed);

        var unrelated = await sessions.GetOrCreateAsync(SessionId.From("s-unrelated"), AgentId.From("assistant"));
        unrelated.Session.ConversationId = ConversationId.From("c_other");
        unrelated.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Active;
        await sessions.SaveAsync(unrelated);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.CreateAsync(CreateConversation(conversationId, "assistant", activeSessionId: SessionId.From("s-active")));
        var controller = new ConversationsController(conversationStore, sessions);

        var result = await controller.Archive(conversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await sessions.GetAsync(SessionId.From("s-active")))!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        (await sessions.GetAsync(SessionId.From("s-old-sealed")))!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        (await sessions.GetAsync(SessionId.From("s-unrelated")))!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Active);

        // ActiveSessionId is cleared after archive (next inbound would create a fresh session
        // if the conversation were re-opened — but archived conversations are hidden from listing).
        var archivedConversation = await conversationStore.GetAsync(conversationId);
        archivedConversation.ShouldNotBeNull();
        archivedConversation!.ActiveSessionId.ShouldBeNull();
        archivedConversation.Status.ShouldBe(ConversationStatus.Archived);
    }

    private static IReadOnlyList<string> ExtractSessionIds(OkObjectResult result)
        => result.Value.ShouldNotBeNull()
            .ShouldBeAssignableTo<IEnumerable<object>>()
            .Select(item => item.GetType().GetProperty("sessionId")?.GetValue(item)?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();

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

    private sealed class StubConversationStore : IConversationStore
    {
        private readonly Conversation _conversation;

        public StubConversationStore(Conversation conversation)
        {
            _conversation = conversation;
        }

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(_conversation.ConversationId == conversationId ? _conversation : null);

        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(BotNexus.Domain.World.CitizenId citizen, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Regression tests for #773: NO_REPLY assistant entries must be stripped from conversation history
    /// so cron wakeups that had nothing to say don't appear as blank turns in the portal.
    /// </summary>
    [Fact]
    public async Task GetHistory_FiltersOutNoReplyAssistantEntries()
    {
        var conversationId = ConversationId.From("c_noreply_filter");
        var sessions = new InMemorySessionStore();
        var conversationStore = new StubConversationStore(CreateConversation(conversationId, "quill"));
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-noreply-1"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        // Interleave real messages with NO_REPLY turns
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "real question", Timestamp = DateTimeOffset.UtcNow.AddMinutes(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "real answer", Timestamp = DateTimeOffset.UtcNow.AddMinutes(2) });
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "another question", Timestamp = DateTimeOffset.UtcNow.AddMinutes(3) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "NO_REPLY", Timestamp = DateTimeOffset.UtcNow.AddMinutes(4) }); // cron NO_REPLY -- must be filtered
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "yet another", Timestamp = DateTimeOffset.UtcNow.AddMinutes(5) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "second real answer", Timestamp = DateTimeOffset.UtcNow.AddMinutes(6) });

        await sessions.SaveAsync(session);
        var controller = new ConversationsController(conversationStore, sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        // 5 visible entries (1 NO_REPLY filtered out)
        response!.TotalCount.ShouldBe(5);
        response.Entries.ShouldNotContain(e => e.Content == "NO_REPLY");
        response.Entries.ShouldContain(e => e.Content == "real answer");
        response.Entries.ShouldContain(e => e.Content == "second real answer");
    }

    [Fact]
    public async Task GetHistory_FiltersNoReplyWithWhitespace()
    {
        // NO_REPLY with surrounding whitespace/newlines should also be filtered (matches client-side Trim() behaviour)
        var conversationId = ConversationId.From("c_noreply_whitespace");
        var sessions = new InMemorySessionStore();
        var conversationStore = new StubConversationStore(CreateConversation(conversationId, "quill"));
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-noreply-2"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello", Timestamp = DateTimeOffset.UtcNow.AddMinutes(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "  NO_REPLY  ", Timestamp = DateTimeOffset.UtcNow.AddMinutes(2) }); // padded
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "NO_REPLY\n", Timestamp = DateTimeOffset.UtcNow.AddMinutes(3) }); // trailing newline

        await sessions.SaveAsync(session);
        var controller = new ConversationsController(conversationStore, sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(1); // only the user message
        response.Entries[0].Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GetHistory_DoesNotFilterUserEntriesNamedNoReply()
    {
        // A user message literally saying "NO_REPLY" should NOT be filtered (only assistant entries)
        var conversationId = ConversationId.From("c_noreply_user_safe");
        var sessions = new InMemorySessionStore();
        var conversationStore = new StubConversationStore(CreateConversation(conversationId, "quill"));
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-noreply-3"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "NO_REPLY", Timestamp = DateTimeOffset.UtcNow.AddMinutes(1) });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "I understand you typed NO_REPLY", Timestamp = DateTimeOffset.UtcNow.AddMinutes(2) });

        await sessions.SaveAsync(session);
        var controller = new ConversationsController(conversationStore, sessions);

        var actionResult = await controller.GetHistory(conversationId.Value, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(2); // both entries preserved
        response.Entries.ShouldContain(e => e.Content == "NO_REPLY" && e.Role == "user");
        response.Entries.ShouldContain(e => e.Content == "I understand you typed NO_REPLY");
    }
}
