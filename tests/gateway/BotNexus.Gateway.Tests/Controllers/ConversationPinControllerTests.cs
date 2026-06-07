using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Tests for conversation pin/unpin endpoints in <see cref="ConversationsController"/>.
/// </summary>
public sealed class ConversationPinControllerTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-pin-test");

    private static Conversation CreateConversation(string id)
        => new()
        {
            ConversationId = ConversationId.From(id),
            AgentId = TestAgent,
            Title = "Test",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ConversationsController CreateController(IConversationStore store)
        => new(store, new InMemorySessionStore());

    [Fact]
    public async Task Pin_Returns_NoContent_And_Updates_Store()
    {
        var store = new InMemoryConversationStore();
        var conversation = CreateConversation("conv-pin-1");
        await store.CreateAsync(conversation);

        var controller = CreateController(store);
        var result = await controller.Pin("conv-pin-1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();

        var loaded = await store.GetAsync(ConversationId.From("conv-pin-1"));
        loaded.ShouldNotBeNull();
        loaded!.IsPinned.ShouldBeTrue();
        loaded.PinnedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Pin_Nonexistent_Returns_NotFound()
    {
        var store = new InMemoryConversationStore();
        var controller = CreateController(store);

        var result = await controller.Pin("nonexistent", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Unpin_Returns_NoContent_And_Clears_Pin()
    {
        var store = new InMemoryConversationStore();
        var conversation = CreateConversation("conv-unpin-1");
        await store.CreateAsync(conversation);
        await store.PinAsync(ConversationId.From("conv-unpin-1"), true);

        var controller = CreateController(store);
        var result = await controller.Unpin("conv-unpin-1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();

        var loaded = await store.GetAsync(ConversationId.From("conv-unpin-1"));
        loaded.ShouldNotBeNull();
        loaded!.IsPinned.ShouldBeFalse();
        loaded.PinnedAt.ShouldBeNull();
    }

    [Fact]
    public async Task List_Orders_Pinned_First()
    {
        var store = new InMemoryConversationStore();

        var older = new Conversation
        {
            ConversationId = ConversationId.From("conv-older"),
            AgentId = TestAgent,
            Title = "Older pinned",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10)
        };
        await store.CreateAsync(older);
        await store.PinAsync(ConversationId.From("conv-older"), true);

        var newer = new Conversation
        {
            ConversationId = ConversationId.From("conv-newer"),
            AgentId = TestAgent,
            Title = "Newer unpinned",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.CreateAsync(newer);

        var controller = CreateController(store);
        var result = await controller.List(TestAgent.Value, CancellationToken.None);

        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var summaries = okResult.Value.ShouldBeAssignableTo<List<ConversationSummary>>();
        summaries.ShouldNotBeNull();
        summaries!.Count.ShouldBe(2);
        summaries[0].ConversationId.ShouldBe("conv-older");
        summaries[0].IsPinned.ShouldBeTrue();
        summaries[1].ConversationId.ShouldBe("conv-newer");
        summaries[1].IsPinned.ShouldBeFalse();
    }
}
