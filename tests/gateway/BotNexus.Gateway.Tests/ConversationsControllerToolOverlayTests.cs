using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the per-session tool overlay carried on the EXISTING override endpoint (issue #3271,
/// portal half of #2523).
/// </summary>
/// <remarks>
/// The load-bearing property here is the opt-in gate. The other three fields on
/// <see cref="SetConversationOverrideRequest"/> are applied unconditionally, so if the new field
/// followed that convention every pre-existing model-only caller would silently CLEAR an overlay it
/// never mentioned. <c>ApplyToolOverride</c> makes an existing payload provably non-destructive, and
/// <see cref="SetOverride_WithoutOptIn_LeavesAnExistingOverlayIntact"/> is what pins it.
/// </remarks>
public sealed class ConversationsControllerToolOverlayTests
{
    private const string AgentId = "test-agent";
    private static readonly ConversationId TestConversationId = ConversationId.From("c_tool_overlay_test");

    [Fact]
    public async Task SetOverride_WithOptIn_PersistsTheOverlay()
    {
        var (controller, store) = CreateController();
        var overlay = new SessionToolOverride { EnabledTools = ["read", "write"] }.ToJson();

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(ToolOverrideJson: overlay, ApplyToolOverride: true),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var persisted = (await store.GetAsync(TestConversationId))!;
        var round = SessionToolOverride.FromJson(persisted.ToolOverrideJson);
        round.ShouldNotBeNull();
        round!.EnabledTools.ShouldBe(["read", "write"]);
    }

    [Fact]
    public async Task SetOverride_WithOptInAndNullJson_ClearsTheOverlay()
    {
        var (controller, store) = CreateController();
        var conversation = (await store.GetAsync(TestConversationId))!;
        conversation.ToolOverrideJson = new SessionToolOverride { DisabledTools = ["exec"] }.ToJson();
        await store.SaveAsync(conversation);

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(ToolOverrideJson: null, ApplyToolOverride: true),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        (await store.GetAsync(TestConversationId))!.ToolOverrideJson.ShouldBeNull();
    }

    [Fact]
    public async Task SetOverride_WithoutOptIn_LeavesAnExistingOverlayIntact()
    {
        // The regression this exists to prevent: a model-only write clobbering a tool restriction.
        var (controller, store) = CreateController();
        var overlay = new SessionToolOverride { DisabledTools = ["exec"] }.ToJson();
        var conversation = (await store.GetAsync(TestConversationId))!;
        conversation.ToolOverrideJson = overlay;
        await store.SaveAsync(conversation);

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(Model: "claude-opus-4"),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var persisted = (await store.GetAsync(TestConversationId))!;
        persisted.ToolOverrideJson.ShouldBe(overlay);
        persisted.ModelOverride.ShouldBe("claude-opus-4");
    }

    [Fact]
    public async Task SetOverride_UnreadableOverlay_Returns400RatherThanStoringIt()
    {
        // SessionToolOverride.FromJson fails OPEN, so a corrupt overlay that reached the column
        // would resolve to "no restriction" while the operator believed one was in force. Reject it
        // at the boundary instead.
        var (controller, store) = CreateController();

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(ToolOverrideJson: "{ not json", ApplyToolOverride: true),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        (await store.GetAsync(TestConversationId))!.ToolOverrideJson.ShouldBeNull();
    }

    [Fact]
    public async Task ClearOverride_AlsoClearsTheToolOverlay()
    {
        var (controller, store) = CreateController();
        var conversation = (await store.GetAsync(TestConversationId))!;
        conversation.ToolOverrideJson = new SessionToolOverride { EnabledTools = ["read"] }.ToJson();
        await store.SaveAsync(conversation);

        var result = await controller.ClearOverride(TestConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        (await store.GetAsync(TestConversationId))!.ToolOverrideJson.ShouldBeNull();
    }

    [Fact]
    public async Task SetOverride_EmitsTheOverlayOnTheResponse()
    {
        // AC1 depends on this: the portal renders current state from the conversation payload, so
        // an overlay the response omits is one the operator cannot see without running /tools.
        var (controller, _) = CreateController();
        var overlay = new SessionToolOverride { EnabledTools = ["read"] }.ToJson();

        var result = await controller.SetOverride(
            TestConversationId.Value,
            new SetConversationOverrideRequest(ToolOverrideJson: overlay, ApplyToolOverride: true),
            CancellationToken.None);

        var body = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ConversationResponse>();
        body.ToolOverrideJson.ShouldBe(overlay);
    }

    private static (ConversationsController, InMemoryConversationStore) CreateController()
    {
        var store = new InMemoryConversationStore();
        store.CreateAsync(new Conversation
        {
            ConversationId = TestConversationId,
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentId),
            Title = "Tool Overlay Test Conversation",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var controller = new ConversationsController(store, new InMemorySessionStore());
        return (controller, store);
    }
}
