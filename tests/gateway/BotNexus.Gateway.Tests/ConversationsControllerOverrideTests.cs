using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the per-conversation model / thinking / context override REST endpoints on
/// <see cref="ConversationsController"/> (PBI5, issue #1706). Covers the "override beats agent
/// default" (the value is persisted so the resolver's top layer reads it), "clears back to agent"
/// (DELETE nulls all three), and unknown-thinking-token rejection acceptance criteria at the API
/// boundary.
/// </summary>
public sealed class ConversationsControllerOverrideTests
{
    private const string AgentId = "test-agent";
    private static readonly ConversationId TestConversationId = ConversationId.From("c_override_test");

    [Fact]
    public async Task SetOverride_PersistsAllThreeFields()
    {
        var (controller, store) = CreateController();

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(Model: "claude-opus-4", Thinking: "high", ContextWindow: 128_000),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var persisted = (await store.GetAsync(TestConversationId))!;
        persisted.ModelOverride.ShouldBe("claude-opus-4");
        persisted.ThinkingOverride.ShouldBe("high");
        persisted.ContextWindowOverride.ShouldBe(128_000);
    }

    [Fact]
    public async Task SetOverride_UnknownThinkingToken_Returns400()
    {
        var (controller, _) = CreateController();

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(Thinking: "supersonic"),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SetOverride_NonPositiveContextWindow_Returns400()
    {
        var (controller, _) = CreateController();

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(ContextWindow: 0),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ClearOverride_RevertsAllFieldsToAgentDefault()
    {
        var (controller, store) = CreateController();
        var conversation = (await store.GetAsync(TestConversationId))!;
        conversation.ModelOverride = "claude-opus-4";
        conversation.ThinkingOverride = "max";
        conversation.ContextWindowOverride = 200_000;
        await store.SaveAsync(conversation);

        var result = await controller.ClearOverride(TestConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var cleared = (await store.GetAsync(TestConversationId))!;
        cleared.ModelOverride.ShouldBeNull();
        cleared.ThinkingOverride.ShouldBeNull();
        cleared.ContextWindowOverride.ShouldBeNull();
    }

    [Fact]
    public async Task SetOverride_NonExistentConversation_Returns404()
    {
        var (controller, _) = CreateController();

        var result = await controller.SetOverride(
            "c_nonexistent",
            new SetConversationOverrideRequest(Model: "m"),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    private static (ConversationsController, InMemoryConversationStore) CreateController()
    {
        var store = new InMemoryConversationStore();
        store.CreateAsync(new Conversation
        {
            ConversationId = TestConversationId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Title = "Override Test Conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var sessions = new InMemorySessionStore();
        // No ModelRegistry / IAgentRegistry supplied: capability validation is skipped, but the
        // token-shape and positivity guards at the API boundary still apply. This isolates the
        // persistence + clear behaviour from provider registration.
        var controller = new ConversationsController(store, sessions);
        return (controller, store);
    }
}
