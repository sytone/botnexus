using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Tests for conversation pinning behaviour in <see cref="SqliteConversationStore"/>.
/// </summary>
public sealed class ConversationPinTests
{
    private static AgentId Agent(string id) => AgentId.From(id);

    private static Conversation CreateConversation(AgentId agentId, string title)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = agentId,
            Title = title,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task PinAsync_Sets_IsPinned_And_PinnedAt()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Pin me");
        await store.CreateAsync(conversation);

        await store.PinAsync(conversation.ConversationId, true);

        var loaded = await store.GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.IsPinned.ShouldBeTrue();
        loaded.PinnedAt.ShouldNotBeNull();
        loaded.PinnedAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task PinAsync_Unpin_Clears_IsPinned_And_PinnedAt()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = CreateConversation(Agent("agent-a"), "Unpin me");
        await store.CreateAsync(conversation);

        await store.PinAsync(conversation.ConversationId, true);
        await store.PinAsync(conversation.ConversationId, false);

        var loaded = await store.GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.IsPinned.ShouldBeFalse();
        loaded.PinnedAt.ShouldBeNull();
    }

    [Fact]
    public async Task PinAsync_Nonexistent_Conversation_Noops()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        // Should not throw
        await store.PinAsync(ConversationId.From("nonexistent-id"), true);
    }

    [Fact]
    public async Task GetSummariesAsync_Orders_Pinned_First()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var unpinned = CreateConversation(Agent("agent-a"), "Unpinned");
        unpinned.UpdatedAt = DateTimeOffset.UtcNow;
        await store.CreateAsync(unpinned);

        // Small delay to ensure ordering by updated_at is deterministic
        await Task.Delay(50);

        var pinned = CreateConversation(Agent("agent-a"), "Pinned");
        pinned.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1); // older updated_at, but pinned
        await store.CreateAsync(pinned);
        await store.PinAsync(pinned.ConversationId, true);

        var summaries = await store.GetSummariesAsync();
        summaries.Count.ShouldBeGreaterThanOrEqualTo(2);

        var pinnedSummary = summaries.First(s => s.ConversationId == pinned.ConversationId.Value);
        var unpinnedSummary = summaries.First(s => s.ConversationId == unpinned.ConversationId.Value);

        pinnedSummary.IsPinned.ShouldBeTrue();
        pinnedSummary.PinnedAt.ShouldNotBeNull();
        unpinnedSummary.IsPinned.ShouldBeFalse();

        // Pinned should come first regardless of UpdatedAt
        var pinnedIndex = summaries.ToList().IndexOf(pinnedSummary);
        var unpinnedIndex = summaries.ToList().IndexOf(unpinnedSummary);
        pinnedIndex.ShouldBeLessThan(unpinnedIndex);
    }

    [Fact]
    public async Task ListForCitizenAsync_Includes_Pin_State()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var agentId = Agent("agent-pin-list");
        var conversation = CreateConversation(agentId, "Pinned citizen conv");
        await store.CreateAsync(conversation);
        await store.PinAsync(conversation.ConversationId, true);

        var citizen = CitizenId.Of(agentId);
        var results = await store.ListForCitizenAsync(citizen);

        results.Count.ShouldBeGreaterThanOrEqualTo(1);
        var loaded = results.First(c => c.ConversationId == conversation.ConversationId);
        loaded.IsPinned.ShouldBeTrue();
        loaded.PinnedAt.ShouldNotBeNull();
    }
}
