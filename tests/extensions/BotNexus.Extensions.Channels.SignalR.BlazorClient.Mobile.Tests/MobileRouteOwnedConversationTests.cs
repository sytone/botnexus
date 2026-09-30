using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class MobileRouteOwnedConversationTests : IDisposable
{
    private const string AgentId = "agent-1";
    private const string RoutedConversationId = "routed";
    private const string RecentConversationId = "recent";

    private readonly BunitContext _context = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public MobileRouteOwnedConversationTests()
    {
        _store.UpsertAgent(new AgentState { AgentId = AgentId, DisplayName = "Agent 1", IsConnected = true });
        _store.SeedConversations(AgentId,
        [
            new ConversationSummaryDto(
                RoutedConversationId, AgentId, "Routed", false, "Active", "session-routed", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ConversationSummaryDto(
                RecentConversationId, AgentId, "Recent", false, "Active", "session-recent", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView(AgentId, RoutedConversationId, SelectionSource.RouteNavigation);
        _store.GetAgent(AgentId)!.ActiveConversationId = RecentConversationId;
        _store.AppendMessage(RoutedConversationId, new ChatMessage("assistant", "routed transcript", DateTimeOffset.UtcNow));
        _store.AppendMessage(RecentConversationId, new ChatMessage("assistant", "recent transcript", DateTimeOffset.UtcNow));

        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsSignalRConnected.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _context.Services.AddSingleton<IClientStateStore>(_store);
        _context.Services.AddSingleton(portalLoad);
        _context.Services.AddSingleton(_interaction);
        _context.Services.AddSingleton(new MobileHubTuningOptions());
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task Deep_link_renders_and_sends_to_the_routed_conversation()
    {
        var cut = _context.Render<Chat>(parameters => parameters
            .Add(component => component.AgentId, AgentId)
            .Add(component => component.ConversationId, RoutedConversationId));

        cut.Markup.ShouldContain("routed transcript");
        cut.Markup.ShouldNotContain("recent transcript");

        await cut.InvokeAsync(() => cut.Find(".input-textarea").Input("route owned"));
        await cut.InvokeAsync(() => cut.Find(".send-btn").Click());

        await _interaction.Received(1).DeliverMessageAsync(
            AgentId, RoutedConversationId, "route owned", InboundDeliveryMode.Auto,
            Arg.Is<IReadOnlyList<DraftAttachment>>(attachments => attachments.Count == 0));
        await _interaction.DidNotReceive().DeliverMessageAsync(
            AgentId, RecentConversationId, Arg.Any<string>(), Arg.Any<InboundDeliveryMode>(), Arg.Any<IReadOnlyList<DraftAttachment>?>());
    }
}
