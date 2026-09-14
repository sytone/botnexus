using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #3326: desktop and mobile state the same delivery intent for the same composer action.
/// The interaction service, not either component, owns the transport used to carry that intent.
/// </summary>
public sealed class InboundDeliveryIntentParityTests : IDisposable
{
    private const string AgentId = "agent-1";
    private const string ConversationId = "conv-1";

    private readonly BunitContext _desktop = new();
    private readonly ClientStateStore _desktopStore = new();
    private readonly IAgentInteractionService _desktopInteraction = Substitute.For<IAgentInteractionService>();

    public InboundDeliveryIntentParityTests()
    {
        ConfigureDesktop();
    }

    public void Dispose()
    {
        _desktop.Dispose();
    }

    [Theory]
    [InlineData(false, ".send-btn", InboundDeliveryMode.Auto)]
    [InlineData(true, "[data-testid='chat-steer-btn']", InboundDeliveryMode.Steer)]
    [InlineData(true, "[data-testid='chat-redirect-btn']", InboundDeliveryMode.Interrupt)]
    public async Task Desktop_emits_the_intent_named_by_the_composer_action(
        bool turnActive,
        string buttonSelector,
        InboundDeliveryMode expectedMode)
    {
        _desktopStore.GetStreamState(ConversationId).IsRunActive = turnActive;

        var desktop = _desktop.Render<ChatPanel>(parameters => parameters
            .Add(component => component.AgentId, AgentId)
            .Add(component => component.ConversationId, ConversationId));

        await desktop.InvokeAsync(() => desktop.Find(".chat-input").Input("same message"));
        await desktop.InvokeAsync(() => desktop.Find(buttonSelector).Click());

        await _desktopInteraction.Received(1).DeliverMessageAsync(
            AgentId, ConversationId, "same message", expectedMode, Arg.Any<IReadOnlyList<DraftAttachment>>());
    }

    private void ConfigureDesktop()
    {
        Seed(_desktopStore);
        _desktop.Services.AddSingleton<IClientStateStore>(_desktopStore);
        _desktop.Services.AddSingleton(_desktopInteraction);
        _desktop.Services.AddSingleton<ISlashCommandDispatcher>(new SlashCommandDispatcher(_desktopInteraction));
        _desktop.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        var preferences = Substitute.For<IPortalPreferencesService>();
        preferences.Current.Returns(new PortalPreferences());
        _desktop.Services.AddSingleton(preferences);
        _desktop.Services.AddSingleton(new HttpClient());
        _desktop.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static void Seed(ClientStateStore store)
    {
        store.UpsertAgent(new AgentState
        {
            AgentId = AgentId,
            DisplayName = "Agent 1",
            IsConnected = true
        });
        store.SeedConversations(AgentId,
        [
            new ConversationSummaryDto(
                ConversationId, AgentId, "Test", false, "Active", "session-1", 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        store.SetActiveConversation(AgentId, ConversationId);
    }

}
