using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The Memory page ships routed at <c>/memory</c>; these pin that it is also reachable — the exact
/// failure #3346 recorded for the plugins page, which shipped routed, registered, and linked from
/// nowhere, so it existed only for whoever typed the URL. Adding a page is precisely when that
/// recurs.
///
/// The ordering clause is the one that costs something: it is asserted from two different stored
/// orders, because a single-position assertion passes just as happily for an entry nailed into a
/// hard-coded render slot as for one that participates in the ordering model.
/// </summary>
public sealed class MemoryNavEntryTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ExtensionFeatureService _features;

    private string _navOrderJson = DefaultNavOrderJson;

    /// <summary>
    /// A stored order from before the Memory key existed. Deliberate: the resolver is supposed to
    /// re-insert a missing built-in at its default position, so this also covers the upgrade case
    /// for anyone whose saved order predates the page (#2535).
    /// </summary>
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

    public MemoryNavEntryTests()
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

    private List<string> NavTestIdsInRenderOrder()
        => RenderLayout()
            .FindAll("a.toolbar-item[data-testid^='nav-']")
            .Select(a => a.GetAttribute("data-testid")!)
            .ToList();

    [Fact]
    public void Sidebar_renders_a_memory_nav_entry_linking_to_the_memory_route()
    {
        var cut = RenderLayout();

        var matches = cut.FindAll("a.toolbar-item[data-testid='nav-memory']");
        Assert.Single(matches);

        var anchor = matches[0];

        // Relative href, as every other nav anchor emits; Blazor resolves it against the base href
        // to /memory, which is what @page "/memory" registers.
        Assert.Equal("memory", anchor.GetAttribute("href"));
        Assert.Contains("Memory", anchor.TextContent);
    }

    [Fact]
    public void Memory_entry_is_reinserted_at_its_default_position_for_a_stored_order_that_predates_it()
    {
        // The stored order above has no "memory" key at all. It must still appear, and beside its
        // default neighbours rather than dumped at the end — otherwise every existing user gets the
        // new page in the wrong place, or not at all.
        var order = NavTestIdsInRenderOrder();

        var memoryIndex = order.IndexOf("nav-memory");
        var agentsIndex = order.IndexOf("nav-agents");
        var cronIndex = order.IndexOf("nav-cron-jobs");

        Assert.True(memoryIndex >= 0, "Memory must appear even when the stored order predates it.");
        Assert.True(agentsIndex >= 0 && cronIndex >= 0);
        Assert.True(agentsIndex < memoryIndex, "Memory sits after Agents by default.");
        Assert.True(memoryIndex < cronIndex, "Memory sits before Cron by default.");
    }

    [Fact]
    public void Memory_entry_moves_with_a_user_order_override()
    {
        var defaultIndex = NavTestIdsInRenderOrder().IndexOf("nav-memory");
        Assert.True(defaultIndex > 0, "Precondition: Memory does not start first.");

        _navOrderJson = """
            [
              { "key": "memory", "order": 1 },
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

        Assert.Equal("nav-memory", NavTestIdsInRenderOrder()[0]);
    }

    /// <summary>Answers every request with one fixed JSON body. Local to this file, as in the
    /// sibling nav tests: a two-line fake, not a fixture worth coupling files over.</summary>
    private sealed class FixedJsonHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json(), System.Text.Encoding.UTF8, "application/json")
            });
    }
}
