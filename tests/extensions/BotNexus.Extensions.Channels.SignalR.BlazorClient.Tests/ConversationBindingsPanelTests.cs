using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class ConversationBindingsPanelTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IGatewayRestClient _rest = Substitute.For<IGatewayRestClient>();

    public ConversationBindingsPanelTests()
    {
        _ctx.Services.AddSingleton(_rest);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Empty_conversation_renders_add_action_and_empty_state()
    {
        _rest.GetConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Conversation("c1", []));

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-testid='binding-empty']"));
        cut.Find("[data-testid='binding-add-open']");
    }

    [Fact]
    public void Existing_bindings_render_the_composite_address_without_thread_id()
    {
        _rest.GetConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Conversation("c1", [Binding("b1", "telegram", "chat:42/thread:7")]));

        var cut = Render();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-binding-id='b1']").TextContent.ShouldContain("telegram");
            cut.Find("[data-binding-id='b1']").TextContent.ShouldContain("chat:42/thread:7");
            cut.Markup.ShouldNotContain("Thread ID");
        });
    }

    [Fact]
    public async Task Add_posts_trimmed_binding_and_refreshes_the_list()
    {
        _rest.GetConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(
                Conversation("c1", []),
                Conversation("c1", [Binding("b1", "telegram", "chat-42")]));
        _rest.AddConversationBindingAsync("c1", Arg.Any<AddConversationBindingRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(Binding("b1", "telegram", "chat-42"));

        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-testid='binding-add-open']"));
        await cut.Find("[data-testid='binding-add-open']").ClickAsync(new());
        await cut.Find("[data-testid='binding-channel-type']").ChangeAsync(new() { Value = " telegram " });
        await cut.Find("[data-testid='binding-channel-address']").ChangeAsync(new() { Value = " chat-42 " });
        await cut.Find("[data-testid='binding-add-save']").ClickAsync(new());

        await _rest.Received(1).AddConversationBindingAsync(
            "c1",
            Arg.Is<AddConversationBindingRequestDto>(request =>
                request.ChannelType == "telegram" &&
                request.ChannelAddress == "chat-42" &&
                request.Mode == "Interactive" &&
                request.ThreadingMode == "Single"),
            Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => cut.Find("[data-binding-id='b1']"));
    }

    [Fact]
    public async Task Remove_requires_confirmation_then_calls_delete()
    {
        _rest.GetConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Conversation("c1", [Binding("b1", "signalr", "portal")]));
        _rest.RemoveConversationBindingAsync("c1", "b1", Arg.Any<CancellationToken>())
            .Returns(true);

        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-testid='binding-remove-b1']"));
        await cut.Find("[data-testid='binding-remove-b1']").ClickAsync(new());

        cut.Find("[data-testid='binding-remove-confirm']");
        await cut.Find("[data-testid='binding-remove-confirm']").ClickAsync(new());

        await _rest.Received(1).RemoveConversationBindingAsync("c1", "b1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_offers_other_conversations_and_calls_move_endpoint()
    {
        _rest.GetConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Conversation("c1", [Binding("b1", "telegram", "chat-42")]));
        _rest.MoveConversationBindingAsync("c1", "b1", Arg.Any<MoveConversationBindingRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(Binding("b1", "telegram", "chat-42"));

        var cut = Render([new ConversationBindingTargetDto("c2", "Long-running conversation")]);
        cut.WaitForAssertion(() => cut.Find("[data-testid='binding-move-b1']"));
        await cut.Find("[data-testid='binding-move-b1']").ClickAsync(new());
        await cut.Find("[data-testid='binding-move-target']").ChangeAsync(new() { Value = "c2" });
        await cut.Find("[data-testid='binding-move-confirm']").ClickAsync(new());

        await _rest.Received(1).MoveConversationBindingAsync(
            "c1",
            "b1",
            Arg.Is<MoveConversationBindingRequestDto>(request => request.TargetConversationId == "c2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Load_failure_is_visible_and_retryable()
    {
        _rest.GetConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns<Task<ConversationResponseDto?>>(_ => throw new HttpRequestException("offline"));

        var cut = Render();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role='alert']").TextContent.ShouldContain("offline");
            cut.Find("[data-testid='binding-retry']");
        });
    }

    private IRenderedComponent<ConversationBindingsPanel> Render(
        IReadOnlyList<ConversationBindingTargetDto>? targets = null)
        => _ctx.Render<ConversationBindingsPanel>(parameters => parameters
            .Add(component => component.ConversationId, "c1")
            .Add(component => component.MoveTargets, targets ?? []));

    private static ConversationResponseDto Conversation(
        string conversationId,
        IReadOnlyList<ConversationBindingDto> bindings)
        => new(
            conversationId,
            "agent-1",
            "Conversation",
            false,
            "Active",
            null,
            bindings,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static ConversationBindingDto Binding(
        string bindingId,
        string channelType,
        string address)
        => new(
            bindingId,
            channelType,
            address,
            "Interactive",
            "Single",
            null,
            DateTimeOffset.UtcNow);
}
