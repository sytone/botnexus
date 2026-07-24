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

    /// <summary>
    /// Creates a store whose internal entity cache (if the implementation has one) is capped
    /// at <paramref name="capacity"/> entries — smaller than the datasets used by the
    /// capacity-stress contract test. Implementations backed by a bounded cache MUST override
    /// this so the parity suite exercises list materialisation under eviction pressure; the
    /// default returns <c>null</c> for cache-free implementations, and the test falls back to
    /// <see cref="CreateStore"/>. This is the generalised guard for #2226: list completeness
    /// must never depend on cache survival.
    /// </summary>
    protected virtual IConversationStore? CreateCapacityConstrainedStore(int capacity) => null;

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
    public async Task ListAsync_ReturnsEveryRow_WhenCountFarExceedsCacheCapacity()
    {
        // Generalised parity guard for #2226: no store implementation may gate the membership
        // of a list result on a bounded read-through cache surviving materialisation. With far
        // more conversations than the cache capacity, warming id N can evict an earlier id; a
        // materialiser that rebuilds the result by re-reading from the cache then silently drops
        // the evicted rows (the flicker bug). For cache-free stores this asserts plain
        // completeness. Runs against every IConversationStore implementation.
        const int capacity = 8;
        const int total = 50;
        var store = CreateCapacityConstrainedStore(capacity) ?? CreateStore();

        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < total; i++)
        {
            var conv = MakeConversation(title: $"conv-{i:D3}");
            await store.CreateAsync(conv);
            expected.Add(conv.ConversationId.Value);
        }

        var list = await store.ListAsync();

        list.Count.ShouldBe(total);
        var returnedIds = list.Select(c => c.ConversationId.Value).ToHashSet(StringComparer.Ordinal);
        returnedIds.SetEquals(expected).ShouldBeTrue();
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

    // ── TodoJson (issue #1464 Step 1/6) ───────────────────────────────────

    [Fact]
    public async Task TodoJson_RoundTrips_AcrossSaveAndReload()
    {
        var store = CreateStore();
        var conv = MakeConversation();
        const string todo = "{\"items\":[{\"id\":\"a\",\"text\":\"first\",\"status\":\"pending\"}]}";
        conv = conv with { TodoJson = todo };

        await store.CreateAsync(conv);
        var loaded = await store.GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.TodoJson.ShouldBe(todo);
    }

    [Fact]
    public async Task TodoJson_DefaultsToNull_WhenNeverSet()
    {
        var store = CreateStore();
        var conv = MakeConversation();

        await store.CreateAsync(conv);
        var loaded = await store.GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.TodoJson.ShouldBeNull();
    }

    [Fact]
    public async Task TodoJson_PersistsUpdate_OnExistingConversation()
    {
        var store = CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        const string updated = "{\"items\":[{\"id\":\"a\",\"text\":\"done it\",\"status\":\"done\"}]}";
        conv = conv with { TodoJson = updated };
        await store.SaveAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.TodoJson.ShouldBe(updated);
    }

    [Fact]
    public async Task TodoJson_CanBeClearedBackToNull()
    {
        var store = CreateStore();
        var conv = MakeConversation() with { TodoJson = "{\"items\":[]}" };
        await store.CreateAsync(conv);

        conv = conv with { TodoJson = null };
        await store.SaveAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.TodoJson.ShouldBeNull();
    }

    // ── PendingAskUserJson (issue #1488, ask_user durability Step 1/5) ─────

    [Fact]
    public async Task PendingAskUserJson_RoundTrips_AcrossSaveAndReload()
    {
        var store = CreateStore();
        var conv = MakeConversation();
        const string pending = "{\"requestId\":\"r1\",\"conversationId\":\"c1\",\"prompt\":\"Pick one\",\"inputType\":\"SingleChoice\"}";
        conv = conv with { PendingAskUserJson = pending };

        await store.CreateAsync(conv);
        var loaded = await store.GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.PendingAskUserJson.ShouldBe(pending);
    }

    [Fact]
    public async Task PendingAskUserJson_DefaultsToNull_WhenNeverSet()
    {
        var store = CreateStore();
        var conv = MakeConversation();

        await store.CreateAsync(conv);
        var loaded = await store.GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.PendingAskUserJson.ShouldBeNull();
    }

    [Fact]
    public async Task PendingAskUserJson_PersistsUpdate_OnExistingConversation()
    {
        var store = CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        const string updated = "{\"requestId\":\"r2\",\"conversationId\":\"c1\",\"prompt\":\"Type a value\",\"inputType\":\"FreeForm\"}";
        conv = conv with { PendingAskUserJson = updated };
        await store.SaveAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.PendingAskUserJson.ShouldBe(updated);
    }

    [Fact]
    public async Task PendingAskUserJson_CanBeClearedBackToNull()
    {
        var store = CreateStore();
        var conv = MakeConversation() with { PendingAskUserJson = "{\"requestId\":\"r3\",\"prompt\":\"q\",\"inputType\":\"FreeForm\"}" };
        await store.CreateAsync(conv);

        conv = conv with { PendingAskUserJson = null };
        await store.SaveAsync(conv);

        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.PendingAskUserJson.ShouldBeNull();
    }
}
