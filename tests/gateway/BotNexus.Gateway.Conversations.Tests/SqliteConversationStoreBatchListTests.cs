using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Regression guard for the N+1 elimination in <see cref="SqliteConversationStore"/> list
/// endpoints (issue #1626). <c>ListAsync</c> and <c>ListForCitizenAsync</c> previously issued
/// <c>SELECT id</c> followed by a per-row loader that fired three more queries each
/// (row + bindings + participants) — an <c>1 + 3N</c> round-trip fan-out. These tests assert
/// (a) the batched loader still returns full-fidelity objects with bindings and participants
/// correctly grouped per conversation, (b) ordering is preserved, (c) a mixed cached/uncached
/// list call is correct, and (d) the number of database round-trips is bounded (does NOT grow
/// with the row count).
/// </summary>
public sealed class SqliteConversationStoreBatchListTests
{
    [Fact]
    public async Task ListAsync_BatchesChildCollections_BoundedRoundTrips_RegardlessOfRowCount()
    {
        using var fixture = new StoreFixture();
        var seed = fixture.CreateStore();
        for (var i = 0; i < 12; i++)
            await seed.CreateAsync(CreateConversation(Agent("agent-a"), $"conv-{i}", CreateBinding("telegram", $"{i}")));

        // Fresh store => empty cache => every row must be loaded from disk.
        var store = fixture.CreateStore();
        store.ResetReadRoundTripCount();
        var list = await store.ListAsync();

        list.Count.ShouldBe(12);
        // Bounded: the batched loader issues a small fixed number of queries (the id listing
        // plus one batched query per child table), NOT one-plus-three-per-row. With 12 rows the
        // old 1 + 3N shape would be ~37 round-trips. Assert we stay well under a per-row bound.
        var roundTrips = store.ReadRoundTripCount;
        roundTrips.ShouldBeLessThan(12,
            $"List of 12 conversations must not fan out per-row (was {roundTrips} round-trips).");
    }

    [Fact]
    public async Task ListAsync_GroupsBindingsAndParticipants_ToTheCorrectConversation()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var a = CreateConversation(Agent("agent-a"), "A",
            CreateBinding("telegram", "a-1"), CreateBinding("signal", "a-2"));
        var b = CreateConversation(Agent("agent-a"), "B",
            CreateBinding("telegram", "b-1"));
        var c = CreateConversation(Agent("agent-a"), "C"); // no bindings at all
        await store.CreateAsync(a);
        await store.CreateAsync(b);
        await store.CreateAsync(c);

        await store.AddParticipantsAsync(a.ConversationId,
        [
            new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-x")), Role = "peer" },
            new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("user-y")) }
        ]);
        await store.AddParticipantsAsync(b.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-z")) }]);

        // Fresh store so the list path reloads from disk via the batched loader.
        var list = await fixture.CreateStore().ListAsync(Agent("agent-a"));

        var loadedA = list.Single(x => x.ConversationId == a.ConversationId);
        var loadedB = list.Single(x => x.ConversationId == b.ConversationId);
        var loadedC = list.Single(x => x.ConversationId == c.ConversationId);

        loadedA.ChannelBindings.Select(x => x.ChannelAddress.Value).OrderBy(x => x)
            .ShouldBe(["a-1", "a-2"]);
        loadedB.ChannelBindings.ShouldHaveSingleItem().ChannelAddress.Value.ShouldBe("b-1");
        loadedC.ChannelBindings.ShouldBeEmpty();

        loadedA.Participants.Select(p => p.CitizenId).ShouldBe(
        [
            CitizenId.Of(AgentId.From("agent-x")),
            CitizenId.Of(UserId.From("user-y"))
        ], ignoreOrder: true);
        loadedA.Participants.Single(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-x"))).Role.ShouldBe("peer");
        loadedB.Participants.ShouldHaveSingleItem().CitizenId.ShouldBe(CitizenId.Of(AgentId.From("agent-z")));
        loadedC.Participants.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_PreservesUpdatedAtDescendingOrder()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var older = CreateConversation(Agent("agent-a"), "older");
        older.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var newer = CreateConversation(Agent("agent-a"), "newer");
        newer.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var middle = CreateConversation(Agent("agent-a"), "middle");
        middle.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.CreateAsync(older);
        await store.CreateAsync(newer);
        await store.CreateAsync(middle);

        var list = await fixture.CreateStore().ListAsync(Agent("agent-a"));

        list.Select(c => c.Title).ShouldBe(["newer", "middle", "older"]);
    }

    [Fact]
    public async Task ListAsync_MixedCachedAndUncached_ReturnsAllWithFidelity()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var cached = CreateConversation(Agent("agent-a"), "cached", CreateBinding("telegram", "c-1"));
        var cold = CreateConversation(Agent("agent-a"), "cold", CreateBinding("signal", "k-1"));
        await store.CreateAsync(cached);
        await store.CreateAsync(cold);

        // Warm only one of the two into the cache on this same store instance.
        _ = await store.GetAsync(cached.ConversationId);

        var list = await store.ListAsync(Agent("agent-a"));

        list.Count.ShouldBe(2);
        list.Single(x => x.ConversationId == cached.ConversationId)
            .ChannelBindings.ShouldHaveSingleItem().ChannelAddress.Value.ShouldBe("c-1");
        list.Single(x => x.ConversationId == cold.ConversationId)
            .ChannelBindings.ShouldHaveSingleItem().ChannelAddress.Value.ShouldBe("k-1");
    }

    [Fact]
    public async Task ListForCitizenAsync_BatchesChildCollections_WithFidelity()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var citizen = CitizenId.Of(AgentId.From("agent-owner"));

        var one = CreateConversation(AgentId.From("agent-owner"), "one", CreateBinding("telegram", "1-a"));
        var two = CreateConversation(AgentId.From("agent-owner"), "two",
            CreateBinding("telegram", "2-a"), CreateBinding("signal", "2-b"));
        await store.CreateAsync(one);
        await store.CreateAsync(two);

        var list = await fixture.CreateStore().ListForCitizenAsync(citizen);

        list.Count.ShouldBe(2);
        list.Single(x => x.ConversationId == one.ConversationId)
            .ChannelBindings.ShouldHaveSingleItem().ChannelAddress.Value.ShouldBe("1-a");
        list.Single(x => x.ConversationId == two.ConversationId)
            .ChannelBindings.Select(b => b.ChannelAddress.Value).OrderBy(x => x).ShouldBe(["2-a", "2-b"]);
    }

    [Fact]
    public async Task ListForCitizenAsync_IncludesParticipantConversations_BatchedHydration()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var participant = CitizenId.Of(AgentId.From("agent-guest"));

        // Conversation owned by a different agent, where agent-guest is a participant.
        var hosted = CreateConversation(AgentId.From("agent-host"), "hosted", CreateBinding("telegram", "h-1"));
        await store.CreateAsync(hosted);
        await store.AddParticipantsAsync(hosted.ConversationId,
            [new SessionParticipant { CitizenId = participant, Role = "guest" }]);

        var list = await fixture.CreateStore().ListForCitizenAsync(participant);

        var loaded = list.ShouldHaveSingleItem();
        loaded.ConversationId.ShouldBe(hosted.ConversationId);
        loaded.ChannelBindings.ShouldHaveSingleItem().ChannelAddress.Value.ShouldBe("h-1");
        loaded.Participants.ShouldContain(p => p.CitizenId == participant && p.Role == "guest");
    }

    [Fact]
    public async Task ListAsync_EmptyResult_DoesNotIssuePerRowQueries()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        store.ResetReadRoundTripCount();
        var list = await store.ListAsync();

        list.ShouldBeEmpty();
        // An empty list must short-circuit the child-collection queries entirely.
        store.ReadRoundTripCount.ShouldBe(0);
    }

    private static AgentId Agent(string id) => AgentId.From(id);

    private static Conversation CreateConversation(AgentId agentId, string title, params ChannelBinding[] bindings)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChannelBindings = bindings.ToList()
        };

    private static ChannelBinding CreateBinding(string channelType, string channelAddress)
        => new()
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From(channelType),
            ChannelAddress = ChannelAddress.From(channelAddress),
            BoundAt = DateTimeOffset.UtcNow,
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single
        };
}
