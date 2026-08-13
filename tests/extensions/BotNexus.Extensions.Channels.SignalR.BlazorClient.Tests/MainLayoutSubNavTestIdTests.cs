using System.Text.RegularExpressions;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Layout;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3051: pins that every sidebar SUB-nav anchor is addressable by a <c>data-testid</c> that is
/// UNIQUE within a render - the same guarantee #2973 established one level up, extended to the four
/// sub-nav render sites it deliberately left out of its footprint.
///
/// Two distinct defects are covered, and they fail differently. Three sites emitted no testid at
/// all; the fourth emitted the identical literal <c>tools-subnav-item</c> on every tool row. Absence
/// makes automation retarget onto a neighbouring element; repetition makes a strict single-element
/// locator either throw or silently take the first match. Both report success against the wrong
/// element, which is why the observable symptom at the top level was a shipped training video that
/// highlighted Cron Jobs while narrating Skills rather than a red test.
///
/// The agents and skills sub-navs are gated on mutually exclusive routes (<c>IsOnPage</c>), so no
/// single render can contain all four site kinds. The uniqueness assertion therefore runs on the
/// agents route - which yields tools rows, agent rows and Add Agent together, i.e. every repeated
/// site kind - and the Skills Explorer row is pinned separately on the skills route.
/// </summary>
public sealed class MainLayoutSubNavTestIdTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ExtensionFeatureService _features;

    public MainLayoutSubNavTestIdTests()
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
        // The Skills nav entry - and therefore its sub-nav - is gated on the skills extension being
        // loaded. Without it the Explorer row never renders and its assertion would pass vacuously.
        restClient.GetExtensionDetailsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ExtensionDetailDto>
            {
                new("botnexus-skills", "Skills", "1.0.0", true, null, null, null)
            });

        // The layout loads its sidebar agent rows from GET /api/agents through the ambient
        // HttpClient. Two agents are returned so the uniqueness assertion has repeated agent rows to
        // discriminate - one agent could not distinguish a unique id from a shared one.
        var http = new HttpClient(new FixedJsonHandler("""
            [
              { "agentId": "alpha", "displayName": "Alpha" },
              { "agentId": "beta",  "displayName": "Beta"  }
            ]
            """))
        { BaseAddress = new Uri("http://localhost/") };

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

        // Two tools, so the tools rows likewise have a sibling to be distinguished from.
        _ctx.Services.AddSingleton(new ToolsApiClient(
            new HttpClient(new FixedJsonHandler("""
                [
                  { "id": "t-1", "name": "Grafana", "url": "https://grafana", "icon": "", "order": 0 },
                  { "id": "t-2", "name": "Wiki",    "url": "https://wiki",    "icon": "", "order": 1 }
                ]
                """))
            { BaseAddress = new Uri("http://localhost/") }));
        _ctx.Services.AddSingleton(new NavOrderApiClient(
            new HttpClient(new FixedJsonHandler("{\"order\":[]}")) { BaseAddress = new Uri("http://localhost/") }));

        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Renders the layout at <paramref name="relativeUrl"/> with the tools group expanded, since the
    /// tools sub-nav is collapsed by default (#2441) and its rows would otherwise not exist.
    /// </summary>
    private IRenderedComponent<MainLayout> RenderAt(string relativeUrl)
    {
        _features.LoadAsync().GetAwaiter().GetResult();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/" + relativeUrl);

        var cut = _ctx.Render<MainLayout>(p => p
            .Add(c => c.Body, (RenderFragment)(_ => { })));

        cut.WaitForAssertion(() => cut.Find("[data-testid='tools-collapse-toggle']"));
        cut.Find("[data-testid='tools-collapse-toggle']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='tools-subnav-item']").Count));
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> SubNavAnchors(IRenderedComponent<MainLayout> cut) =>
        cut.FindAll("a.sidebar-subnav-item");

    /// <summary>
    /// Acceptance criterion 1: every rendered sub-nav anchor carries a non-empty data-testid, with an
    /// explicit non-vacuity floor so an empty sidebar cannot satisfy the assertion by having nothing
    /// to check. The floor is 5 because the agents route renders two tool rows, two agent rows and
    /// Add Agent - fewer than that means the fixture stopped exercising a site kind.
    /// </summary>
    [Fact]
    public void Every_subnav_anchor_carries_a_non_empty_data_testid()
    {
        var cut = RenderAt("agents");

        var anchors = SubNavAnchors(cut);

        Assert.True(
            anchors.Count >= 5,
            $"Expected at least 5 sub-nav anchors (2 tools + 2 agents + Add Agent); found {anchors.Count}. "
            + "The fixture is no longer exercising every sub-nav site kind, so this test would pass vacuously.");

        var missing = anchors
            .Where(a => string.IsNullOrWhiteSpace(a.GetAttribute("data-testid")))
            .Select(a => $"'{a.TextContent.Trim()}' (href='{a.GetAttribute("href")}')")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Every sidebar sub-nav anchor must carry a data-testid (#3051). Missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Acceptance criterion 2: sub-nav ids are unique within a single render. Asserted on the
    /// EFFECTIVE identity - the route-derived id, which is on data-testid unless the row keeps a
    /// documented group override, in which case it is on data-testid-alias. Comparing raw
    /// data-testid values would spuriously fail on the tools group selector, which is deliberately
    /// shared and covered by criterion 4 instead.
    /// </summary>
    [Fact]
    public void Subnav_testids_are_unique_within_one_render()
    {
        var cut = RenderAt("agents");

        var anchors = SubNavAnchors(cut);
        Assert.True(anchors.Count >= 5, $"Non-vacuity: expected at least 5 sub-nav anchors, found {anchors.Count}.");

        var identities = anchors
            .Select(a => a.GetAttribute("data-testid-alias") is { Length: > 0 } alias
                ? alias
                : a.GetAttribute("data-testid") ?? string.Empty)
            .ToList();

        var duplicates = identities
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} x{g.Count()}")
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Sub-nav data-testid values must be unique within a render (#3051). Duplicated: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// Acceptance criterion 3: each id is derived from its own href, asserted per item rather than by
    /// spot check, so a hand-written id that happens to be unique still fails.
    /// </summary>
    [Fact]
    public void Each_subnav_testid_is_derived_from_its_href()
    {
        var cut = RenderAt("agents");

        var anchors = SubNavAnchors(cut);
        Assert.True(anchors.Count >= 5, $"Non-vacuity: expected at least 5 sub-nav anchors, found {anchors.Count}.");

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttribute("href") ?? string.Empty;
            var expected = "subnav-" + Regex.Replace(href, "[^a-zA-Z0-9-]+", "-").Trim('-');

            var ids = new[] { anchor.GetAttribute("data-testid"), anchor.GetAttribute("data-testid-alias") };

            Assert.True(
                ids.Contains(expected, StringComparer.Ordinal),
                $"Sub-nav anchor href='{href}' must be selectable by '{expected}' (#3051); "
                + $"found data-testid='{ids[0]}', data-testid-alias='{ids[1]}'.");
        }
    }

    /// <summary>
    /// Acceptance criterion 4: the tools group selector survives. It names the row KIND rather than
    /// the row, has an in-repo consumer in <c>MainLayoutTests</c>, and is a legitimate "all tool
    /// rows" selector - so it is preserved verbatim on data-testid while the unique id rides on the
    /// alias. Both tool rows must still answer to it.
    /// </summary>
    [Fact]
    public void Tools_group_selector_still_matches_every_tool_row()
    {
        var cut = RenderAt("agents");

        var toolRows = cut.FindAll("[data-testid='tools-subnav-item']");
        Assert.Equal(2, toolRows.Count);

        Assert.Equal(
            new[] { "subnav-tools-t-1", "subnav-tools-t-2" },
            toolRows.Select(r => r.GetAttribute("data-testid-alias")).ToArray());
    }

    /// <summary>
    /// The Skills Explorer row lives on a route that excludes the agents sub-nav, so it gets its own
    /// render. Before #3051 it carried no testid at all.
    /// </summary>
    [Fact]
    public void Skills_explorer_subnav_row_is_addressable()
    {
        var cut = RenderAt("skills");

        var explorer = cut.FindAll("[data-testid='subnav-skills-explorer']");
        Assert.Single(explorer);
        Assert.Equal("skills/explorer", explorer[0].GetAttribute("href"));
    }

    /// <summary>
    /// Acceptance criterion 5: structural. Behaviour tests prove today's output is right; only this
    /// prevents the next author hand-writing an id onto a new sub-nav row, which is exactly how the
    /// three-of-four gap arose. The tools group override is the single documented exception and is
    /// passed as an argument to the helper rather than written onto an anchor, so it is not matched
    /// by a literal-on-an-anchor scan.
    /// </summary>
    [Fact]
    public void No_subnav_testid_is_written_as_a_per_item_literal()
    {
        var source = File.ReadAllText(MainLayoutPath);

        // Exactly ONE anchor in the whole layout may declare sidebar-subnav-item in markup: the
        // helper's own. Every other occurrence is a render site that bypassed it - which is how the
        // three-of-four gap arose. Counting rather than forbidding keeps the fence honest: a fence
        // that forbids all occurrences would have to be disabled the moment the helper exists.
        var anchorDeclarations = Regex.Matches(source, "<a[^>]*sidebar-subnav-item", RegexOptions.Singleline)
            .Select(m => m.Value)
            .ToList();

        Assert.True(
            anchorDeclarations.Count == 1,
            "Sidebar sub-nav anchors must be emitted by the shared SubNavAnchor helper, not written "
            + $"per item (#3051). Expected exactly 1 anchor declaring sidebar-subnav-item (the helper's "
            + $"own); found {anchorDeclarations.Count}: {string.Join(" | ", anchorDeclarations)}.");

        // ...and that one declaration must belong to the helper, not to a surviving hand-written row
        // that happens to be the only one left.
        Assert.Contains(
            "private RenderFragment SubNavAnchor(",
            source,
            StringComparison.Ordinal);

        // Guard the guard: the scan is only meaningful if the helper is actually in use.
        var calls = Regex.Matches(source, @"@SubNavAnchor\(").Count;
        Assert.True(calls >= 4, $"Expected at least 4 @SubNavAnchor( call sites in {MainLayoutPath}; found {calls}.");
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
    private sealed class FixedJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public FixedJsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
