using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Tests for the move binding endpoint in <see cref="ConversationsController"/>.
/// </summary>
public sealed class ConversationMoveBindingTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-move-test");

    private static Conversation CreateConversation(string id, ChannelBinding? binding = null)
    {
        var conv = new Conversation
        {
            ConversationId = ConversationId.From(id),
            AgentId = TestAgent,
            Title = "Test",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (binding is not null)
            conv.ChannelBindings.Add(binding);
        return conv;
    }

    private static ChannelBinding CreateBinding(string id = "binding-1")
        => new()
        {
            BindingId = BindingId.From(id),
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("1234567890"),
            Mode = BindingMode.Interactive,
            BoundAt = DateTimeOffset.UtcNow
        };

    private static ConversationsController CreateController(IConversationStore store)
        => new(store, new InMemorySessionStore());

    [Fact]
    public async Task MoveBinding_Success_RemovesFromSourceAndAddsToTarget()
    {
        var store = new InMemoryConversationStore();
        var binding = CreateBinding();
        var source = CreateConversation("conv-source", binding);
        var target = CreateConversation("conv-target");
        await store.CreateAsync(source);
        await store.CreateAsync(target);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "conv-source",
            "binding-1",
            new MoveBindingRequest("conv-target"),
            CancellationToken.None);

        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var response = okResult.Value.ShouldBeOfType<BindingResponse>();
        response.BindingId.ShouldBe("binding-1");
        response.ChannelType.ShouldBe("telegram");

        // Verify source no longer has binding
        var loadedSource = await store.GetAsync(ConversationId.From("conv-source"));
        loadedSource!.ChannelBindings.ShouldBeEmpty();

        // Verify target has the binding
        var loadedTarget = await store.GetAsync(ConversationId.From("conv-target"));
        loadedTarget!.ChannelBindings.Count.ShouldBe(1);
        loadedTarget.ChannelBindings[0].BindingId.Value.ShouldBe("binding-1");
    }

    [Fact]
    public async Task MoveBinding_SourceNotFound_Returns404()
    {
        var store = new InMemoryConversationStore();
        var target = CreateConversation("conv-target");
        await store.CreateAsync(target);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "nonexistent",
            "binding-1",
            new MoveBindingRequest("conv-target"),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_BindingNotFoundOnSource_Returns404()
    {
        var store = new InMemoryConversationStore();
        var source = CreateConversation("conv-source");
        var target = CreateConversation("conv-target");
        await store.CreateAsync(source);
        await store.CreateAsync(target);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "conv-source",
            "nonexistent-binding",
            new MoveBindingRequest("conv-target"),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_TargetNotFound_Returns404()
    {
        var store = new InMemoryConversationStore();
        var binding = CreateBinding();
        var source = CreateConversation("conv-source", binding);
        await store.CreateAsync(source);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "conv-source",
            "binding-1",
            new MoveBindingRequest("nonexistent-target"),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();

        // Verify source still has binding (not removed on failed move)
        var loadedSource = await store.GetAsync(ConversationId.From("conv-source"));
        loadedSource!.ChannelBindings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MoveBinding_SameSourceAndTarget_Returns400()
    {
        var store = new InMemoryConversationStore();
        var binding = CreateBinding();
        var source = CreateConversation("conv-source", binding);
        await store.CreateAsync(source);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "conv-source",
            "binding-1",
            new MoveBindingRequest("conv-source"),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_EmptyTargetConversationId_Returns400()
    {
        var store = new InMemoryConversationStore();
        var binding = CreateBinding();
        var source = CreateConversation("conv-source", binding);
        await store.CreateAsync(source);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "conv-source",
            "binding-1",
            new MoveBindingRequest(""),
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MoveBinding_PreservesBindingProperties()
    {
        var store = new InMemoryConversationStore();
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("binding-props"),
            ChannelType = ChannelKey.From("teams"),
            ChannelAddress = ChannelAddress.From("channel/thread:abc"),
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Prefix,
            DisplayPrefix = "[Portal]",
            BoundAt = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var source = CreateConversation("conv-source", binding);
        var target = CreateConversation("conv-target");
        await store.CreateAsync(source);
        await store.CreateAsync(target);

        var controller = CreateController(store);
        var result = await controller.MoveBinding(
            "conv-source",
            "binding-props",
            new MoveBindingRequest("conv-target"),
            CancellationToken.None);

        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var response = okResult.Value.ShouldBeOfType<BindingResponse>();
        response.BindingId.ShouldBe("binding-props");
        response.ChannelType.ShouldBe("teams");
        response.ChannelAddress.ShouldBe("channel/thread:abc");
        response.Mode.ShouldBe("Interactive");
        response.ThreadingMode.ShouldBe("Prefix");
        response.DisplayPrefix.ShouldBe("[Portal]");
    }
}
