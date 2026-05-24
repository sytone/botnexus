using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;

namespace BotNexus.Gateway.Conversations.Tests;

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
    public async Task ArchiveAsync_ClearsActiveSessionId()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Agent()) with
        {
            ActiveSessionId = SessionId.Create()
        };
        await store.CreateAsync(conv);

        await store.ArchiveAsync(conv.ConversationId);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.ActiveSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task ListAndSave_ReactivatesArchivedConversation()
    {
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        var existing = MakeConversation(agentId, "Default-like") with
        {
            IsDefault = true
        };
        await store.CreateAsync(existing);
        await store.ArchiveAsync(existing.ConversationId);

        var archived = (await store.ListAsync(agentId))
            .Single(c => c.ConversationId == existing.ConversationId);
        archived.Status.ShouldBe(ConversationStatus.Archived);
        archived.ActiveSessionId = null;
        archived.Status = ConversationStatus.Active;
        await store.SaveAsync(archived);

        var reopened = await store.GetAsync(existing.ConversationId);
        reopened.ShouldNotBeNull();

        reopened!.ConversationId.ShouldBe(existing.ConversationId);
        reopened.Status.ShouldBe(ConversationStatus.Active);
        reopened.ActiveSessionId.ShouldBeNull();
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

        var result = await store.ResolveByBindingAsync(agentId, ChannelKey.From("telegram"), ChannelAddress.From("12345"));

        result.ShouldNotBeNull();
        result!.ConversationId.ShouldBe(conv.ConversationId);
    }

    [Fact]
    public async Task ResolveByBindingAsync_DifferentCompositeAddresses_ResolveDistinctConversations()
    {
        // Composite addresses (e.g. <chatId>/topic:<N>) are opaque to the store —
        // it matches by (channelType, channelAddress) exact equality. Different
        // composite addresses for the same underlying chat → different conversations.
        var store = new InMemoryConversationStore();
        var agentId = Agent();
        var convA = MakeConversation(agentId) with
        {
            ChannelBindings =
            [
                new ChannelBinding
                {
                    ChannelType = ChannelKey.From("teams"),
                    ChannelAddress = ChannelAddress.From("team-channel/topic:thread-42")
                }
            ]
        };
        await store.CreateAsync(convA);

        var match = await store.ResolveByBindingAsync(agentId, ChannelKey.From("teams"), ChannelAddress.From("team-channel/topic:thread-42"));
        var noMatch = await store.ResolveByBindingAsync(agentId, ChannelKey.From("teams"), ChannelAddress.From("team-channel/topic:wrong-thread"));

        match.ShouldNotBeNull();
        match!.ConversationId.ShouldBe(convA.ConversationId);
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

        var result = await store.ResolveByBindingAsync(agentId, ChannelKey.From("telegram"), ChannelAddress.From("99999"));
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

    // ── Initiator + ListForCitizenAsync ────────────────────────────────────────

    [Fact]
    public async Task Initiator_NullByDefault_AndRoundTrips_WhenSet()
    {
        var store = new InMemoryConversationStore();
        var agent = Agent();
        var user = CitizenId.Of(UserId.From("alice"));

        var withoutInitiator = MakeConversation(agent, "no-init");
        var withInitiator = MakeConversation(agent, "with-init") with { Initiator = user };

        await store.CreateAsync(withoutInitiator);
        await store.CreateAsync(withInitiator);

        (await store.GetAsync(withoutInitiator.ConversationId))!.Initiator.ShouldBeNull();
        (await store.GetAsync(withInitiator.ConversationId))!.Initiator!.Value.ShouldBe(user);
    }

    [Fact]
    public async Task ListForCitizenAsync_Throws_OnInvalidCitizen()
    {
        var store = new InMemoryConversationStore();
        await Should.ThrowAsync<ArgumentException>(() => store.ListForCitizenAsync(default));
    }

    [Fact]
    public async Task ListForCitizenAsync_User_ReturnsOnly_ConversationsTheyInitiated()
    {
        var store = new InMemoryConversationStore();
        var alice = CitizenId.Of(UserId.From("alice"));
        var bob = CitizenId.Of(UserId.From("bob"));

        var aliceConv = MakeConversation(Agent(), "alice-1") with { Initiator = alice };
        var bobConv = MakeConversation(Agent(), "bob-1") with { Initiator = bob };
        var noOne = MakeConversation(Agent(), "no-init");

        await store.CreateAsync(aliceConv);
        await store.CreateAsync(bobConv);
        await store.CreateAsync(noOne);

        var aliceList = await store.ListForCitizenAsync(alice);
        aliceList.Select(c => c.ConversationId).ShouldBe(new[] { aliceConv.ConversationId }, ignoreOrder: true);

        var bobList = await store.ListForCitizenAsync(bob);
        bobList.Select(c => c.ConversationId).ShouldBe(new[] { bobConv.ConversationId }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListForCitizenAsync_Agent_ReturnsUnion_OfInitiatedAndOwned()
    {
        var store = new InMemoryConversationStore();
        var helper = AgentId.From("helper");
        var other = AgentId.From("other");
        var alice = CitizenId.Of(UserId.From("alice"));
        var helperCitizen = CitizenId.Of(helper);

        // Owned by helper, initiated by a user
        var owned = MakeConversation(helper, "owned-by-helper") with { Initiator = alice };
        // Owned by other agent, but initiated by helper (agent-to-agent)
        var initiatedByHelper = MakeConversation(other, "initiated-by-helper") with { Initiator = helperCitizen };
        // Unrelated to helper entirely
        var unrelated = MakeConversation(other, "unrelated") with { Initiator = alice };

        await store.CreateAsync(owned);
        await store.CreateAsync(initiatedByHelper);
        await store.CreateAsync(unrelated);

        var helperList = await store.ListForCitizenAsync(helperCitizen);

        helperList.Select(c => c.ConversationId).ShouldBe(
            new[] { owned.ConversationId, initiatedByHelper.ConversationId },
            ignoreOrder: true);
    }

    [Fact]
    public async Task ListForCitizenAsync_DeduplicatesWhenInitiatorAndOwnerAreSameAgent()
    {
        var store = new InMemoryConversationStore();
        var helper = AgentId.From("helper");
        var helperCitizen = CitizenId.Of(helper);

        var selfStarted = MakeConversation(helper, "self") with { Initiator = helperCitizen };
        await store.CreateAsync(selfStarted);

        var list = await store.ListForCitizenAsync(helperCitizen);
        list.Count.ShouldBe(1);
        list[0].ConversationId.ShouldBe(selfStarted.ConversationId);
    }

    [Fact]
    public async Task ListForCitizenAsync_IncludesArchivedConversations()
    {
        var store = new InMemoryConversationStore();
        var alice = CitizenId.Of(UserId.From("alice"));

        var conv = MakeConversation(Agent(), "archived") with { Initiator = alice };
        await store.CreateAsync(conv);
        await store.ArchiveAsync(conv.ConversationId);

        var list = await store.ListForCitizenAsync(alice);
        list.Count.ShouldBe(1);
        list[0].Status.ShouldBe(ConversationStatus.Archived);
    }
}
