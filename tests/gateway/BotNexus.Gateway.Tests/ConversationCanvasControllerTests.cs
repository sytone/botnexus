using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the canvas/canvas-state REST API endpoints (Issues #1066 + #1688).
/// Verifies the six canvas endpoints on the dedicated <see cref="ConversationCanvasController"/>,
/// which depends only on <see cref="IConversationStore"/> (plus optional canvas notifiers),
/// exercising canvas-state CRUD against an in-memory store without the heavyweight
/// conversation-CRUD dependency surface.
/// </summary>
public sealed class ConversationCanvasControllerTests
{
    private static readonly ConversationId TestConversationId = ConversationId.From("c_canvas_test");

    // -- Canvas HTML -------------------------------------------------------

    [Fact]
    public async Task GetCanvas_NoHtml_Returns204()
    {
        var (controller, _) = CreateController();
        var result = await controller.GetCanvas("test-agent", TestConversationId.Value, CancellationToken.None);
        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetCanvas_WithHtml_ReturnsContent()
    {
        var (controller, store) = CreateController();
        var conversation = await store.GetAsync(TestConversationId);
        conversation!.CanvasHtml = "<h1>hi</h1>";
        await store.SaveAsync(conversation);
        var result = await controller.GetCanvas("test-agent", TestConversationId.Value, CancellationToken.None);
        var content = result.ShouldBeOfType<ContentResult>();
        content.Content.ShouldBe("<h1>hi</h1>");
        content.ContentType.ShouldBe("text/html");
    }

    [Fact]
    public async Task GetCanvas_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();
        var result = await controller.GetCanvas("test-agent", "c_nonexistent", CancellationToken.None);
        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task PutCanvas_SavesHtml_Returns204()
    {
        var (controller, store) = CreateController();
        var result = await controller.PutCanvas("test-agent", TestConversationId.Value, "<p>x</p>", CancellationToken.None);
        result.ShouldBeOfType<NoContentResult>();
        var conversation = await store.GetAsync(TestConversationId);
        conversation!.CanvasHtml.ShouldBe("<p>x</p>");
    }

    [Fact]
    public async Task PutCanvas_EmptyHtml_ClearsToNull()
    {
        var (controller, store) = CreateController();
        var conversation = await store.GetAsync(TestConversationId);
        conversation!.CanvasHtml = "<p>x</p>";
        await store.SaveAsync(conversation);
        await controller.PutCanvas("test-agent", TestConversationId.Value, "", CancellationToken.None);
        var updated = await store.GetAsync(TestConversationId);
        updated!.CanvasHtml.ShouldBeNull();
    }

    [Fact]
    public async Task PutCanvas_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();
        var result = await controller.PutCanvas("test-agent", "c_nonexistent", "<p>x</p>", CancellationToken.None);
        result.ShouldBeOfType<NotFoundResult>();
    }

    // -- Canvas state CRUD -------------------------------------------------

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
    public async Task SetCanvasStateKey_NotifiesCanvasNotifiers()
    {
        var notifier = new RecordingCanvasNotifier();
        var (controller, _) = CreateController(notifier);
        var value = JsonDocument.Parse("7").RootElement.Clone();
        await controller.SetCanvasStateKey(TestConversationId.Value, "k", value, CancellationToken.None);
        notifier.Changes.Count.ShouldBe(1);
        notifier.Changes[0].Key.ShouldBe("k");
        notifier.Changes[0].Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteCanvasStateKey_ExistingKey_Returns204()
    {
        var (controller, store) = CreateController();
        var value = JsonDocument.Parse("\"temp\"").RootElement.Clone();
        await store.SetCanvasStateKeyAsync(TestConversationId, "temp", value);
        var result = await controller.DeleteCanvasStateKey(TestConversationId.Value, "temp", CancellationToken.None);
        result.ShouldBeOfType<NoContentResult>();
        var state = await store.GetCanvasStateAsync(TestConversationId);
        state.ShouldNotBeNull();
        state!.ContainsKey("temp").ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteCanvasStateKey_MissingKey_Returns204()
    {
        var (controller, _) = CreateController();
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

    [Fact]
    public async Task DeleteCanvasStateKey_NotifiesCanvasNotifiers_WithNullValue()
    {
        var notifier = new RecordingCanvasNotifier();
        var (controller, _) = CreateController(notifier);
        await controller.DeleteCanvasStateKey(TestConversationId.Value, "k", CancellationToken.None);
        notifier.Changes.Count.ShouldBe(1);
        notifier.Changes[0].Value.ShouldBeNull();
    }

    // -- Route contract (#1900) --------------------------------------------
    // The iframe canvas bridge (CanvasPanel.HandleCanvasMessage) and the documented REST contract
    // both target api/conversations/{id}/canvas-state/{key}. The #1732 extraction moved these
    // endpoints onto ConversationCanvasController with [Route("api/[controller]")], which the ASP.NET
    // token replacement expands to api/ConversationCanvas/... . That silently 404'd every iframe
    // write (the agent tool writes to the store directly, bypassing HTTP, so it kept working and
    // masked the break). These tests pin the composed routes to the contract so the regression
    // cannot recur.

    [Fact]
    public void Controller_base_route_is_api_conversations_not_token_expanded()
    {
        var routeAttr = typeof(ConversationCanvasController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single();

        // Must be the literal contract segment. "api/[controller]" expands to "api/ConversationCanvas",
        // which breaks the iframe bridge POST (#1900).
        routeAttr.Template.ShouldBe("api/conversations");
        routeAttr.Template.ShouldNotContain("[controller]");
    }

    [Theory]
    [InlineData(nameof(ConversationCanvasController.GetCanvasState), typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), "api/conversations/{conversationId}/canvas-state")]
    [InlineData(nameof(ConversationCanvasController.GetCanvasStateKey), typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), "api/conversations/{conversationId}/canvas-state/{key}")]
    [InlineData(nameof(ConversationCanvasController.SetCanvasStateKey), typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), "api/conversations/{conversationId}/canvas-state/{key}")]
    [InlineData(nameof(ConversationCanvasController.DeleteCanvasStateKey), typeof(Microsoft.AspNetCore.Mvc.HttpDeleteAttribute), "api/conversations/{conversationId}/canvas-state/{key}")]
    public void Canvas_state_endpoints_compose_to_the_contract_route(string methodName, Type verbAttributeType, string expectedRoute)
    {
        var routeAttr = typeof(ConversationCanvasController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single();

        var method = typeof(ConversationCanvasController).GetMethod(methodName)!;
        var verbAttr = method.GetCustomAttributes(verbAttributeType, inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider>()
            .Single();

        var composed = $"{routeAttr.Template}/{verbAttr.Template}";
        composed.ShouldBe(expectedRoute);
    }
    private static (ConversationCanvasController, InMemoryConversationStore) CreateController(IAgentCanvasNotifier? notifier = null)
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
        var notifiers = notifier is null ? null : new[] { notifier };
        var controller = new ConversationCanvasController(store, notifiers);
        return (controller, store);
    }

    private sealed class RecordingCanvasNotifier : IAgentCanvasNotifier
    {
        public List<(string Key, object? Value)> Changes { get; } = [];

        public Task NotifyCanvasUpdatedAsync(string agentId, string conversationId, string html, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCanvasStateChangedAsync(string conversationId, string key, object? value, CancellationToken cancellationToken = default)
        {
            Changes.Add((key, value));
            return Task.CompletedTask;
        }
    }
}
