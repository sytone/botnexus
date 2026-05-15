using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AgentPanelVerticalSliceTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IPortalLoadService _portalLoad = Substitute.For<IPortalLoadService>();

    public AgentPanelVerticalSliceTests()
    {
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);
        _portalLoad.InitializeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _store.SeedAgents([new AgentSummary("agent-1", "Alpha")]);
        _store.SeedConversations("agent-1", [
            new ConversationSummaryDto(
                ConversationId: "conv-1",
                AgentId: "agent-1",
                Title: "General",
                IsDefault: true,
                Status: "Active",
                ActiveSessionId: null,
                BindingCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
        ]);
        _store.ActiveAgentId = "agent-1";

        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(Substitute.For<IAgentInteractionService>());
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Agent_selection_renders_agent_panel_shell_container()
    {
        var cut = RenderHomeForAgentConversation();
        Assert.Single(cut.FindAll("[data-testid='agent-panel']"));
    }

    [Fact]
    public void Agent_panel_renders_expected_tab_strip_labels()
    {
        var cut = RenderHomeForAgentConversation();

        var tabLabels = cut.FindAll(".agent-panel-tab .agent-tab-label").Select(label => label.TextContent.Trim()).ToArray();
        Assert.Equal(["Conversation", "Workspace", "Reports", "Canvas"], tabLabels);

        Assert.Contains("data-testid=\"workspace-panel\"", cut.Markup);
        Assert.Contains("Reports are coming next", cut.Markup);
        Assert.Contains("Canvas is coming next", cut.Markup);
    }

    [Fact]
    public void Conversation_tab_is_default_active_tab()
    {
        var cut = RenderHomeForAgentConversation();
        Assert.NotNull(cut.Find(".agent-panel-tab.active[data-tab='conversation']"));
    }

    [Fact]
    public void Conversation_tab_hosts_existing_chat_surface()
    {
        var cut = RenderHomeForAgentConversation();

        Assert.NotNull(cut.Find("[data-testid='agent-panel-conversation'] .chat-panel"));
    }

    [Fact]
    public void App_css_contains_agent_panel_mobile_responsive_hooks()
    {
        var cssPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot",
            "css",
            "app.css");

        var css = File.ReadAllText(cssPath);

        Assert.Contains(".agent-panel", css);
        Assert.Contains(".agent-panel-header", css);
        Assert.Contains(".agent-panel-tab-strip", css);
        Assert.Contains(".agent-panel-tab", css);
        Assert.Contains(".agent-panel-placeholder", css);
        Assert.Contains("@media (max-width: 768px)", css);
    }

    private IRenderedComponent<Home> RenderHomeForAgentConversation() =>
        _ctx.Render<Home>(p => p
            .Add(c => c.AgentId, "agent-1")
            .Add(c => c.ConversationId, "conv-1"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate BotNexus.slnx from test base directory.");
    }
}
