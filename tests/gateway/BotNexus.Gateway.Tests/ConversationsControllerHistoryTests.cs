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
    public async Task GetHistory_WithVirtualCronConversationId_UsesSessionHistory()
    {
        var sessionId = SessionId.From("cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54");
        var virtualConversationId = $"cron-session:{sessionId.Value}";
        var sessions = new InMemorySessionStore();
        var conversationStore = new InMemoryConversationStore();
        var session = await sessions.GetOrCreateAsync(sessionId, AgentId.From("assistant"));
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "cron-1",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "cron-2",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await sessions.SaveAsync(session);

        var controller = new ConversationsController(conversationStore, sessions);
        var actionResult = await controller.GetHistory(virtualConversationId, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.ConversationId.ShouldBe(virtualConversationId);
        response.TotalCount.ShouldBe(2);
        response.Entries.Count.ShouldBe(2);
        response.Entries[0].Kind.ShouldBe("message");
        response.Entries[0].SessionId.ShouldBe(sessionId.Value);
        response.Entries[0].Content.ShouldBe("cron-1");
        response.Entries[1].Content.ShouldBe("cron-2");
    }

    [Fact]
    public async Task GetHistory_WithVirtualCronConversationId_WhenSessionMissing_ReturnsEmpty()
    {
        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(new InMemoryConversationStore(), sessions);
        var virtualConversationId = "cron-session:cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54";

        var actionResult = await controller.GetHistory(virtualConversationId, limit: 200, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.ConversationId.ShouldBe(virtualConversationId);
        response.TotalCount.ShouldBe(0);
        response.Entries.ShouldBeEmpty();
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
    public async Task Archive_WithVirtualCronConversationId_SealsRequestedSession_AndDistinctActiveSession()
    {
        var conversationId = ConversationId.From("c_cron_cleanup_seals_requested_and_active");
        var requestedSessionId = SessionId.From("cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54");
        var activeSessionId = SessionId.From("cron:20260510001608:c7fe67628e3142a1894974d22bb998a8");
        var virtualConversationId = $"cron-session:{requestedSessionId.Value}";
        var sessions = new InMemorySessionStore();

        var requestedSession = await sessions.GetOrCreateAsync(requestedSessionId, AgentId.From("assistant"));
        requestedSession.Session.ConversationId = conversationId;
        requestedSession.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "session-a-history",
            Timestamp = DateTimeOffset.UtcNow
        });
        await sessions.SaveAsync(requestedSession);

        var activeSession = await sessions.GetOrCreateAsync(activeSessionId, AgentId.From("assistant"));
        activeSession.Session.ConversationId = conversationId;
        activeSession.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "session-b-history",
            Timestamp = DateTimeOffset.UtcNow
        });
        await sessions.SaveAsync(activeSession);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.CreateAsync(CreateConversation(conversationId, "assistant", activeSessionId));
        var controller = new ConversationsController(conversationStore, sessions);

        var archiveResult = await controller.Archive(virtualConversationId, CancellationToken.None);

        archiveResult.ShouldBeOfType<NoContentResult>();

        var sealedRequestedSession = await sessions.GetAsync(requestedSessionId);
        sealedRequestedSession.ShouldNotBeNull();
        sealedRequestedSession!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        sealedRequestedSession.History.ShouldContain(entry => entry.Content == "session-a-history");

        var sealedActiveSession = await sessions.GetAsync(activeSessionId);
        sealedActiveSession.ShouldNotBeNull();
        sealedActiveSession!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        sealedActiveSession.History.ShouldContain(entry => entry.Content == "session-b-history");

        var archivedConversation = await conversationStore.GetAsync(conversationId);
        archivedConversation.ShouldNotBeNull();
        archivedConversation!.Status.ShouldBe(ConversationStatus.Archived);
        archivedConversation.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Archive_WithVirtualCronConversationId_WithoutLinkedConversation_SealsSession_AndReturnsNoContent()
    {
        var orphanSessionId = SessionId.From("cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54");
        var virtualConversationId = $"cron-session:{orphanSessionId.Value}";
        var sessions = new InMemorySessionStore();
        var orphanSession = await sessions.GetOrCreateAsync(orphanSessionId, AgentId.From("assistant"));
        orphanSession.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "keep-history",
            Timestamp = DateTimeOffset.UtcNow
        });
        await sessions.SaveAsync(orphanSession);

        var controller = new ConversationsController(new InMemoryConversationStore(), sessions);

        var archiveResult = await controller.Archive(virtualConversationId, CancellationToken.None);

        archiveResult.ShouldBeOfType<NoContentResult>();
        var sealedSession = await sessions.GetAsync(orphanSessionId);
        sealedSession.ShouldNotBeNull();
        sealedSession!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        sealedSession.History.ShouldContain(entry => entry.Content == "keep-history");
    }

    [Fact]
    public async Task Archive_SealsAllSessionsLinkedToConversation()
    {
        var conversationId = ConversationId.From("c_archive_seals_all_linked_sessions");
        var sessions = new InMemorySessionStore();

        var firstLinked = await sessions.GetOrCreateAsync(SessionId.From("s-linked-1"), AgentId.From("assistant"));
        firstLinked.Session.ConversationId = conversationId;
        firstLinked.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Active;
        await sessions.SaveAsync(firstLinked);

        var secondLinked = await sessions.GetOrCreateAsync(SessionId.From("s-linked-2"), AgentId.From("assistant"));
        secondLinked.Session.ConversationId = conversationId;
        secondLinked.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Suspended;
        await sessions.SaveAsync(secondLinked);

        var unrelated = await sessions.GetOrCreateAsync(SessionId.From("s-unrelated"), AgentId.From("assistant"));
        unrelated.Session.ConversationId = ConversationId.From("c_other");
        unrelated.Status = BotNexus.Gateway.Abstractions.Models.SessionStatus.Active;
        await sessions.SaveAsync(unrelated);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.CreateAsync(CreateConversation(conversationId, "assistant", activeSessionId: SessionId.From("s-linked-1")));
        var controller = new ConversationsController(conversationStore, sessions);

        var result = await controller.Archive(conversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await sessions.GetAsync(SessionId.From("s-linked-1")))!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        (await sessions.GetAsync(SessionId.From("s-linked-2")))!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);
        (await sessions.GetAsync(SessionId.From("s-unrelated")))!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Active);
    }

    [Fact]
    public async Task Archive_WithVirtualCronConversationId_WhenSessionMissing_ReturnsNoContent()
    {
        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(new InMemoryConversationStore(), sessions);

        var result = await controller.Archive(
            "cron-session:cron:20260510001608:c7fe67628e3142a1894974d22bb998a8",
            CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Archive_WithVirtualCronConversationId_KeepsConversationArchivedAcrossListReload()
    {
        var conversationId = ConversationId.From("c_cron_archived_stays_hidden");
        var requestedSessionId = SessionId.From("cron:20260509002033:6f2f84a4f1634ff492a4fec212872c54");
        var sessions = new InMemorySessionStore();

        var requestedSession = await sessions.GetOrCreateAsync(requestedSessionId, AgentId.From("assistant"));
        requestedSession.Session.ConversationId = conversationId;
        requestedSession.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "cron-history",
            Timestamp = DateTimeOffset.UtcNow
        });
        await sessions.SaveAsync(requestedSession);

        var conversationStore = new InMemoryConversationStore();
        await conversationStore.CreateAsync(CreateConversation(conversationId, "assistant", requestedSessionId));
        var controller = new ConversationsController(conversationStore, sessions);

        var archiveResult = await controller.Archive($"cron-session:{requestedSessionId.Value}", CancellationToken.None);

        archiveResult.ShouldBeOfType<NoContentResult>();

        var sealedRequestedSession = await sessions.GetAsync(requestedSessionId);
        sealedRequestedSession.ShouldNotBeNull();
        sealedRequestedSession!.Status.ShouldBe(BotNexus.Gateway.Abstractions.Models.SessionStatus.Sealed);

        var listResult = await controller.List("assistant", CancellationToken.None);
        var list = (listResult as OkObjectResult)?.Value as IReadOnlyList<ConversationSummary>;
        list.ShouldNotBeNull();
        list!.ShouldNotContain(summary => summary.ConversationId == conversationId);

        var sessionsController = new SessionsController(sessions);
        var defaultSessionsResult = await sessionsController.List("assistant", cancellationToken: CancellationToken.None);
        var defaultSessionIds = ExtractSessionIds(defaultSessionsResult.ShouldBeOfType<OkObjectResult>());
        defaultSessionIds.ShouldNotContain(requestedSessionId.Value);

        var includeInactiveResult = await sessionsController.List("assistant", includeInactive: true, cancellationToken: CancellationToken.None);
        var includeInactiveSessionIds = ExtractSessionIds(includeInactiveResult.ShouldBeOfType<OkObjectResult>());
        includeInactiveSessionIds.ShouldContain(requestedSessionId.Value);
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

        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, ThreadId? threadId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(BotNexus.Domain.World.CitizenId citizen, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
