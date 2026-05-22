using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationsControllerMoveBindingTests
{
    private static Conversation CreateConversation(string conversationId, string agentId = "agent1")
        => new()
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = AgentId.From(agentId),
            Title = "Test",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ChannelBinding CreateBinding(string bindingId)
        => new()
        {
            BindingId = BindingId.From(bindingId),
            ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("addr-1"),
            Mode = BindingMode.Interactive,
            ThreadingMode = ThreadingMode.Single,
            BoundAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task MoveBinding_HappyPath_RemovesFromSourceAndAddsToTarget()
    {
        var binding = CreateBinding("b_move_1");
        var source = CreateConversation("c_move_source");
        source.ChannelBindings.Add(binding);
        var target = CreateConversation("c_move_target");

        var store = new MultiConversationStub(source, target);
        var controller = new ConversationsController(store, new InMemorySessionStore());

        var result = await controller.MoveBinding(
            "c_move_source", "b_move_1", new MoveBindingRequest("c_move_target"), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        source.ChannelBindings.ShouldBeEmpty();
        target.ChannelBindings.ShouldContain(b => b.BindingId == binding.BindingId);
    }

    [Fact]
    public async Task MoveBinding_SameConversation_ReturnsNoContent_BindingUnchanged()
    {
        var binding = CreateBinding("b_move_same");
        var conv = CreateConversation("c_move_same");
        conv.ChannelBindings.Add(binding);

        var store = new MultiConversationStub(conv);
        var controller = new ConversationsController(store, new InMemorySessionStore());

        var result = await controller.MoveBinding(
            "c_move_same", "b_move_same", new MoveBindingRequest("c_move_same"), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        conv.ChannelBindings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MoveBinding_SourceNotFound_Returns404()
    {
        var store = new MultiConversationStub();
        var controller = new ConversationsController(store, new InMemorySessionStore());

        var result = await controller.MoveBinding(
            "c_nonexistent", "b_any", new MoveBindingRequest("c_target"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MoveBinding_BindingNotFound_Returns404()
    {
        var source = CreateConversation("c_move_no_binding");

        var store = new MultiConversationStub(source);
        var controller = new ConversationsController(store, new InMemorySessionStore());

        var result = await controller.MoveBinding(
            "c_move_no_binding", "b_nonexistent", new MoveBindingRequest("c_target"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MoveBinding_TargetNotFound_Returns404()
    {
        var binding = CreateBinding("b_move_notarget");
        var source = CreateConversation("c_move_no_target");
        source.ChannelBindings.Add(binding);

        var store = new MultiConversationStub(source);
        var controller = new ConversationsController(store, new InMemorySessionStore());

        var result = await controller.MoveBinding(
            "c_move_no_target", "b_move_notarget", new MoveBindingRequest("c_nonexistent_target"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MoveBinding_EmptyTargetConversationId_Returns400()
    {
        var source = CreateConversation("c_move_empty_target");

        var store = new MultiConversationStub(source);
        var controller = new ConversationsController(store, new InMemorySessionStore());

        var result = await controller.MoveBinding(
            "c_move_empty_target", "b_any", new MoveBindingRequest(""), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    /// <summary>Simple in-memory store supporting lookup of multiple conversations by ID.</summary>
    private sealed class MultiConversationStub : IConversationStore
    {
        private readonly Dictionary<ConversationId, Conversation> _store;

        public MultiConversationStub(params Conversation[] conversations)
        {
            _store = conversations.ToDictionary(c => c.ConversationId);
        }

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(_store.GetValueOrDefault(conversationId));

        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Conversation>>(_store.Values.ToList());

        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
        {
            _store[conversation.ConversationId] = conversation;
            return Task.FromResult(conversation);
        }

        public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
        {
            _store[conversation.ConversationId] = conversation;
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, ThreadId? threadId, CancellationToken ct = default)
            => Task.FromResult<Conversation?>(null);
    }
}
