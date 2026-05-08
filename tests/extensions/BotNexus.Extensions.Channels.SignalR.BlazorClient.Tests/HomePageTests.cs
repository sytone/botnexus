using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class HomePageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IClientStateStore _store;
    private readonly IPortalLoadService _portalLoad;

    public HomePageTests()
    {
        _store = Substitute.For<IClientStateStore>();
        _portalLoad = Substitute.For<IPortalLoadService>();

        _portalLoad.IsReady.Returns(false);
        _portalLoad.IsLoading.Returns(true);
        _portalLoad.LoadError.Returns((string?)null);
        _store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        _store.ActiveAgentId.Returns((string?)null);

        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Shows_connecting_spinner_when_not_ready()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);

        var cut = _ctx.Render<Home>();

        Assert.Contains("Connecting", cut.Markup);
    }

    [Fact]
    public void Shows_portal_loading_container_when_not_ready()
    {
        _portalLoad.IsReady.Returns(false);

        var cut = _ctx.Render<Home>();

        cut.Find(".portal-loading");
    }

    [Fact]
    public void Shows_error_message_when_LoadError_is_set()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Connection refused");

        var cut = _ctx.Render<Home>();

        Assert.Contains("Connection refused", cut.Markup);
        cut.Find(".portal-load-error");
    }

    [Fact]
    public void Does_not_render_spinner_when_error_is_set()
    {
        _portalLoad.IsReady.Returns(false);
        _portalLoad.LoadError.Returns("Something failed");

        var cut = _ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".portal-load-spinner"));
    }

    [Fact]
    public void Renders_main_UI_when_ready_with_no_active_agent()
    {
        _portalLoad.IsReady.Returns(true);
        _store.ActiveAgentId.Returns((string?)null);

        var cut = _ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".portal-loading"));
        Assert.Contains("Select an agent", cut.Markup);
    }

    [Fact]
    public void Does_not_render_loading_when_portal_is_ready()
    {
        _portalLoad.IsReady.Returns(true);

        var cut = _ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".portal-loading"));
    }

    [Fact]
    public void Renders_chat_panel_wrapper_for_each_agent_when_ready()
    {
        _portalLoad.IsReady.Returns(true);
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" },
            ["agent-2"] = new AgentState { AgentId = "agent-2", DisplayName = "Beta" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent(Arg.Any<string>()).Returns(ci =>
            agents.GetValueOrDefault(ci.ArgAt<string>(0)));

        // Register additional services needed by ChatPanel
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());

        var cut = _ctx.Render<Home>();

        var wrappers = cut.FindAll(".chat-panel-wrapper");
        Assert.Equal(2, wrappers.Count);
    }

    [Fact]
    public void Active_agent_wrapper_has_active_class()
    {
        _portalLoad.IsReady.Returns(true);
        var agents = new Dictionary<string, AgentState>
        {
            ["agent-1"] = new AgentState { AgentId = "agent-1", DisplayName = "Alpha" }
        };
        _store.Agents.Returns(agents.AsReadOnly());
        _store.ActiveAgentId.Returns("agent-1");
        _store.GetAgent("agent-1").Returns(agents["agent-1"]);

        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient());

        var cut = _ctx.Render<Home>();

        var wrapper = cut.Find(".chat-panel-wrapper");
        Assert.Contains("active", wrapper.ClassList);
    }
}
