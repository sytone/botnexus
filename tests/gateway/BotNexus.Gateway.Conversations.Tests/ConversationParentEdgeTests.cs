using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Domain.World;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Store-level tests for the #2338 nested-run edge: <see cref="Conversation.ParentConversationId"/>
/// and <see cref="Conversation.SpawningToolCallId"/>.
/// </summary>
/// <remarks>
/// Two contracts are pinned here. (1) The edge round-trips through persistence, and rows written
/// before the columns existed still hydrate (as top-level conversations, i.e. a null parent).
/// (2) A conversation with a parent is <em>nested</em> and must not appear in the top-level listing -
/// this is the presentation fix that #468 originally reached for by collapsing the child's identity
/// onto the parent's, which is what flooded the parent's SignalR group.
/// </remarks>
public sealed class ConversationParentEdgeTests
{
    [Fact]
    public async Task ParentConversationIdAndSpawningToolCallId_RoundTrip()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var parent = ConversationId.Create();

        var child = ConversationFactory.CreateForSubAgent(
            ConversationId.Create(),
            AgentId.From("parent--subagent--general--abc"),
            parent,
            spawningToolCallId: "call_xyz",
            title: "nested run");
        await store.CreateAsync(child);

        // Read through a fresh store instance so the assertion goes to SQLite, not the cache.
        var loaded = await fixture.CreateStore().GetAsync(child.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ParentConversationId.ShouldBe(parent);
        loaded.SpawningToolCallId.ShouldBe("call_xyz");
        loaded.Kind.ShouldBe(ConversationKind.AgentSubAgent);
        loaded.Source.ShouldBe(ConversationSource.Agent);
    }

    [Fact]
    public async Task TopLevelConversation_HasNullParentEdge()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conversation = ConversationFactory.CreateForChannel(
            ConversationId.Create(),
            AgentId.From("agent-a"),
            "ordinary");
        await store.CreateAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ParentConversationId.ShouldBeNull();
        loaded.SpawningToolCallId.ShouldBeNull();
    }

    [Fact]
    public async Task GetSummariesAsync_ExcludesNestedConversations()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var parent = ConversationFactory.CreateForChannel(
            ConversationId.Create(),
            AgentId.From("agent-a"),
            "supervisor");
        await store.CreateAsync(parent);

        var child = ConversationFactory.CreateForSubAgent(
            ConversationId.Create(),
            AgentId.From("agent-a--subagent--general--abc"),
            parent.ConversationId,
            spawningToolCallId: "call_1",
            title: "nested run");
        await store.CreateAsync(child);

        var summaries = await store.GetSummariesAsync();

        summaries.Select(s => s.ConversationId).ShouldContain(parent.ConversationId.Value);
        summaries.Select(s => s.ConversationId).ShouldNotContain(
            child.ConversationId.Value,
            "a sub-agent run is reachable only by expanding its spawning tool call in the parent " +
            "conversation, so it must never be listed at top level (#2338).");
    }

    [Fact]
    public async Task NestedConversation_IsStillDirectlyLoadable()
    {
        // Excluded from the *listing*, not from existence: expanding the tool call must be able to
        // load the child's own history, so GetAsync has to keep working.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var parent = ConversationId.Create();

        var child = ConversationFactory.CreateForSubAgent(
            ConversationId.Create(),
            AgentId.From("agent-a--subagent--general--abc"),
            parent,
            title: "nested run");
        await store.CreateAsync(child);

        (await store.GetAsync(child.ConversationId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task LegacyRow_WithoutParentColumns_HydratesAsTopLevel()
    {
        // Back-compat: a row written before the columns existed (simulated by NULLing them) must
        // load unchanged and be treated as top-level rather than throwing on the missing edge.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conversation = ConversationFactory.CreateForChannel(
            ConversationId.Create(),
            AgentId.From("agent-a"),
            "legacy");
        await store.CreateAsync(conversation);

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE conversations SET parent_conversation_id = NULL, spawning_tool_call_id = NULL WHERE id = $id";
            command.Parameters.AddWithValue("$id", conversation.ConversationId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ParentConversationId.ShouldBeNull();
        (await fixture.CreateStore().GetSummariesAsync())
            .Select(s => s.ConversationId)
            .ShouldContain(conversation.ConversationId.Value);
    }

    [Fact]
    public async Task InMemoryStore_GetSummariesAsync_ExcludesNestedConversations()
    {
        // Parity: the listing rule is a store contract, not a SQLite implementation detail.
        var store = new InMemoryConversationStore(new FakeWorldContext());

        var parent = ConversationFactory.CreateForChannel(
            ConversationId.Create(), AgentId.From("agent-a"), "supervisor");
        await store.CreateAsync(parent);
        var child = ConversationFactory.CreateForSubAgent(
            ConversationId.Create(), AgentId.From("agent-a--sub"), parent.ConversationId, title: "nested");
        await store.CreateAsync(child);

        var summaries = await store.GetSummariesAsync();

        summaries.Select(s => s.ConversationId).ShouldContain(parent.ConversationId.Value);
        summaries.Select(s => s.ConversationId).ShouldNotContain(child.ConversationId.Value);
    }
}
