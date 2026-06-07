using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Abstract contract test base for <see cref="IConversationStore"/>. Each concrete subclass
/// provides a real implementation via <see cref="CreateStore"/>. All tests run identically
/// against every implementation to enforce parity.
/// </summary>
public abstract class ConversationStoreContractTests
{
    /// <summary>Creates a fresh store instance for a single test.</summary>
    protected abstract IConversationStore CreateStore();

    /// <summary>Disposes any resources used by the store fixture (e.g. temp DB).</summary>
    protected virtual void DisposeStore() { }

    private static AgentId Agent(string id = "agent-1") => AgentId.From(id);
    private static ConversationId NewId() => ConversationId.Create();

    private static Conversation MakeConversation(AgentId? agentId = null, string? title = null) =>
        new()
        {
            ConversationId = NewId(),
            AgentId = agentId ?? Agent(),
            Title = title ?? "Test conversation"
        };

    // ── GetAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var store = CreateStore();
        var result = await store.GetAsync(NewId());
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsConversation_AfterCreate()
    {
        var store = CreateStore();
        var conv = MakeConversation();

        await store.CreateAsync(conv);
        var loaded = await store.GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ConversationId.ShouldBe(conv.ConversationId);
        loaded.AgentId.ShouldBe(conv.AgentId);
        loaded.Title.ShouldBe(conv.Title);
    }

    // ── CreateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ReturnsCreatedConversation()
    {
        var store = CreateStore();
        var conv = MakeConversation();

        var result = await store.CreateAsync(conv);

        result.ShouldNotBeNull();
        result.ConversationId.ShouldBe(conv.ConversationId);
    }

    [Fact]
    public async Task CreateAsync_ThrowsOnDuplicateId()
    {
        var store = CreateStore();
        var conv = MakeConversation();

        await store.CreateAsync(conv);
        await Should.ThrowAsync<Exception>(async () => await store.CreateAsync(conv));
    }

    // ── SaveAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_PersistsUpdatedTitle()
    {
        var store = CreateStore();
        var conv = MakeConversation(title: "Original");
        await store.CreateAsync(conv);

        conv = conv with { Title = "Updated" };
        await store.SaveAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("Updated");
    }

    [Fact]
    public async Task SaveAsync_CreatesIfNotExists()
    {
        var store = CreateStore();
        var conv = MakeConversation();

        await store.SaveAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.ConversationId.ShouldBe(conv.ConversationId);
    }

    // ── ListAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenNoConversations()
    {
        var store = CreateStore();
        var result = await store.ListAsync();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_ReturnsAll_WhenNoAgentFilter()
    {
        var store = CreateStore();
        await store.CreateAsync(MakeConversation(Agent("a")));
        await store.CreateAsync(MakeConversation(Agent("b")));

        var result = await store.ListAsync();
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListAsync_FiltersByAgent()
    {
        var store = CreateStore();
        await store.CreateAsync(MakeConversation(Agent("a")));
        await store.CreateAsync(MakeConversation(Agent("b")));

        var result = await store.ListAsync(Agent("a"));
        result.Count.ShouldBe(1);
        result[0].AgentId.ShouldBe(Agent("a"));
    }

    // ── ArchiveAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ArchiveAsync_SetsStatusToArchived()
    {
        var store = CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        await store.ArchiveAsync(conv.ConversationId);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task ArchiveAsync_NoOp_WhenNotFound()
    {
        var store = CreateStore();
        // Should not throw
        await store.ArchiveAsync(NewId());
    }

    [Fact]
    public async Task ArchiveAsync_ConversationStillVisibleInListAsync()
    {
        // NOTE: ListAsync returns ALL conversations regardless of status.
        // This is the actual contract behaviour across all implementations.
        // GetSummariesAsync is the method that filters to Active-only.
        var store = CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        await store.ArchiveAsync(conv.ConversationId);

        var list = await store.ListAsync();
        list.ShouldContain(c => c.ConversationId == conv.ConversationId);
    }

    // ── TouchAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TouchAsync_UpdatesTimestamp()
    {
        var store = CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        var before = (await store.GetAsync(conv.ConversationId))!.UpdatedAt;
        await Task.Delay(50); // Ensure time advances
        await store.TouchAsync(conv.ConversationId);

        var after = (await store.GetAsync(conv.ConversationId))!.UpdatedAt;
        after.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task TouchAsync_NoOp_WhenNotFound()
    {
        var store = CreateStore();
        // Should not throw
        await store.TouchAsync(NewId());
    }
}
