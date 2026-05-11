using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
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
        var session = await sessions.GetOrCreateAsync("s-history-1", "quill");
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
        var session = await sessions.GetOrCreateAsync("s-history-2", "quill");
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

    private static Conversation CreateConversation(ConversationId conversationId, string agentId)
        => new()
        {
            ConversationId = conversationId,
            AgentId = AgentId.From(agentId),
            Title = "Default",
            IsDefault = true,
            Status = ConversationStatus.Active,
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
    }
}
