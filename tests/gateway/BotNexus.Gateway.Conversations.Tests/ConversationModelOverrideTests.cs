using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Round-trip persistence tests for the per-conversation model / thinking / context override
/// fields (PBI5, issue #1706). These pin the "persists across restart" acceptance criterion by
/// saving through one store instance and reading back through a fresh instance pointed at the same
/// SQLite file, then pin "clears back to agent" by nulling the fields and confirming the reload
/// observes no override.
/// </summary>
public sealed class ConversationModelOverrideTests
{
    [Fact]
    public async Task Overrides_RoundTripAcrossStoreInstances()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        conversation.ModelOverride = "claude-opus-4";
        conversation.ThinkingOverride = "high";
        conversation.ContextWindowOverride = 200_000;

        await store.CreateAsync(conversation);

        // Fresh store instance == simulated gateway restart against the same database file.
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ModelOverride.ShouldBe("claude-opus-4");
        loaded.ThinkingOverride.ShouldBe("high");
        loaded.ContextWindowOverride.ShouldBe(200_000);
    }

    [Fact]
    public async Task Overrides_DefaultToNull_WhenNeverSet()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");

        await store.CreateAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ModelOverride.ShouldBeNull();
        loaded.ThinkingOverride.ShouldBeNull();
        loaded.ContextWindowOverride.ShouldBeNull();
    }

    [Fact]
    public async Task Overrides_ClearBackToAgent_WhenNulledAndSaved()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        conversation.ModelOverride = "claude-opus-4";
        conversation.ThinkingOverride = "max";
        conversation.ContextWindowOverride = 1_000_000;
        await store.CreateAsync(conversation);

        var reloaded = await store.GetAsync(conversation.ConversationId);
        reloaded.ShouldNotBeNull();
        reloaded!.ModelOverride = null;
        reloaded.ThinkingOverride = null;
        reloaded.ContextWindowOverride = null;
        await store.SaveAsync(reloaded);

        // Simulated restart confirms the cleared state is durable, not just cache-local.
        var afterClear = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        afterClear.ShouldNotBeNull();
        afterClear!.ModelOverride.ShouldBeNull();
        afterClear.ThinkingOverride.ShouldBeNull();
        afterClear.ContextWindowOverride.ShouldBeNull();
    }

    private static Conversation NewConversation(string agentId)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(agentId),
            Title = "Override test",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
