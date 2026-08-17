using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Round-trip persistence tests for the per-session tool overlay (issue #2523).
/// </summary>
/// <remarks>
/// The feature's value depends on the restriction being durable: an operator who drops <c>exec</c>
/// for a risky conversation must not silently get it back when the client reconnects or the gateway
/// restarts. These tests save through one store instance and read back through a fresh instance
/// pointed at the same SQLite file, which is the simulated-restart shape used by the sibling
/// model-override tests.
/// </remarks>
public sealed class ConversationToolOverrideTests
{
    private const string OverlayJson = """{"disabledTools":["exec","shell"]}""";

    [Fact]
    public async Task ToolOverride_RoundTripsAcrossStoreInstances()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        conversation.ToolOverrideJson = OverlayJson;
        await store.CreateAsync(conversation);

        // Fresh store instance == simulated gateway restart against the same database file.
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ToolOverrideJson.ShouldBe(OverlayJson);
    }

    [Fact]
    public async Task ToolOverride_DefaultsToNull_WhenNeverSet()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        await store.CreateAsync(conversation);

        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.ToolOverrideJson.ShouldBeNull();
    }

    [Fact]
    public async Task PatchOverride_WritesAndClearsTheOverlay()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        await store.CreateAsync(conversation);

        var patched = await store.PatchOverrideAsync(
            conversation.ConversationId,
            new ConversationOverridePatch { ToolOverrideJson = FieldUpdate<string?>.Set(OverlayJson) });

        patched.ShouldNotBeNull();
        patched!.ToolOverrideJson.ShouldBe(OverlayJson);

        var cleared = await store.PatchOverrideAsync(
            conversation.ConversationId,
            new ConversationOverridePatch { ToolOverrideJson = FieldUpdate<string?>.Set(null) });

        cleared.ShouldNotBeNull();
        cleared!.ToolOverrideJson.ShouldBeNull();
    }

    [Fact]
    public async Task PatchOverride_DoesNotDisturbTheModelOverride()
    {
        // #2139 clobber-avoidance: the two overrides ride the same patch type, so writing one must
        // leave the other exactly as committed.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        conversation.ModelOverride = "claude-opus-4";
        await store.CreateAsync(conversation);

        var patched = await store.PatchOverrideAsync(
            conversation.ConversationId,
            new ConversationOverridePatch { ToolOverrideJson = FieldUpdate<string?>.Set(OverlayJson) });

        patched.ShouldNotBeNull();
        patched!.ToolOverrideJson.ShouldBe(OverlayJson);
        patched.ModelOverride.ShouldBe("claude-opus-4");
    }

    [Fact]
    public async Task FullSave_DoesNotWipeAnIndependentlyPatchedOverlay()
    {
        // Guards the clone path: the overlay was initially omitted from the SaveAsync clone, which
        // is exactly the #2131 defect shape - a full save silently NULLing a committed override.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = NewConversation("agent-a");
        conversation.ToolOverrideJson = OverlayJson;
        await store.CreateAsync(conversation);

        var reloaded = await store.GetAsync(conversation.ConversationId);
        reloaded.ShouldNotBeNull();
        reloaded!.Title = "Renamed";
        await store.SaveAsync(reloaded);

        var afterSave = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        afterSave.ShouldNotBeNull();
        afterSave!.Title.ShouldBe("Renamed");
        afterSave.ToolOverrideJson.ShouldBe(OverlayJson);
    }

    private static Conversation NewConversation(string agentId)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From(agentId),
            Title = "Tool override test",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
