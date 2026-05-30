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

        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(BotNexus.Domain.World.CitizenId citizen, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
