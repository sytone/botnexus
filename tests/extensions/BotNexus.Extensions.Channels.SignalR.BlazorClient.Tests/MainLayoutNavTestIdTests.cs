using System.Text.RegularExpressions;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #2973: pins that EVERY top-level sidebar nav anchor is addressable by a stable
/// <c>data-testid</c>, not just the four that happened to have one hand-written.
///
/// The expected set is enumerated from <c>MainLayout.razor</c> ITSELF rather than from a literal
/// list of eight. A hard-coded list is vacuous the moment a ninth nav entry ships: the new anchor
/// would carry no testid and the test would still pass because it only ever checked the old eight.
/// Deriving the count from the source means a nav entry added tomorrow is covered on the day it
/// lands, which is precisely the guarantee the issue asks for.
///
/// The failure this prevents is silent-wrong-target, not an exception: automation that cannot find
/// <c>nav-skills</c> retargets onto the nearest anchor that does have a testid and asserts against
/// the wrong element while reporting success.
/// </summary>
public sealed class MainLayoutNavTestIdTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ExtensionFeatureService _features;

    public MainLayoutNavTestIdTests()
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
        // The Skills nav entry is gated on the botnexus-skills extension being loaded. Without it
        // the sidebar renders seven anchors and the coverage assertion would silently skip the very
        // entry the issue reports as missing, so the fixture enables it.
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
        _ctx.Services.AddSingleton(new ToolsApiClient(new HttpClient(new EmptyJsonHandler("[]")) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.Services.AddSingleton(new NavOrderApiClient(new HttpClient(new EmptyJsonHandler("{\"order\":[]}")) { BaseAddress = new Uri("http://localhost/") }));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // Load the feature flags directly. In production this happens once the portal reports ready;
        // driving it here keeps the test independent of the readiness sequence while still exercising
        // the real flag, so the Skills anchor renders through its real @if gate.
        _features.LoadAsync().GetAwaiter().GetResult();
        return _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));
    }

    /// <summary>Acceptance criterion 1: every rendered nav anchor has a non-empty data-testid.</summary>
    [Fact]
    public void Every_sidebar_nav_anchor_carries_a_non_empty_data_testid()
    {
        var cut = RenderLayout();

        var anchors = cut.FindAll("a.sidebar-nav-item");

        // Non-vacuity, two ways. The set must be non-empty, AND it must match the number of nav
        // anchors the component source actually emits - so a newly added entry that forgot its
        // testid cannot hide behind a stale expected count.
        Assert.NotEmpty(anchors);
        Assert.Equal(NavAnchorCallCountInSource(), anchors.Count);

        var missing = anchors
            .Where(a => string.IsNullOrWhiteSpace(a.GetAttribute("data-testid")))
            .Select(a => $"'{a.TextContent.Trim()}' (href='{a.GetAttribute("href")}')")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Every sidebar nav anchor must carry a data-testid (#2973). Missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Acceptance criterion 2: each testid is derived from the route, asserted per item rather than
    /// by spot check, with the empty root route mapping to nav-home instead of the meaningless "nav-".
    /// </summary>
    [Fact]
    public void Each_nav_testid_is_derived_from_its_route()
    {
        var cut = RenderLayout();

        var anchors = cut.FindAll("a.sidebar-nav-item");
        Assert.NotEmpty(anchors);

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttribute("href") ?? string.Empty;
            var expected = string.IsNullOrEmpty(href) ? "nav-home" : $"nav-{href}";

            // The route-derived id is on data-testid, unless the anchor keeps a documented legacy
            // id - in which case the derived id must still select it via data-testid-alias.
            var ids = new[] { anchor.GetAttribute("data-testid"), anchor.GetAttribute("data-testid-alias") };

            Assert.True(
                ids.Contains(expected, StringComparer.Ordinal),
                $"Nav anchor href='{href}' must be selectable by '{expected}' (#2973); "
                + $"found data-testid='{ids[0]}', data-testid-alias='{ids[1]}'.");
        }
    }

    /// <summary>
    /// Acceptance criterion 3: the pre-existing nav-cron-jobs id keeps selecting the Cron anchor.
    /// It tracks the LABEL rather than the route and has in-repo plus unknown external consumers, so
    /// it is preserved, not renamed. The route-derived nav-cron must select the same element.
    /// </summary>
    [Fact]
    public void Cron_anchor_keeps_its_legacy_testid_and_gains_the_route_derived_one()
    {
        var cut = RenderLayout();

        // Both selectors must resolve to exactly one element, and to the SAME element. bUnit's Find
        // returns a fresh wrapper object per call, so reference equality on the wrappers proves
        // nothing - identity is asserted on the underlying DOM node instead.
        var legacyMatches = cut.FindAll("[data-testid='nav-cron-jobs']");
        var derivedMatches = cut.FindAll("[data-testid-alias='nav-cron']");

        Assert.Single(legacyMatches);
        Assert.Single(derivedMatches);

        var legacy = legacyMatches[0];
        Assert.Same(legacy, derivedMatches[0]);

        // The one element carries both keys, so either selector reaches the Cron anchor.
        Assert.Equal("nav-cron-jobs", legacy.GetAttribute("data-testid"));
        Assert.Equal("nav-cron", legacy.GetAttribute("data-testid-alias"));
        Assert.Equal("cron", legacy.GetAttribute("href"));
        Assert.Contains("Cron Jobs", legacy.TextContent);
    }

    /// <summary>
    /// Acceptance criterion 4: the attribute is emitted from ONE helper. Behaviour tests prove the
    /// output is right today; only a structural assertion prevents the next author reintroducing a
    /// hand-written literal on a new anchor - which is exactly how the original four-of-eight gap
    /// arose. A literal data-testid="nav-..." anywhere in the layout means the helper was bypassed.
    /// </summary>
    [Fact]
    public void No_nav_testid_is_written_as_a_per_item_literal()
    {
        var source = File.ReadAllText(MainLayoutPath);

        var literals = Regex.Matches(source, "data-testid=\"nav-[a-z-]", RegexOptions.None)
            .Select(m => m.Value)
            .ToList();

        Assert.True(
            literals.Count == 0,
            "Sidebar nav data-testid values must be emitted by the shared NavAnchor helper, not "
            + $"hand-written per item (#2973). Found {literals.Count} literal(s): {string.Join(", ", literals)}.");
    }

    /// <summary>
    /// Counts the nav anchors the layout emits, by counting calls to the shared helper in source.
    /// This is what makes the coverage assertion self-updating: adding a ninth nav entry adds a
    /// ninth NavAnchor call, which raises the expected count without anyone editing this test.
    /// </summary>
    private static int NavAnchorCallCountInSource()
    {
        var source = File.ReadAllText(MainLayoutPath);
        var count = Regex.Matches(source, @"@NavAnchor\(").Count;

        // Guard the guard: if the helper is ever renamed, this regex would quietly return 0 and the
        // coverage assertion would compare against nothing.
        Assert.True(count > 0, $"Expected @NavAnchor( calls in {MainLayoutPath}; the helper may have been renamed.");
        return count;
    }

    private static string MainLayoutPath => Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient", "Layout", "MainLayout.razor");

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            {
                current = current.Parent;
            }

            Assert.NotNull(current);
            return current!.FullName;
        }
    }

    /// <summary>Returns a fixed JSON body for any request, so the layout's data loads resolve.</summary>
    private sealed class EmptyJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public EmptyJsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
