using Bunit;
using Bunit.TestDoubles;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Probe Round 2 bUnit tests covering interaction service call verification,
/// session confirmation dialog, and conversation routing.
/// </summary>
public sealed class ProbeRound2ComponentTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store;
    private readonly IAgentInteractionService _interaction;

    public ProbeRound2ComponentTests()
    {
        _store = new ClientStateStore();
        _interaction = Substitute.For<IAgentInteractionService>();

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        var restClient = Substitute.For<IGatewayRestClient>();
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        var http = new HttpClient();
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var _mockPrefs = Substitute.For<IPortalPreferencesService>(); _mockPrefs.Current.Returns(new PortalPreferences()); _ctx.Services.AddSingleton(_mockPrefs);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto MakeConv(string convId, string agentId, string title = "Test") =>
        new(convId, agentId, title, false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private AgentState SeedConnectedAgent(string agentId)
    {
        var agent = new AgentState { AgentId = agentId, DisplayName = agentId, IsConnected = true };
        _store.UpsertAgent(agent);
        return agent;
    }

    // ── ChatPanel: Sending a message calls IAgentInteractionService.SendMessageAsync ──

    [Fact]
    public async Task ChatPanel_SendMessage_CallsInteractionServiceWithCorrectArgs()
    {
        SeedConnectedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Type a message into the textarea
        var textarea = cut.Find(".chat-input");
        await cut.InvokeAsync(() => textarea.Input("Hello from test!"));

        // Click send button
        var sendBtn = cut.Find(".send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        await _interaction.Received(1).SendMessageAsync("agent-1", "Hello from test!");
    }

    // ── ChatPanel: Follow Up button queues via IAgentInteractionService.FollowUpAsync ──

    [Fact]
    public async Task ChatPanel_FollowUpButton_CallsFollowUpAsync()
    {
        SeedConnectedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        // Mark the run active so the run controls (incl. Follow Up) render instead of Send.
        _store.GetStreamState("conv-1").IsRunActive = true;

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var textarea = cut.Find(".chat-input");
        await cut.InvokeAsync(() => textarea.Input("Next, write the tests."));

        var followUpBtn = cut.Find("[data-testid='chat-followup-btn']");
        await cut.InvokeAsync(() => followUpBtn.Click());

        // Follow Up must route to FollowUpAsync (queue after loop), NOT SteerAsync or SendMessageAsync.
        await _interaction.Received(1).FollowUpAsync("agent-1", "Next, write the tests.");
        await _interaction.DidNotReceive().SteerAsync("agent-1", Arg.Any<string>());
        await _interaction.DidNotReceive().SendMessageAsync("agent-1", Arg.Any<string>());
    }

    [Fact]
    public void ChatPanel_InputPlaceholder_IncludesPromptsCommandHint()
    {
        SeedConnectedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        var textarea = cut.Find(".chat-input");
        Assert.Contains("/prompts", textarea.GetAttribute("placeholder"));
    }

    [Fact]
    public async Task ChatPanel_PromptsSlashCommand_SendsPromptsMessage()
    {
        SeedConnectedAgent("agent-1");
        _store.SeedConversations("agent-1", [MakeConv("conv-1", "agent-1")]);
        _store.SetActiveConversation("agent-1", "conv-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));
        var textarea = cut.Find(".chat-input");

        await cut.InvokeAsync(() => textarea.Input("/prompts"));
        await cut.InvokeAsync(() => textarea.TriggerEvent("onkeydown", new KeyboardEventArgs { Key = "Enter" }));

        await _interaction.Received(1).SendMessageAsync("agent-1", "/prompts");
    }

    // ── ChatPanel: New session button click triggers confirmation dialog ───────

    [Fact]
    public void ChatPanel_NewSessionButton_Click_ShowsConfirmationDialog()
    {
        SeedConnectedAgent("agent-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Confirm dialog should not be visible initially
        Assert.Empty(cut.FindAll(".reset-confirm-overlay"));

        // Click new session button
        cut.Find(".new-chat-btn").Click();

        // Confirmation dialog should now appear
        cut.Find(".reset-confirm-overlay");
        cut.Find(".confirm-btn");
        cut.Find(".cancel-btn");
    }

    // ── ChatPanel: Confirming new session calls IAgentInteractionService.ResetSessionAsync ──

    [Fact]
    public async Task ChatPanel_ConfirmNewSession_CallsResetSessionAsync()
    {
        SeedConnectedAgent("agent-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        // Open confirmation dialog
        cut.Find(".new-chat-btn").Click();

        // Click confirm
        var confirmBtn = cut.Find(".confirm-btn");
        await cut.InvokeAsync(() => confirmBtn.Click());

        await _interaction.Received(1).ResetSessionAsync("agent-1");
    }

    // ── ChatPanel: Cancelling new session dialog hides dialog without calling service ──

    [Fact]
    public async Task ChatPanel_CancelNewSession_HidesDialogWithoutCallingService()
    {
        SeedConnectedAgent("agent-1");

        var cut = _ctx.Render<ChatPanel>(p => p.Add(c => c.AgentId, "agent-1"));

        cut.Find(".new-chat-btn").Click();
        cut.Find(".cancel-btn").Click();

        Assert.Empty(cut.FindAll(".reset-confirm-overlay"));
        await _interaction.DidNotReceive().ResetSessionAsync(Arg.Any<string>());
    }

    // ── MainLayout: Clicking conversation calls IAgentInteractionService.SelectConversationAsync ──

    [Fact]
    public void MainLayout_ConversationList_RendersConversationButton()
    {
        // Use a dedicated context matching MainLayoutTests setup pattern
        using var ctx = new BunitContext();
        var store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);
        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);

        ctx.Services.AddSingleton<IClientStateStore>(store);
        ctx.Services.AddSingleton(interaction);
        ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        ctx.Services.AddSingleton(portalLoad);
        ctx.Services.AddSingleton(hub);
        ctx.Services.AddSingleton(gatewayInfo);
        ctx.Services.AddSingleton(restClient);
        ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        ctx.Services.AddSingleton(new CronApiClient(http));
        ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs2 = Substitute.For<IPortalPreferencesService>(); mockPrefs2.Current.Returns(new PortalPreferences()); ctx.Services.AddSingleton(mockPrefs2);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        store.SeedAgents([new AgentSummary("a-1", "Agent One")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-1", "a-1", "Click Me", false, "Active", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

        // Verify the conversation button renders with the correct title
        var convItems = cut.FindAll(".conversation-list-item-btn");
        Assert.Single(convItems);
        Assert.Contains("Click Me", convItems[0].TextContent);
    }


    // ── MainLayout: Clicking new conversation button calls CreateConversationAsync ──

    [Fact]
    public async Task MainLayout_NewConversationButton_CallsCreateConversationAsync()
    {
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);

        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(new ExtensionFeatureService(restClient));
        _ctx.Services.AddSingleton(new CronApiClient(http));

        _store.SeedAgents([new AgentSummary("a-1", "Agent One")]);
        _store.SeedConversations("a-1", []);
        _store.SelectView("a-1", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

        cut.WaitForState(() => cut.FindAll(".conversation-new-btn").Count > 0);
        await cut.InvokeAsync(() => cut.Find(".conversation-new-btn").Click());

        await _interaction.Received(1).CreateConversationAsync("a-1");
    }
}
