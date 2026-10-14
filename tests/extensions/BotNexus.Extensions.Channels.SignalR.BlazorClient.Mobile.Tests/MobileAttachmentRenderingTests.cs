using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class MobileAttachmentRenderingTests : IDisposable
{
    private readonly BunitContext _context = new();
    private readonly ClientStateStore _store = new();

    public MobileAttachmentRenderingTests()
    {
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _context.Services.AddSingleton<IClientStateStore>(_store);
        _context.Services.AddSingleton(portalLoad);
        _context.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _context.Services.AddSingleton(new MobileHubTuningOptions());
        _context.JSInterop.Mode = JSRuntimeMode.Loose;

        _store.UpsertAgent(new AgentState
        {
            AgentId = "agent",
            DisplayName = "Agent",
            IsConnected = true
        });
        _store.SeedConversations("agent",
        [
            new ConversationSummaryDto(
                "conversation",
                "agent",
                "Conversation",
                false,
                "Active",
                null,
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        ]);
        _store.SelectView("agent", "conversation", SelectionSource.RouteNavigation);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void User_and_assistant_rows_render_through_the_shared_attachment_component()
    {
        var attachment = new ChatAttachment("file.txt", "text/plain", 4, "dGVzdA==");
        var conversation = _store.GetAgent("agent")?.Conversations["conversation"];
        conversation.ShouldNotBeNull();
        conversation.AppendMessage(new ChatMessage("User", "user", DateTimeOffset.UtcNow)
        {
            Attachments = [attachment]
        });
        conversation.AppendMessage(new ChatMessage("Assistant", "assistant", DateTimeOffset.UtcNow)
        {
            Attachments = [attachment]
        });

        var cut = RenderChat();

        var attachmentLists = cut.FindComponents<AttachmentList>();
        attachmentLists.Count.ShouldBe(2);
        attachmentLists.ShouldAllBe(component => component.Instance.Attachments.ShouldHaveSingleItem() == attachment);
    }

    [Fact]
    public void Shared_attachment_component_owns_mobile_lightbox_backdrop_and_escape()
    {
        var conversation = _store.GetAgent("agent")?.Conversations["conversation"];
        conversation.ShouldNotBeNull();
        conversation.AppendMessage(new ChatMessage("User", "photo", DateTimeOffset.UtcNow)
        {
            Attachments = [new ChatAttachment("photo.png", "image/png", 3, "AQID")]
        });
        var attachmentList = RenderChat().FindComponent<AttachmentList>();

        attachmentList.Find("[data-testid='attachment-image-open']").Click();
        attachmentList.Find("[data-testid='attachment-lightbox']").Click();
        attachmentList.FindAll("[data-testid='attachment-lightbox']").ShouldBeEmpty();

        attachmentList.Find("[data-testid='attachment-image-open']").Click();
        attachmentList.Find("[data-testid='attachment-lightbox']").KeyDown("Escape");
        attachmentList.FindAll("[data-testid='attachment-lightbox']").ShouldBeEmpty();
    }

    private IRenderedComponent<Chat> RenderChat() =>
        _context.Render<Chat>(parameters => parameters
            .Add(component => component.AgentId, "agent")
            .Add(component => component.ConversationId, "conversation"));
}
