using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the Activity sub-navigation shell and its parameterised route (#2897).
/// </summary>
/// <remarks>
/// The shell is routing + navigation only: no subsection content, no new endpoint, no new query.
/// These pin the four observable clauses of the issue - direct navigation selects a section, an
/// unknown section degrades non-fatally to the default view, the parameterless route is unchanged,
/// and hrefs follow the section KEY rather than its display position.
/// </remarks>
public sealed class ActivitySubNavigationTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly IPortalLoadService _portalLoad;

    public ActivitySubNavigationTests()
    {
        _portalLoad = Substitute.For<IPortalLoadService>();
        _portalLoad.IsReady.Returns(true);
        _portalLoad.IsLoading.Returns(false);
        _portalLoad.LoadError.Returns((string?)null);

        var store = Substitute.For<IClientStateStore>();
        store.Agents.Returns(new Dictionary<string, AgentState>().AsReadOnly());
        store.GetAgent(Arg.Any<string>()).Returns((AgentState?)null);

        var rest = Substitute.For<IGatewayRestClient>();
        rest.GetAllConversationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummaryDto>>(Array.Empty<ConversationSummaryDto>()));

        _ctx.Services.AddSingleton(_portalLoad);
        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton(rest);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private static readonly IReadOnlyList<ActivitySection> TwoSections =
    [
        new("overview", "Overview"),
        new("costs", "Costs")
    ];

    /// <summary>
    /// AC1 (route declaration half): the component carries BOTH the parameterless and the
    /// parameterised route templates. Reverting the routing change deletes the second template and
    /// reddens this by name.
    /// </summary>
    [Fact]
    public void Activity_declares_both_the_parameterless_and_the_parameterised_route()
    {
        var templates = typeof(Activity)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(a => a.Template)
            .ToList();

        templates.ShouldContain("/activity");
        templates.ShouldContain("/activity/{Section}");
    }

    /// <summary>
    /// AC1: navigating directly to a subsection URL selects that subsection with no clicks.
    /// </summary>
    [Fact]
    public void Direct_navigation_to_a_subsection_url_selects_that_subsection()
    {
        var cut = _ctx.Render<Activity>(p => p
            .Add(c => c.Sections, TwoSections)
            .Add(c => c.Section, "costs"));

        var active = cut.FindAll("[data-testid='activity-subnav-item'].active");
        active.Count.ShouldBe(1);
        active[0].GetAttribute("data-section").ShouldBe("costs");
        cut.Instance.ActiveSection.ShouldBe("costs");

        // Non-fatal notice belongs to the unknown-section path only.
        cut.FindAll("[data-testid='activity-subnav-unknown']").ShouldBeEmpty();
    }

    /// <summary>
    /// AC2: an unknown or removed section name falls back to the default view with a non-fatal
    /// message - no exception, no blank panel.
    /// </summary>
    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("retired-section")]
    public void Unknown_section_falls_back_to_the_default_view_with_a_non_fatal_message(string section)
    {
        var cut = _ctx.Render<Activity>(p => p
            .Add(c => c.Sections, TwoSections)
            .Add(c => c.Section, section));

        cut.Instance.ActiveSection.ShouldBeNull();
        cut.FindAll("[data-testid='activity-subnav-item'].active").ShouldBeEmpty();

        var notice = cut.Find("[data-testid='activity-subnav-unknown']");
        notice.TextContent.ShouldContain(section);

        // The default dashboard is still rendered: fallback, not an error page or a blank panel.
        cut.Find("[data-testid='activity-dashboard']");
    }

    /// <summary>
    /// AC3: the parameterless route selects no subsection, emits no fallback notice, and still
    /// renders the same dashboard as before the parameterised route was added.
    /// </summary>
    [Fact]
    public void Parameterless_route_renders_the_default_view_with_no_subsection_selected()
    {
        var cut = _ctx.Render<Activity>(p => p.Add(c => c.Sections, TwoSections));

        cut.Instance.ActiveSection.ShouldBeNull();
        cut.FindAll("[data-testid='activity-subnav-item'].active").ShouldBeEmpty();
        cut.FindAll("[data-testid='activity-subnav-unknown']").ShouldBeEmpty();
        cut.Find("[data-testid='activity-dashboard']");
    }

    /// <summary>
    /// AC4: each sub-navigation entry's href is derived from the section KEY, so reversing the
    /// display order moves the entries but never rewrites their link targets.
    /// </summary>
    [Fact]
    public void Subnav_hrefs_follow_the_section_key_after_reordering()
    {
        static Dictionary<string, string> HrefsByKey(IRenderedComponent<Activity> c) =>
            c.FindAll("[data-testid='activity-subnav-item']")
                .ToDictionary(e => e.GetAttribute("data-section")!, e => e.GetAttribute("href")!);

        var forward = _ctx.Render<Activity>(p => p.Add(c => c.Sections, TwoSections));
        var reversed = _ctx.Render<Activity>(p => p.Add(c => c.Sections, TwoSections.Reverse().ToList()));

        var forwardHrefs = HrefsByKey(forward);
        var reversedHrefs = HrefsByKey(reversed);

        forwardHrefs.Count.ShouldBe(reversedHrefs.Count);
        foreach (var (key, href) in forwardHrefs)
            reversedHrefs[key].ShouldBe(href);
        forwardHrefs["overview"].ShouldBe("/activity/overview");
        forwardHrefs["costs"].ShouldBe("/activity/costs");

        // Display order really did change - otherwise the assertion above would be vacuous.
        forward.FindAll("[data-testid='activity-subnav-item']")[0].GetAttribute("data-section")
            .ShouldBe("overview");
        reversed.FindAll("[data-testid='activity-subnav-item']")[0].GetAttribute("data-section")
            .ShouldBe("costs");
    }

    /// <summary>
    /// AC4 (unit half): the href builder uses the key, with no index parameter available to it.
    /// </summary>
    [Fact]
    public void Section_href_is_built_from_the_key()
    {
        Activity.SectionHref("costs").ShouldBe("/activity/costs");
        Activity.SectionHref("overview").ShouldBe("/activity/overview");
    }

    /// <summary>
    /// AC1/AC4: one linkable entry per known section, in registry order.
    /// </summary>
    [Fact]
    public void Subnav_renders_one_linkable_entry_per_known_section()
    {
        var cut = _ctx.Render<Activity>(p => p.Add(c => c.Sections, TwoSections));

        cut.Find("[data-testid='activity-subnav']");
        var items = cut.FindAll("[data-testid='activity-subnav-item']");
        items.Count.ShouldBe(TwoSections.Count);
        items.Select(i => i.GetAttribute("data-section")).ShouldBe(["overview", "costs"]);
        items.ShouldAllBe(i => i.TagName == "A");
    }
}
