using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins extension-contributed left-nav entries in the portal sidebar.
/// </summary>
/// <remarks>
/// These replace the hard-coded Agent Builder nav fragment that previously lived in MainLayout.
/// The clause that actually costs something is POSITION: a contributed entry declaring order 65
/// must land between Agents (60) and Cron (70), because a merge that simply appended contributions
/// would satisfy "it renders" while silently moving every contributed entry to the bottom.
/// </remarks>
public sealed class PortalNavContributionTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ExtensionFeatureService _features;

    private string _navOrderJson = DefaultNavOrderJson;
    private string _contributionsJson = "[]";
    private string _pluginsJson = "[]";
    private readonly PluginsApiClient _pluginsApi;

    private const string DefaultNavOrderJson = """
        [
          { "key": "home", "order": 5 },
          { "key": "activity", "order": 10 },
          { "key": "tools", "order": 20 },
          { "key": "chat", "order": 30 },
          { "key": "configuration", "order": 40 },
          { "key": "skills", "order": 50 },
          { "key": "agents", "order": 60 },
          { "key": "cron", "order": 70 },
          { "key": "plugins", "order": 80 }
        ]
        """;

    private const string AgentBuilderContribution = """
        [
          { "id": "agent-builder", "label": "Agent Builder", "path": "/agent-builder",
            "icon": "tools", "order": 65, "external": true, "extensionId": "botnexus-agent-builder" }
        ]
        """;

    public PortalNavContributionTests()
    {
        var store = new ClientStateStore();
        var interaction = Substitute.For<IAgentInteractionService>();
        var portalLoad = Substitute.For<IPortalLoadService>();
        portalLoad.IsReady.Returns(false);
        portalLoad.IsLoading.Returns(true);
        portalLoad.LoadError.Returns((string?)null);

        var hub = new GatewayHubConnection();
        var restClient = Substitute.For<IGatewayRestClient>();
        restClient.ApiBaseUrl.Returns("");
        restClient.GetExtensionDetailsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ExtensionDetailDto>
            {
                new("botnexus-skills", "Skills", "1.0.0", true, null, null, null)
            });

        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var gatewayInfo = new GatewayInfoService(http, restClient);
        _features = new ExtensionFeatureService(restClient);

        _ctx.Services.AddSingleton<IClientStateStore>(store);
        _ctx.Services.AddSingleton(interaction);
        _ctx.Services.AddSingleton<ISlashCommandDispatcher>(sp => new SlashCommandDispatcher(sp.GetRequiredService<IAgentInteractionService>()));
        _ctx.Services.AddSingleton(portalLoad);
        _ctx.Services.AddSingleton(hub);
        _ctx.Services.AddSingleton(gatewayInfo);
        _ctx.Services.AddSingleton(Substitute.For<IUpdateStatusService>());
        var mockPrefs = Substitute.For<IPortalPreferencesService>();
        mockPrefs.Current.Returns(new PortalPreferences());
        _ctx.Services.AddSingleton(mockPrefs);
        _ctx.Services.AddSingleton(restClient);
        _ctx.Services.AddSingleton(Substitute.For<IChannelErrorReporter>());
        _ctx.Services.AddSingleton(http);
        _ctx.Services.AddSingleton(_features);
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.Services.AddSingleton(sp => new ConversationSectionsState(sp.GetRequiredService<SectionsApiClient>()));
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new ToolsApiClient(new HttpClient(new FixedJsonHandler(() => "[]")) { BaseAddress = new Uri("http://localhost/") }));
        _pluginsApi = new PluginsApiClient(
            new HttpClient(new PluginsHandler(() => _pluginsJson)) { BaseAddress = new Uri("http://localhost/") });
        _ctx.Services.AddSingleton(_pluginsApi);
        _ctx.Services.AddSingleton(new NavOrderApiClient(
            new HttpClient(new RoutingJsonHandler(() => _navOrderJson, () => _contributionsJson))
            { BaseAddress = new Uri("http://localhost/") }));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        _features.LoadAsync().GetAwaiter().GetResult();
        return _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));
    }

    private List<string> NavTestIdsInRenderOrder()
    {
        var cut = RenderLayout();
        return cut.FindAll("a.sidebar-nav-item")
            .Select(a => a.GetAttribute("data-testid-alias") is { Length: > 0 } alias
                ? alias
                : a.GetAttribute("data-testid") ?? string.Empty)
            .ToList();
    }

    // The sidebar must contain no trace of Agent Builder when nothing contributes it - that is what
    // proves the entry now comes from the manifest rather than from portal source.
    [Fact]
    public void No_contribution_means_no_contributed_entry()
    {
        var ids = NavTestIdsInRenderOrder();

        Assert.NotEmpty(ids);
        Assert.DoesNotContain("nav-agent-builder", ids);
    }

    [Fact]
    public void A_contributed_entry_renders_with_its_label_and_path()
    {
        _contributionsJson = AgentBuilderContribution;

        var cut = RenderLayout();

        var anchor = Assert.Single(cut.FindAll("a.sidebar-nav-item[data-testid='nav-agent-builder']"));
        Assert.Contains("Agent Builder", anchor.TextContent);
        Assert.Equal("botnexus-agent-builder", anchor.GetAttribute("data-nav-contributed-by"));

        // Not the extension's own path: an embedded view is reached through the portal route that
        // frames it, which is what keeps the sidebar and header around the view.
        Assert.Equal("extension/agent-builder", anchor.GetAttribute("href"));
        Assert.Equal("true", anchor.GetAttribute("data-nav-embedded"));
    }

    // Leaving the portal entirely has to be asked for. A plugin that says nothing gets embedded.
    [Fact]
    public void A_full_page_contribution_links_straight_at_the_extension_path()
    {
        _contributionsJson = """
            [
              { "id": "widget", "label": "Widget", "path": "/widget", "icon": "tools",
                "order": 65, "external": true, "fullPage": true, "extensionId": "x" }
            ]
            """;

        var cut = RenderLayout();

        var anchor = Assert.Single(cut.FindAll("a.sidebar-nav-item[data-testid='nav-widget']"));
        Assert.Equal("/widget", anchor.GetAttribute("href"));
        Assert.Equal("false", anchor.GetAttribute("data-nav-embedded"));
    }

    // Order 65 sits between Agents (60) and Cron (70). An append-only merge would put it last.
    [Fact]
    public void A_contributed_entry_lands_at_its_declared_order_not_at_the_end()
    {
        _contributionsJson = AgentBuilderContribution;

        var ids = NavTestIdsInRenderOrder();

        var agents = ids.IndexOf("nav-agents");
        var contributed = ids.IndexOf("nav-agent-builder");
        // Cron's label-based testid is "nav-cron-jobs", but the helper prefers the route-derived
        // alias the rest of the sidebar uses, which is "nav-cron".
        var cron = ids.IndexOf("nav-cron");

        Assert.True(agents >= 0 && contributed >= 0 && cron >= 0, string.Join(",", ids));
        Assert.True(agents < contributed, $"expected agent-builder after agents: {string.Join(",", ids)}");
        Assert.True(contributed < cron, $"expected agent-builder before cron: {string.Join(",", ids)}");
        Assert.NotEqual(ids.Count - 1, contributed);
    }

    // Unknown icon names must fall back rather than leaving a blank square: Icon renders nothing
    // for a name it does not know.
    [Fact]
    public void An_unknown_icon_name_falls_back_instead_of_rendering_nothing()
    {
        _contributionsJson = """
            [
              { "id": "widget", "label": "Widget", "path": "/widget",
                "icon": "not-a-real-icon", "order": 65, "external": true, "extensionId": "x" }
            ]
            """;

        var cut = RenderLayout();

        var anchor = Assert.Single(cut.FindAll("a.sidebar-nav-item[data-testid='nav-widget']"));
        Assert.NotEmpty(anchor.QuerySelectorAll("svg"));
    }

    // A wire document that is not a contribution list must not put blank rows in the sidebar.
    // The stubs other layout tests use answer every path with the nav-order array, so this is the
    // realistic failure, not a hypothetical one.
    [Fact]
    public void Entries_that_cannot_be_rendered_are_dropped()
    {
        _contributionsJson = """
            [
              { "key": "home", "order": 5 },
              { "id": "", "label": "", "path": "" },
              { "id": "evil", "label": "Evil", "path": "//evil.example", "order": 1 },
              { "id": "ok", "label": "Ok", "path": "/ok", "order": 65 }
            ]
            """;

        var ids = NavTestIdsInRenderOrder();

        Assert.Contains("nav-ok", ids);
        Assert.DoesNotContain("nav-evil", ids);
        Assert.DoesNotContain("nav-", ids);
    }

    // The sidebar can get long. A plugin's own entries can be hidden without uninstalling it, and
    // the join happens in the portal because the nav endpoint reads loaded EXTENSIONS and knows
    // nothing about which plugin installed which.
    [Fact]
    public void A_plugin_marked_hidden_drops_its_contributed_entry()
    {
        _contributionsJson = AgentBuilderContribution;
        _pluginsJson = """
            [{"name":"botnexus-agent-builder","source":"/x","resolvedVersion":"abc",
              "updatesEnabled":true,"installedAtUtc":"2026-08-27T00:00:00Z","fileCount":1,
              "trustState":1,"updateState":1,
              "deployedExtensionId":"botnexus-agent-builder","navHidden":true}]
            """;

        var ids = NavTestIdsInRenderOrder();

        Assert.NotEmpty(ids);
        Assert.DoesNotContain("nav-agent-builder", ids);
    }

    // Non-vacuity: the SAME contribution renders when the plugin is not hidden, so the test above
    // proves the flag rather than a broken fetch.
    [Fact]
    public void The_same_entry_renders_when_its_plugin_is_not_hidden()
    {
        _contributionsJson = AgentBuilderContribution;
        _pluginsJson = """
            [{"name":"botnexus-agent-builder","source":"/x","resolvedVersion":"abc",
              "updatesEnabled":true,"installedAtUtc":"2026-08-27T00:00:00Z","fileCount":1,
              "trustState":1,"updateState":1,
              "deployedExtensionId":"botnexus-agent-builder","navHidden":false}]
            """;

        Assert.Contains("nav-agent-builder", NavTestIdsInRenderOrder());
    }

    // Hiding one plugin must not hide an unrelated extension's entry.
    [Fact]
    public void Hiding_one_plugin_leaves_another_extensions_entry_alone()
    {
        _contributionsJson = """
            [
              { "id": "agent-builder", "label": "AB", "path": "/agent-builder",
                "order": 65, "external": true, "extensionId": "botnexus-agent-builder" },
              { "id": "other", "label": "Other", "path": "/other",
                "order": 66, "external": true, "extensionId": "botnexus-other" }
            ]
            """;
        _pluginsJson = """
            [{"name":"botnexus-agent-builder","source":"/x","resolvedVersion":"abc",
              "updatesEnabled":true,"installedAtUtc":"2026-08-27T00:00:00Z","fileCount":1,
              "trustState":1,"updateState":1,
              "deployedExtensionId":"botnexus-agent-builder","navHidden":true}]
            """;

        var ids = NavTestIdsInRenderOrder();

        Assert.DoesNotContain("nav-agent-builder", ids);
        Assert.Contains("nav-other", ids);
    }

    // Hiding a plugin's entry has to repaint the sidebar in place. Asserting on the SAME rendered
    // component - never a fresh render - is what makes this about live update rather than about
    // the filter, which its own tests already cover.
    [Fact]
    public async Task The_sidebar_repaints_when_a_plugin_hides_its_entry()
    {
        _contributionsJson = AgentBuilderContribution;
        var cut = RenderLayout();
        Assert.NotEmpty(cut.FindAll("a.sidebar-nav-item[data-testid='nav-agent-builder']"));

        // The real write path, so the event is raised by the code that ships rather than by a
        // test-only hook.
        _pluginsJson = """
            [{"name":"botnexus-agent-builder","source":"/x","resolvedVersion":"abc",
              "updatesEnabled":true,"installedAtUtc":"2026-08-27T00:00:00Z","fileCount":1,
              "trustState":1,"updateState":1,
              "deployedExtensionId":"botnexus-agent-builder","navHidden":true}]
            """;
        await _pluginsApi.SetNavVisibilityAsync("botnexus-agent-builder", navHidden: true);

        cut.WaitForState(() => cut.FindAll("a.sidebar-nav-item[data-testid='nav-agent-builder']").Count == 0);
        Assert.NotEmpty(cut.FindAll("a.sidebar-nav-item[data-testid='nav-plugins']"));
    }

    /// <summary>Answers the plugin list and the nav-visibility write distinctly.</summary>
    private sealed class PluginsHandler(Func<string> list) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = path.EndsWith("/nav-visibility", StringComparison.Ordinal)
                ? """{"name":"botnexus-agent-builder","source":"/x","resolvedVersion":"abc","updatesEnabled":true,"installedAtUtc":"2026-08-27T00:00:00Z","fileCount":1,"trustState":1,"updateState":1,"deployedExtensionId":"botnexus-agent-builder","navHidden":true}"""
                : list();

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Returns one JSON body for any request; used for the unrelated tools call.</summary>
    private sealed class FixedJsonHandler : HttpMessageHandler
    {
        private readonly Func<string> _json;

        public FixedJsonHandler(Func<string> json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json(), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Answers the nav-order and contributions paths separately.</summary>
    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Func<string> _navOrder;
        private readonly Func<string> _contributions;

        public RoutingJsonHandler(Func<string> navOrder, Func<string> contributions)
        {
            _navOrder = navOrder;
            _contributions = contributions;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = path.Contains("/api/nav/contributions", StringComparison.Ordinal)
                ? _contributions()
                : _navOrder();

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
