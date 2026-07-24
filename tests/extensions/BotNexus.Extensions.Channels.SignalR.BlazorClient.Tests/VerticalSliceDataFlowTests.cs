using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Vertical slice integration tests that render parent components and verify
/// end-to-end data flow through to child components. These catch wiring bugs
/// where child parameters are missing or stale after parent state changes.
/// </summary>
public sealed class VerticalSliceDataFlowTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IAgentInteractionService _interactionService = Substitute.For<IAgentInteractionService>();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly IPortalPreferencesService _prefs = Substitute.For<IPortalPreferencesService>();

    public VerticalSliceDataFlowTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _prefs.Current.Returns(new PortalPreferences());

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(_interactionService);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(_prefs);
        _ctx.Services.AddSingleton(_restClient);
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Validates that when Home is rendered with an agent and conversation, the interaction
    /// service's SelectConversationAsync is called to load history. This tests the
    /// Home → AgentPanel → ChatPanel data flow path.
    /// </summary>
    [Fact]
    public void Home_with_non_active_conversation_calls_SelectConversationAsync()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Agent One")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "Default Conv",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            new ConversationSummaryDto(
                ConversationId: "conv-2",
                AgentId: "agent-1",
                Title: "Other Conv",
                IsDefault: false,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        _interactionService.SelectConversationAsync("agent-1", "conv-2")
            .Returns(Task.CompletedTask);

        // Navigate to conv-2 (not the default/active one)
        var cut = _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-2"));

        // SelectConversationAsync should be called because conv-2 != active (conv-1)
        cut.WaitForAssertion(() =>
            _interactionService.Received(1).SelectConversationAsync("agent-1", "conv-2"));
    }

    /// <summary>
    /// Validates that the ChatPanel child component receives and renders correctly when
    /// the parent AgentPanel passes the AgentId through. Tests the data binding contract.
    /// </summary>
    [Fact]
    public void AgentPanel_passes_AgentId_to_ChatPanel_child()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Agent One")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "Test Conv",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-1"));

        // The chat panel should be rendered inside the agent panel's conversation tab
        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find("[data-testid='agent-panel-conversation'] .chat-panel");
            Assert.NotNull(panel);
        });
    }

    /// <summary>
    /// When store state changes (e.g., new conversation added), the rendered tree should
    /// update without crashing. Tests reactive data flow from store → parent → children.
    /// </summary>
    [Fact]
    public void Store_change_triggers_rerender_without_crash()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Agent One")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "Test Conv",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-1"));

        // Verify initial render
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='agent-panel']")));

        // Mutate store — add a second conversation
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "Test Conv",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            new ConversationSummaryDto(
                ConversationId: "conv-2",
                AgentId: "agent-1",
                Title: "Second Conv",
                IsDefault: false,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
        ]);
        _store.NotifyChanged();

        // Panel should still render correctly after state mutation
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='agent-panel']")));
    }

    /// <summary>
    /// When Home renders without a ConversationId parameter, it should still render
    /// the agent panel (conversation tab) without crashing. Tests null-safety in
    /// the Home → ApplyRouteSelectionAsync → AgentPanel chain.
    /// </summary>
    [Fact]
    public void Home_without_conversation_id_renders_panel()
    {
        _store.SeedAgents([new AgentSummary("agent-1", "Agent One")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "Test Conv",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
        ]);
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1"));

        // Panel should render even without explicit conversation ID
        cut.WaitForAssertion(() =>
            Assert.NotNull(cut.Find("[data-testid='agent-panel']")));
    }
}