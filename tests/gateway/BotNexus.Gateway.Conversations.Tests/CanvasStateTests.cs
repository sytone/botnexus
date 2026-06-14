using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Tests for canvas state key-value persistence on conversations.
/// Validates get/set/delete/clear operations against the SQLite store.
/// </summary>
public sealed class CanvasStateTests
{
    // ── GetCanvasStateAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetCanvasStateAsync_ReturnsEmpty_WhenNoStateExists()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        var state = await store.GetCanvasStateAsync(conv.ConversationId);

        state.ShouldNotBeNull();
        state.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCanvasStateAsync_ReturnsNull_WhenConversationNotFound()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var state = await store.GetCanvasStateAsync(ConversationId.From("nonexistent"));

        state.ShouldBeNull();
    }

    // ── SetCanvasStateKeyAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SetCanvasStateKeyAsync_InsertsNewKey()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        var value = JsonDocument.Parse("\"hello\"").RootElement;
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "greeting", value);

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(1);
        state["greeting"].GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task SetCanvasStateKeyAsync_UpsertsExistingKey()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        var v1 = JsonDocument.Parse("42").RootElement;
        var v2 = JsonDocument.Parse("99").RootElement;
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "counter", v1);
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "counter", v2);

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!["counter"].GetInt32().ShouldBe(99);
    }

    [Fact]
    public async Task SetCanvasStateKeyAsync_SupportsMultipleKeys()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        await store.SetCanvasStateKeyAsync(conv.ConversationId, "name", JsonDocument.Parse("\"Alice\"").RootElement);
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "age", JsonDocument.Parse("30").RootElement);
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "active", JsonDocument.Parse("true").RootElement);

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(3);
        state["name"].GetString().ShouldBe("Alice");
        state["age"].GetInt32().ShouldBe(30);
        state["active"].GetBoolean().ShouldBe(true);
    }

    [Fact]
    public async Task SetCanvasStateKeyAsync_SupportsComplexValues()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        var obj = JsonDocument.Parse("{\"x\":1,\"y\":2}").RootElement;
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "position", obj);

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!["position"].GetProperty("x").GetInt32().ShouldBe(1);
        state["position"].GetProperty("y").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task SetCanvasStateKeyAsync_ReturnsFalse_WhenConversationNotFound()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var value = JsonDocument.Parse("\"test\"").RootElement;
        var result = await store.SetCanvasStateKeyAsync(ConversationId.From("nonexistent"), "key", value);

        result.ShouldBeFalse();
    }

    // ── DeleteCanvasStateKeyAsync ──────────────────────────────────────────

    [Fact]
    public async Task DeleteCanvasStateKeyAsync_RemovesKey()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        await store.SetCanvasStateKeyAsync(conv.ConversationId, "temp", JsonDocument.Parse("1").RootElement);
        await store.DeleteCanvasStateKeyAsync(conv.ConversationId, "temp");

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!.ShouldNotContainKey("temp");
    }

    [Fact]
    public async Task DeleteCanvasStateKeyAsync_NoOp_WhenKeyNotFound()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        // Should not throw
        await store.DeleteCanvasStateKeyAsync(conv.ConversationId, "nonexistent");
    }

    [Fact]
    public async Task DeleteCanvasStateKeyAsync_LeavesOtherKeys()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        await store.SetCanvasStateKeyAsync(conv.ConversationId, "keep", JsonDocument.Parse("1").RootElement);
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "remove", JsonDocument.Parse("2").RootElement);
        await store.DeleteCanvasStateKeyAsync(conv.ConversationId, "remove");

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!.Count.ShouldBe(1);
        state.ShouldContainKey("keep");
    }

    // ── ClearCanvasStateAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ClearCanvasStateAsync_RemovesAllKeys()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        await store.SetCanvasStateKeyAsync(conv.ConversationId, "a", JsonDocument.Parse("1").RootElement);
        await store.SetCanvasStateKeyAsync(conv.ConversationId, "b", JsonDocument.Parse("2").RootElement);
        await store.ClearCanvasStateAsync(conv.ConversationId);

        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClearCanvasStateAsync_NoOp_WhenNoState()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv = MakeConversation();
        await store.CreateAsync(conv);

        // Should not throw
        await store.ClearCanvasStateAsync(conv.ConversationId);
    }

    // ── Existence-probe semantics (issue #1387) ───────────────────────────
    //
    // The canvas get/set existence guard was changed from a full conversation
    // hydrate (GetAsync -> 3 queries) to a cheap `SELECT 1 ... LIMIT 1` probe.
    // These tests pin the existence semantics so the perf optimisation cannot
    // silently change behaviour: a conversation that exists in the table must
    // be writable/readable, and one that does not must return false/null —
    // independent of any participants/bindings/cache state.

    [Fact]
    public async Task SetCanvasStateKeyAsync_Succeeds_ForExistingConversationWithoutCacheWarmup()
    {
        using var fixture = new StoreFixture();
        var conv = MakeConversation();

        // Create with one store instance, then operate with a *fresh* instance so
        // the in-memory cache is cold. Existence must be resolved from the DB by
        // the cheap probe, not from a cached hydrate.
        var writer = fixture.CreateStore();
        await writer.CreateAsync(conv);

        var store = fixture.CreateStore();
        var result = await store.SetCanvasStateKeyAsync(
            conv.ConversationId, "k", JsonDocument.Parse("1").RootElement);

        result.ShouldBeTrue();
        var state = await store.GetCanvasStateAsync(conv.ConversationId);
        state.ShouldNotBeNull();
        state!["k"].GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetCanvasStateAsync_ReturnsNull_ForUnknownConversation_AfterOtherConversationsExist()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        // Populate the table with a real conversation + canvas state so the
        // probe runs against a non-empty conversations table.
        var existing = MakeConversation();
        await store.CreateAsync(existing);
        await store.SetCanvasStateKeyAsync(existing.ConversationId, "k", JsonDocument.Parse("1").RootElement);

        // A different, never-created id must probe to "not found" -> null.
        var state = await store.GetCanvasStateAsync(ConversationId.From(Guid.NewGuid().ToString()));
        state.ShouldBeNull();
    }

    [Fact]
    public async Task SetCanvasStateKeyAsync_ReturnsFalse_ForUnknownConversation_AfterOtherConversationsExist()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var existing = MakeConversation();
        await store.CreateAsync(existing);

        var result = await store.SetCanvasStateKeyAsync(
            ConversationId.From(Guid.NewGuid().ToString()), "k", JsonDocument.Parse("1").RootElement);

        result.ShouldBeFalse();
    }

    // ── State isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task CanvasState_IsIsolatedBetweenConversations()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conv1 = MakeConversation();
        var conv2 = MakeConversation();
        await store.CreateAsync(conv1);
        await store.CreateAsync(conv2);

        await store.SetCanvasStateKeyAsync(conv1.ConversationId, "shared-key", JsonDocument.Parse("\"conv1\"").RootElement);
        await store.SetCanvasStateKeyAsync(conv2.ConversationId, "shared-key", JsonDocument.Parse("\"conv2\"").RootElement);

        var state1 = await store.GetCanvasStateAsync(conv1.ConversationId);
        var state2 = await store.GetCanvasStateAsync(conv2.ConversationId);

        state1!["shared-key"].GetString().ShouldBe("conv1");
        state2!["shared-key"].GetString().ShouldBe("conv2");
    }

    // ── Persistence ────────────────────────────────────────────────────────

    [Fact]
    public async Task CanvasState_SurvivesGatewayRestart()
    {
        using var fixture = new StoreFixture();
        var store1 = fixture.CreateStore();
        var conv = MakeConversation();
        await store1.CreateAsync(conv);
        await store1.SetCanvasStateKeyAsync(conv.ConversationId, "persistent", JsonDocument.Parse("true").RootElement);

        // Simulate restart by creating a new store instance against same DB
        var store2 = fixture.CreateStore();
        var state = await store2.GetCanvasStateAsync(conv.ConversationId);

        state.ShouldNotBeNull();
        state!["persistent"].GetBoolean().ShouldBe(true);
    }

    private static Conversation MakeConversation() => new()
    {
        ConversationId = ConversationId.From(Guid.NewGuid().ToString()),
        AgentId = AgentId.From("test-agent"),
        Title = "canvas state test"
    };
}
