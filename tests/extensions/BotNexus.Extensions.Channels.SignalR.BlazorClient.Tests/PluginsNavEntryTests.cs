using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3346: the plugins page (#2687) shipped routed at <c>/plugins</c> with no sidebar entry, so it
/// was reachable only by typing the URL. These tests pin the nav entry itself: that it renders,
/// that its href resolves to the plugins route, that it carries the route-derived
/// <c>nav-plugins</c> testid the rest of the sidebar uses (#2973), and - the clause that actually
/// costs something - that it obeys a user order override exactly as any other built-in does,
/// with no special-casing.
///
/// Assertions live in this new file rather than <c>MainLayoutTests</c> because that file is
/// reserved by an in-flight PR; the seam under test is the same either way.
/// </summary>
public sealed class PluginsNavEntryTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ExtensionFeatureService _features;

    /// <summary>
    /// Nav order the fake <c>GET /api/nav-order</c> returns. Each test sets this BEFORE rendering,
    /// which is what makes the override clause non-vacuous: the same component is driven once with
    /// the built-in order and once with an override that hoists plugins to the top.
    /// </summary>
    private string _navOrderJson = DefaultNavOrderJson;

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

    public PluginsNavEntryTests()
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
        _ctx.Services.AddSingleton(new CronApiClient(http));
        _ctx.Services.AddSingleton(new SectionsApiClient(http));
        _ctx.Services.AddSingleton(sp => new ConversationSectionsState(sp.GetRequiredService<SectionsApiClient>()));
        _ctx.Services.AddSingleton(new ToolsApiClient(new HttpClient(new FixedJsonHandler(() => "[]")) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.Services.AddSingleton(new NavOrderApiClient(
            new HttpClient(new FixedJsonHandler(() => _navOrderJson)) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        _features.LoadAsync().GetAwaiter().GetResult();
        return _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));
    }

    /// <summary>
    /// Acceptance criteria 1 and 3: the sidebar renders exactly one Plugins entry, addressable by
    /// the same route-derived <c>data-testid</c> convention the existing nav tests use, and its
    /// href resolves to the <c>/plugins</c> route the page is registered at.
    /// </summary>
    [Fact]
    public void Sidebar_renders_a_plugins_nav_entry_linking_to_the_plugins_route()
    {
        var cut = RenderLayout();

        var matches = cut.FindAll("a.sidebar-nav-item[data-testid='nav-plugins']");
        Assert.Single(matches);

        var anchor = matches[0];

        // The href is emitted relative (as every other nav anchor is), so assert the route rather
        // than a leading-slash literal - Blazor resolves "plugins" against the base href to
        // /plugins, which is what @page "/plugins" registers.
        Assert.Equal("plugins", anchor.GetAttribute("href"));
        Assert.Contains("Plugins", anchor.TextContent);
    }

    /// <summary>
    /// Acceptance criterion 4: the entry participates in user order overrides like any other
    /// built-in. Rendered with the built-in order it sits last; rendered with an override that
    /// gives plugins the lowest order number it sits first. Asserting BOTH positions from the same
    /// component is what proves the position is driven by the ordering model rather than by a
    /// hard-coded render slot - a single-position assertion would pass for a special-cased entry
    /// nailed to the bottom of the list.
    /// </summary>
    [Fact]
    public void Plugins_entry_moves_with_a_user_order_override()
    {
        var defaultOrder = NavTestIdsInRenderOrder();
        Assert.Equal("nav-plugins", defaultOrder[^1]);

        // Same component, overridden order: plugins hoisted above home.
        _navOrderJson = """
            [
              { "key": "plugins", "order": 1 },
              { "key": "home", "order": 5 },
              { "key": "activity", "order": 10 },
              { "key": "tools", "order": 20 },
              { "key": "chat", "order": 30 },
              { "key": "configuration", "order": 40 },
              { "key": "skills", "order": 50 },
              { "key": "agents", "order": 60 },
              { "key": "cron", "order": 70 }
            ]
            """;

        var overridden = NavTestIdsInRenderOrder();
        Assert.Equal("nav-plugins", overridden[0]);

        // Non-vacuity: the override must MOVE the entry, not merely be present in both renders,
        // and it must not drop any other nav entry on the way.
        Assert.NotEqual(defaultOrder[0], overridden[0]);
        Assert.Equal(defaultOrder.Count, overridden.Count);
        Assert.Equal(
            defaultOrder.OrderBy(x => x, StringComparer.Ordinal),
            overridden.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// Renders a fresh layout and returns the top-level nav testids in DOM order.
    /// </summary>
    private List<string> NavTestIdsInRenderOrder()
    {
        var cut = RenderLayout();
        var ids = cut.FindAll("a.sidebar-nav-item")
            .Select(a => a.GetAttribute("data-testid-alias") is { Length: > 0 } alias
                ? alias
                : a.GetAttribute("data-testid") ?? string.Empty)
            .ToList();

        Assert.NotEmpty(ids);
        Assert.Contains("nav-plugins", ids);

        // No disposal needed: cut.FindAll is scoped to THIS rendered component's tree, so a second
        // render in the same context cannot leak anchors into the first render's result.
        return ids;
    }

    /// <summary>Returns a caller-supplied JSON body for any request, re-read on every call.</summary>
    private sealed class FixedJsonHandler : HttpMessageHandler
    {
        private readonly Func<string> _json;

        public FixedJsonHandler(Func<string> json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json(), System.Text.Encoding.UTF8, "application/json")
            });
    }
}
