using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

public sealed class CanvasPersistenceTests
{
    [Fact]
    public async Task CanvasHtml_IsNullByDefault_OnCreate()
    {
        using var fixture = new StoreFixture();
        var conversation = MakeConversation("agent-a");
        await fixture.CreateStore().CreateAsync(conversation);
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.CanvasHtml.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_PersistsCanvasHtml()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = MakeConversation("agent-a");
        await store.CreateAsync(conversation);
        conversation.CanvasHtml = "<h1>Hello</h1>";
        await store.SaveAsync(conversation);
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.CanvasHtml.ShouldBe("<h1>Hello</h1>");
    }

    [Fact]
    public async Task SaveAsync_CanClearCanvasHtml()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = MakeConversation("agent-a");
        conversation.CanvasHtml = "<h1>old</h1>";
        await store.CreateAsync(conversation);
        conversation.CanvasHtml = null;
        await store.SaveAsync(conversation);
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.CanvasHtml.ShouldBeNull();
    }

    [Fact]
    public async Task CanvasHtml_SurvivesGatewayRestart()
    {
        using var fixture = new StoreFixture();
        var write = fixture.CreateStore();
        var conversation = MakeConversation("agent-a");
        await write.CreateAsync(conversation);
        conversation.CanvasHtml = "<p>persisted</p>";
        await write.SaveAsync(conversation);
        var read = fixture.CreateStore();
        var loaded = await read.GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.CanvasHtml.ShouldBe("<p>persisted</p>");
    }

    [Fact]
    public async Task ConversationCanvasNotifier_SavesHtmlToConversation()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = MakeConversation("agent-a");
        await store.CreateAsync(conversation);
        var notifier = new ConversationCanvasNotifier(store);
        await notifier.NotifyCanvasUpdatedAsync("agent-a", conversation.ConversationId.Value, "<h1>live</h1>");
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.CanvasHtml.ShouldBe("<h1>live</h1>");
    }

    [Fact]
    public async Task ConversationCanvasNotifier_ClearsHtml_WhenEmptyString()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversation = MakeConversation("agent-a");
        conversation.CanvasHtml = "<h1>old</h1>";
        await store.CreateAsync(conversation);
        var notifier = new ConversationCanvasNotifier(store);
        await notifier.NotifyCanvasUpdatedAsync("agent-a", conversation.ConversationId.Value, string.Empty);
        var loaded = await fixture.CreateStore().GetAsync(conversation.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.CanvasHtml.ShouldBeNull();
    }

    [Fact]
    public async Task ConversationCanvasNotifier_NoOp_WhenConversationNotFound()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var notifier = new ConversationCanvasNotifier(store);
        await notifier.NotifyCanvasUpdatedAsync("agent-a", "nonexistent-conv-id", "<h1>html</h1>");
    }

    private static Conversation MakeConversation(string agentId) => new()
    {
        ConversationId = ConversationId.From(Guid.NewGuid().ToString()),
        AgentId = AgentId.From(agentId),
        Title = "test conv"
    };
}