using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the canvas state REST API endpoints (Issue #1066).
/// Verifies CRUD operations on conversation canvas state via ConversationsController.
/// </summary>
public sealed class ConversationsControllerCanvasStateTests
{
    private static readonly ConversationId TestConversationId = ConversationId.From("c_canvas_test");

    [Fact]
    public async Task GetCanvasState_EmptyConversation_ReturnsEmptyObject()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetCanvasState(TestConversationId.Value, CancellationToken.None);

        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var state = okResult.Value as Dictionary<string, JsonElement>;
        state.ShouldNotBeNull();
        state.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetCanvasState_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetCanvasState("c_nonexistent", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetCanvasStateKey_ExistingKey_ReturnsValue()
    {
        var (controller, store) = CreateController();
        var value = JsonDocument.Parse("42").RootElement.Clone();
        await store.SetCanvasStateKeyAsync(TestConversationId, "counter", value);

        var result = await controller.GetCanvasStateKey(TestConversationId.Value, "counter", CancellationToken.None);

        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var returnedValue = (JsonElement)okResult.Value!;
        returnedValue.GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task GetCanvasStateKey_MissingKey_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetCanvasStateKey(TestConversationId.Value, "missing", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetCanvasStateKey_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetCanvasStateKey("c_nonexistent", "key", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SetCanvasStateKey_NewKey_Returns200()
    {
        var (controller, store) = CreateController();
        var value = JsonDocument.Parse("\"hello\"").RootElement.Clone();

        var result = await controller.SetCanvasStateKey(TestConversationId.Value, "greeting", value, CancellationToken.None);

        result.ShouldBeOfType<OkResult>();

        // Verify persisted
        var state = await store.GetCanvasStateAsync(TestConversationId);
        state.ShouldNotBeNull();
        state!["greeting"].GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task SetCanvasStateKey_ExistingKey_Upserts()
    {
        var (controller, store) = CreateController();
        var first = JsonDocument.Parse("1").RootElement.Clone();
        var second = JsonDocument.Parse("2").RootElement.Clone();

        await controller.SetCanvasStateKey(TestConversationId.Value, "version", first, CancellationToken.None);
        var result = await controller.SetCanvasStateKey(TestConversationId.Value, "version", second, CancellationToken.None);

        result.ShouldBeOfType<OkResult>();
        var state = await store.GetCanvasStateAsync(TestConversationId);
        state!["version"].GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task SetCanvasStateKey_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();
        var value = JsonDocument.Parse("true").RootElement.Clone();

        var result = await controller.SetCanvasStateKey("c_nonexistent", "key", value, CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteCanvasStateKey_ExistingKey_Returns204()
    {
        var (controller, store) = CreateController();
        var value = JsonDocument.Parse("\"temp\"").RootElement.Clone();
        await store.SetCanvasStateKeyAsync(TestConversationId, "temp", value);

        var result = await controller.DeleteCanvasStateKey(TestConversationId.Value, "temp", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();

        // Verify deleted
        var state = await store.GetCanvasStateAsync(TestConversationId);
        state.ShouldNotBeNull();
        state!.ContainsKey("temp").ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteCanvasStateKey_MissingKey_Returns204()
    {
        var (controller, _) = CreateController();

        // Deleting a non-existent key is idempotent
        var result = await controller.DeleteCanvasStateKey(TestConversationId.Value, "no_such_key", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteCanvasStateKey_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.DeleteCanvasStateKey("c_nonexistent", "key", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    private static (ConversationsController, InMemoryConversationStore) CreateController()
    {
        var store = new InMemoryConversationStore();
        store.CreateAsync(new Conversation
        {
            ConversationId = TestConversationId,
            AgentId = AgentId.From("test-agent"),
            Title = "Test Canvas Conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(store, sessions);
        return (controller, store);
    }
}
