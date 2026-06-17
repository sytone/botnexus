using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the pending <c>ask_user</c> REST endpoint on <see cref="ConversationsController"/>
/// (#1488, ask_user durability Step 1/5). A reloaded, newly-opened, or mobile client that missed
/// the live <c>UserInputRequired</c> event hydrates the inline prompt from this endpoint, so it must
/// return the persisted payload, 204 when nothing is pending, and 404 when the conversation is unknown.
/// </summary>
public sealed class ConversationsControllerPendingAskUserTests
{
    private const string AgentId = "test-agent";
    private static readonly ConversationId TestConversationId = ConversationId.From("c_ask_test");

    private const string PersistedPrompt =
        "{\"requestId\":\"req-1\",\"conversationId\":\"c_ask_test\",\"prompt\":\"Continue?\",\"inputType\":\"FreeForm\"}";

    [Fact]
    public async Task GetPendingAskUser_WhenPromptPending_ReturnsPayload()
    {
        var (controller, store) = CreateController();
        var conversation = (await store.GetAsync(TestConversationId))!;
        conversation.PendingAskUserJson = PersistedPrompt;
        await store.SaveAsync(conversation);

        var result = await controller.GetPendingAskUser(AgentId, TestConversationId.Value, CancellationToken.None);

        var content = result.ShouldBeOfType<ContentResult>();
        content.Content.ShouldBe(PersistedPrompt);
        content.ContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task GetPendingAskUser_WhenNonePending_Returns204()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetPendingAskUser(AgentId, TestConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetPendingAskUser_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.GetPendingAskUser(AgentId, "c_nonexistent", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    private static (ConversationsController, InMemoryConversationStore) CreateController()
    {
        var store = new InMemoryConversationStore();
        store.CreateAsync(new Conversation
        {
            ConversationId = TestConversationId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Title = "Test Ask Conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var sessions = new InMemorySessionStore();
        var controller = new ConversationsController(store, sessions);
        return (controller, store);
    }
}
