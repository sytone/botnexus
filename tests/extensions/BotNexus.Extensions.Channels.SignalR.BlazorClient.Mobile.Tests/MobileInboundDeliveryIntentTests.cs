using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class MobileInboundDeliveryIntentTests : IDisposable
{
    private const string AgentId = "agent-1";
    private const string ConversationId = "conv-1";

    private readonly BunitContext _context = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interaction = Substitute.For<IAgentInteractionService>();

    public MobileInboundDeliveryIntentTests()
    {
        _store.UpsertAgent(new AgentState { AgentId = AgentId, DisplayName = "Agent 1", IsConnected = true });
        _store.SeedConversations(AgentId,
        [
            new ConversationSummaryDto(
                ConversationId, AgentId, "Test", false, "Active", "session-1", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        _store.SelectView(AgentId, ConversationId, SelectionSource.RouteNavigation);

        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsSignalRConnected.Returns(true);
        portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _context.Services.AddSingleton<IClientStateStore>(_store);
        _context.Services.AddSingleton(portalLoad);
        _context.Services.AddSingleton(_interaction);
        _context.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_interaction));
        _context.Services.AddSingleton(new MobileHubTuningOptions());
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _context.Dispose();

    [Theory]
    [InlineData(false, ".send-btn", InboundDeliveryMode.Auto)]
    [InlineData(true, "[data-testid='chat-steer-btn']", InboundDeliveryMode.Steer)]
    [InlineData(true, "[data-testid='chat-redirect-btn']", InboundDeliveryMode.Interrupt)]
    public async Task Mobile_emits_the_same_delivery_intents_as_desktop(
        bool turnActive,
        string buttonSelector,
        InboundDeliveryMode expectedMode)
    {
        _store.GetStreamState(ConversationId).IsRunActive = turnActive;
        var cut = _context.Render<Chat>(parameters => parameters
            .Add(component => component.AgentId, AgentId)
            .Add(component => component.ConversationId, ConversationId));

        await cut.InvokeAsync(() => cut.Find(".input-textarea").Input("same message"));
        await cut.InvokeAsync(() => cut.Find(buttonSelector).Click());

        await _interaction.Received(1).DeliverMessageAsync(
            AgentId, ConversationId, "same message", expectedMode, null);
    }
}
