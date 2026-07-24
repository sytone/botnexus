using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Verifies the agent panel header shows the agent description in place of the
/// permanently-visible id, while the id remains in the DOM (revealed on hover
/// via CSS and exposed as a title tooltip on the meta block).
/// </summary>
public sealed class AgentPanelHeaderTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();

    public AgentPanelHeaderTests()
    {
        // AgentPanel renders its child ChatPanel, which requires the full portal
        // service set, so register the same services as the other AgentPanel tests.
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(true);
        portalLoad.IsLoading.Returns(false);
        portalLoad.LoadError.Returns((string?)null);
        var prefs = Substitute.For<IPortalPreferencesService>();
        prefs.Current.Returns(new PortalPreferences());

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(prefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(http);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Header_renders_description_as_visible_sublabel()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "desc-agent",
            DisplayName = "Desc Agent",
            Description = "Handles widget triage",
            IsConnected = true
        });
        _store.SelectView("desc-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "desc-agent"));

        var description = cut.Find(".agent-panel-description");
        Assert.Equal("Handles widget triage", description.TextContent.Trim());
    }

    [Fact]
    public void Header_keeps_agent_id_in_dom_for_hover_reveal()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "desc-agent",
            DisplayName = "Desc Agent",
            Description = "Handles widget triage",
            IsConnected = true
        });
        _store.SelectView("desc-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "desc-agent"));

        // The id element is still present (hidden by default, shown on hover via CSS)
        // so it stays available to assistive tech and E2E checks.
        var idElement = cut.Find(".agent-panel-id");
        Assert.Equal("desc-agent", idElement.TextContent.Trim());
    }

    [Fact]
    public void Header_exposes_agent_id_as_meta_title_tooltip()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "desc-agent",
            DisplayName = "Desc Agent",
            Description = "Handles widget triage",
            IsConnected = true
        });
        _store.SelectView("desc-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "desc-agent"));

        var meta = cut.Find(".agent-panel-meta");
        Assert.Equal("desc-agent", meta.GetAttribute("title"));
    }

    [Fact]
    public void Header_omits_description_element_when_description_is_empty()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "no-desc-agent",
            DisplayName = "No Desc Agent",
            Description = null,
            IsConnected = true
        });
        _store.SelectView("no-desc-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "no-desc-agent"));

        Assert.Empty(cut.FindAll(".agent-panel-description"));
        // The id element and its hover tooltip remain even without a description.
        Assert.Equal("no-desc-agent", cut.Find(".agent-panel-id").TextContent.Trim());
        Assert.Equal("no-desc-agent", cut.Find(".agent-panel-meta").GetAttribute("title"));
    }

    [Fact]
    public void Header_avatar_uses_agent_emoji_when_set()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "emoji-agent",
            DisplayName = "Emoji Agent",
            Emoji = "🔬",
            IsConnected = true
        });
        _store.SelectView("emoji-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "emoji-agent"));

        var avatar = cut.Find(".agent-panel-avatar");
        Assert.Equal("🔬", avatar.TextContent.Trim());
    }

    [Fact]
    public void Header_avatar_falls_back_to_robot_when_emoji_missing()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = "no-emoji-agent",
            DisplayName = "No Emoji Agent",
            Emoji = null,
            IsConnected = true
        });
        _store.SelectView("no-emoji-agent", string.Empty, SelectionSource.UserClick);

        var cut = _ctx.Render<AgentPanel>(p => p.Add(c => c.AgentId, "no-emoji-agent"));

        var avatar = cut.Find(".agent-panel-avatar");
        Assert.Equal("🤖", avatar.TextContent.Trim());
    }
}