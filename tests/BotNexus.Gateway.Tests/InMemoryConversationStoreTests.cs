using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryConversationStore"/>.
/// </summary>
public sealed class InMemoryConversationStoreTests
{
    private static AgentId Agent(string id = "agent1") => AgentId.From(id);
    private static ConversationId NewId() => ConversationId.Create();

    private static Conversation MakeConversation(AgentId agentId, string? title = null) =>
        new()
        {
            ConversationId = NewId(),
            AgentId = agentId,
            Title = title ?? "Test conversation"
        };

    [Fact]
    public async Task CreateAsync_PersistsConversation()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        var conv = MakeConversation(agentId);

        var result = await store.CreateAsync(conv);

        result.ShouldBe(conv);
        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe(conv.Title);
    }

    [Fact]
    public async Task CreateAsync_ThrowsIfDuplicateId()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Agent());
        await store.CreateAsync(conv);

        await Should.ThrowAsync<InvalidOperationException>(() => store.CreateAsync(conv));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownId()
    {
        var store = new InMemoryConversationStore();
        var result = await store.GetAsync(ConversationId.Create());
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllConversations()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        await store.CreateAsync(MakeConversation(agentId, "A"));
        await store.CreateAsync(MakeConversation(agentId, "B"));

        var list = await store.ListAsync();
        list.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListAsync_FiltersByAgentId()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync(MakeConversation(Agent("agent1"), "A"));
        await store.CreateAsync(MakeConversation(Agent("agent2"), "B"));

        var list = await store.ListAsync(Agent("agent1"));
        list.Count.ShouldBe(1);
        list[0].Title.ShouldBe("A");
    }

    [Fact]
    public async Task GetOrCreateDefaultAsync_CreatesDefaultOnFirstCall()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();

        var conv = await store.GetOrCreateDefaultAsync(agentId);

        conv.IsDefault.ShouldBeTrue();
        conv.Title.ShouldBe("Default");
        conv.Status.ShouldBe(ConversationStatus.Active);
    }

    [Fact]
    public async Task GetOrCreateDefaultAsync_IsIdempotent()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();

        var first = await store.GetOrCreateDefaultAsync(agentId);
        var second = await store.GetOrCreateDefaultAsync(agentId);

        first.ConversationId.ShouldBe(second.ConversationId);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingConversation()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Agent());
        await store.CreateAsync(conv);

        var updated = conv with { Title = "Updated Title" };
        await store.SaveAsync(updated);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.Title.ShouldBe("Updated Title");
    }

    [Fact]
    public async Task ArchiveAsync_SetsStatusToArchived()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Agent());
        await store.CreateAsync(conv);

        await store.ArchiveAsync(conv.ConversationId);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task ResolveByBindingAsync_MatchesOnChannelAddress()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        var conv = MakeConversation(agentId) with
        {
            ChannelBindings =
            [
                new ChannelBinding
                {
                    ChannelType = ChannelKey.From("telegram"),
                    ChannelAddress = ChannelAddress.From("12345")
                }
            ]
        };
        await store.CreateAsync(conv);

        var result = await store.ResolveByBindingAsync(agentId, ChannelKey.From("telegram"), ChannelAddress.From("12345"), null);

        result.ShouldNotBeNull();
        result!.ConversationId.ShouldBe(conv.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_MatchesOnThreadId()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        var conv = MakeConversation(agentId) with
        {
            ChannelBindings =
            [
                new ChannelBinding
                {
                    ChannelType = ChannelKey.From("teams"),
                    ChannelAddress = ChannelAddress.From("team-channel"),
                    ThreadId = ThreadId.From("thread-42")
                }
            ]
        };
        await store.CreateAsync(conv);

        var match = await store.ResolveByBindingAsync(agentId, ChannelKey.From("teams"), ChannelAddress.From("team-channel"), ThreadId.From("thread-42"));
        var noMatch = await store.ResolveByBindingAsync(agentId, ChannelKey.From("teams"), ChannelAddress.From("team-channel"), ThreadId.From("wrong-thread"));

        match.ShouldNotBeNull();
        noMatch.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveByBindingAsync_ReturnsNullForArchivedConversation()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        var conv = MakeConversation(agentId) with
        {
            ChannelBindings =
            [
                new ChannelBinding
                {
                    ChannelType = ChannelKey.From("telegram"),
                    ChannelAddress = ChannelAddress.From("99999")
                }
            ]
        };
        await store.CreateAsync(conv);
        await store.ArchiveAsync(conv.ConversationId);

        var result = await store.ResolveByBindingAsync(agentId, ChannelKey.From("telegram"), ChannelAddress.From("99999"), null);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetSummariesAsync_ReturnsSummariesForAgent()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent("summary-agent");
        var conv = MakeConversation(agentId) with
        {
            ChannelBindings = [new ChannelBinding { ChannelType = ChannelKey.From("telegram"), ChannelAddress = ChannelAddress.From("1") }]
        };
        await store.CreateAsync(conv);

        var summaries = await store.GetSummariesAsync(agentId);
        summaries.Count.ShouldBe(1);
        summaries[0].BindingCount.ShouldBe(1);
        summaries[0].AgentId.ShouldBe("summary-agent");
    }
}
