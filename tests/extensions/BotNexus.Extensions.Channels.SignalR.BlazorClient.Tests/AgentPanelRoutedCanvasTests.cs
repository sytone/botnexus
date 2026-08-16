using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2976: the canvas pane must render the canvas of the conversation named by the ROUTE, not the
/// agent's active conversation.
///
/// Why the "one-line swap" in the issue body is not safe, and why these tests are shaped this way:
/// <see cref="Home"/> renders one <c>AgentPanel</c> per agent in <c>Store.Agents</c> (a foreach),
/// while the route names exactly ONE agent. <c>CanvasPanel</c> resolves its HTML through
/// <c>IClientStateStore.GetConversation</c>, which searches EVERY agent's conversation map. So a
/// routed conversation id handed to the wrong agent's panel would happily resolve and render a
/// foreign agent's canvas. The routed id is therefore only valid for the routed agent, and only
/// when that conversation actually belongs to it.
/// </summary>
public sealed class AgentPanelRoutedCanvasTests : IDisposable
{
    private const string RoutedCanvas = "<h1>ROUTED-CANVAS</h1>";
    private const string ActiveCanvas = "<h1>ACTIVE-CANVAS</h1>";
    private const string OtherAgentCanvas = "<h1>OTHER-AGENT-CANVAS</h1>";

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public AgentPanelRoutedCanvasTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _store.SeedAgents([new AgentSummary("agent-1", "Alpha"), new AgentSummary("agent-2", "Beta")]);
        _store.SeedConversations("agent-1", [
            Conversation("conv-active", isDefault: true),
            Conversation("conv-routed", isDefault: false)
        ]);
        _store.SeedConversations("agent-2", [Conversation("conv-other", isDefault: true)]);

        // The divergence under test: the agent's ACTIVE conversation is conv-active, while the
        // route (below) names conv-routed. IAgentInteractionService is a substitute, so the
        // route-application path in Home cannot converge active onto routed - which is exactly
        // the real-world state a stale deep link produces.
        _store.SelectView("agent-1", string.Empty, SelectionSource.UserClick);
        _store.SetActiveConversation("agent-1", "conv-active");
        _store.SetActiveConversation("agent-2", "conv-other");

        _store.GetConversation("conv-active")!.CanvasHtml = ActiveCanvas;
        _store.GetConversation("conv-routed")!.CanvasHtml = RoutedCanvas;
        _store.GetConversation("conv-other")!.CanvasHtml = OtherAgentCanvas;

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        // #3064: Home injects the per-agent conversation MRU at the route seam.
        _ctx.Services.AddSingleton<IConversationMruService, ConversationMruService>();
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp =>
            new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(Substitute.For<IPortalPreferencesService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static ConversationSummaryDto Conversation(string id, bool isDefault) => new(
        ConversationId: id,
        AgentId: id == "conv-other" ? "agent-2" : "agent-1",
        Title: id,
        IsDefault: isDefault,
        Status: "Active",
        ActiveSessionId: null,
        BindingCount: 0,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    /// <summary>AC1 + AC2: routed conversation differs from active, and the ROUTED one wins.</summary>
    [Fact]
    public void Canvas_pane_renders_routed_conversation_when_it_differs_from_active()
    {
        var cut = RenderAt("/agent/agent-1/conversation/conv-routed?tab=canvas", "agent-1", "conv-routed");

        var srcdoc = CanvasSrcdocFor(cut, "agent-1");
        Assert.Contains("ROUTED-CANVAS", srcdoc);
        Assert.DoesNotContain("ACTIVE-CANVAS", srcdoc);
    }

    /// <summary>
    /// AC3: with no conversation segment on the route, the pre-existing active-conversation
    /// behaviour is preserved.
    /// </summary>
    [Fact]
    public void Canvas_pane_falls_back_to_active_conversation_when_route_has_no_conversation()
    {
        var cut = RenderAt("/agent/agent-1?tab=canvas", "agent-1", conversationId: null);

        var srcdoc = CanvasSrcdocFor(cut, "agent-1");
        Assert.Contains("ACTIVE-CANVAS", srcdoc);
        Assert.DoesNotContain("ROUTED-CANVAS", srcdoc);
    }

    /// <summary>
    /// The hazard the naive swap introduces: Home renders an AgentPanel for EVERY agent, but the
    /// routed conversation belongs to exactly one. A non-routed agent's panel must keep showing its
    /// own active conversation and must never resolve the routed id through the store-wide
    /// GetConversation lookup.
    /// </summary>
    [Fact]
    public void Routed_conversation_does_not_leak_into_a_different_agents_canvas_pane()
    {
        var cut = RenderAt("/agent/agent-1/conversation/conv-routed?tab=canvas", "agent-1", "conv-routed");

        var srcdoc = CanvasSrcdocFor(cut, "agent-2");
        Assert.Contains("OTHER-AGENT-CANVAS", srcdoc);
        Assert.DoesNotContain("ROUTED-CANVAS", srcdoc);
    }

    /// <summary>
    /// A routed conversation id that does not belong to the routed agent must not be honoured -
    /// otherwise a hand-edited or stale URL renders another agent's canvas under this agent.
    /// </summary>
    [Fact]
    public void Routed_conversation_belonging_to_another_agent_is_not_honoured()
    {
        var cut = RenderAt("/agent/agent-1/conversation/conv-other?tab=canvas", "agent-1", "conv-other");

        var srcdoc = CanvasSrcdocFor(cut, "agent-1");
        Assert.Contains("ACTIVE-CANVAS", srcdoc);
        Assert.DoesNotContain("OTHER-AGENT-CANVAS", srcdoc);
    }

    private IRenderedComponent<Home> RenderAt(string uri, string agentId, string? conversationId)
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(uri);

        return _ctx.Render<Home>(p =>
        {
            p.Add(c => c.AgentId, agentId);
            p.Add(c => c.ConversationId, conversationId);
        });
    }

    /// <summary>
    /// Reads the srcdoc of the canvas iframe inside a specific agent's panel. Scoping by the panel
    /// id is what makes the cross-agent assertions meaningful - Home renders several panels and a
    /// whole-markup search could not tell them apart.
    /// </summary>
    private static string CanvasSrcdocFor(IRenderedComponent<Home> cut, string agentId)
    {
        var section = cut.Find($"#{agentId}-canvas-panel");
        var iframe = section.QuerySelector("iframe[data-testid='canvas-iframe']");
        Assert.NotNull(iframe);
        var srcdoc = iframe!.GetAttribute("srcdoc");
        Assert.False(string.IsNullOrWhiteSpace(srcdoc), $"canvas iframe for '{agentId}' had no srcdoc");
        return srcdoc!;
    }
}
