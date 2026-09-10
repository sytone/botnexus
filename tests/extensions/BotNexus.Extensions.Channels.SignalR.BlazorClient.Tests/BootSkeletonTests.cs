using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The boot skeleton (Interface Review P3) and the cache-buster it shares index.html with.
///
/// A source-file fence rather than a bUnit fixture, and it has to be: the skeleton exists
/// precisely because it renders WITHOUT the WASM runtime, so a component test — which needs that
/// runtime — cannot observe the thing being asserted. Every failure mode below is silent in a
/// browser: the skeleton still renders, it just renders wrong, or forever.
/// </summary>
public sealed class BootSkeletonTests
{
    private static readonly string WwwrootPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot"));

    private static string Index() => File.ReadAllText(Path.Combine(WwwrootPath, "index.html"));

    private static string Css() => File.ReadAllText(Path.Combine(WwwrootPath, "css", "app.css"));

    // ── It has to be there before the runtime is ───────────────────────────

    [Fact]
    public void The_skeleton_is_static_markup_that_paints_before_the_framework_loads()
    {
        // If it ever moves into a Blazor component it stops covering the wait it exists for:
        // the wait IS the framework download and start.
        var index = Index();

        var skeletonAt = index.IndexOf("boot-skeleton", StringComparison.Ordinal);
        var frameworkAt = index.IndexOf("_framework/blazor.webassembly", StringComparison.Ordinal);

        Assert.True(skeletonAt >= 0, "the boot skeleton is missing from index.html");
        Assert.True(
            skeletonAt < frameworkAt,
            "the skeleton must be in the document before the framework script, or it cannot paint first");
    }

    [Fact]
    public void The_skeleton_lives_inside_app_so_the_first_render_tears_it_down()
    {
        // Nothing removes it explicitly. Blazor replaces the contents of #app, and
        // BotNexusBoot replaces #app wholesale on a failed boot - both of which only clear a
        // skeleton that is INSIDE #app. Outside it, the placeholder would sit under the running
        // portal forever, which is the worst available outcome for a loading state.
        var index = Index();

        var appAt = index.IndexOf("<div id=\"app\">", StringComparison.Ordinal);
        Assert.True(appAt >= 0, "#app is missing from index.html");

        var skeletonAt = index.IndexOf("class=\"boot-skeleton\"", StringComparison.Ordinal);
        Assert.True(skeletonAt > appAt, "the skeleton must be inside #app or nothing ever removes it");

        var closingAt = index.IndexOf("<div id=\"blazor-error-ui\">", StringComparison.Ordinal);
        Assert.True(
            closingAt < 0 || skeletonAt < closingAt,
            "the skeleton must sit inside #app, not after it");
    }

    // ── A typo here is invisible, not red ──────────────────────────────────

    [Fact]
    public void Every_skeleton_class_in_the_markup_has_a_rule_in_the_stylesheet()
    {
        // An unstyled placeholder is a zero-height span. It renders, it throws nothing, and the
        // user gets the blank screen this feature was built to replace.
        var css = Css();
        var used = Regex.Matches(Index(), @"\bboot-(?:sk|skeleton)[a-z0-9-]*\b")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);

        var missing = used.Where(c => !css.Contains("." + c)).ToList();
        Assert.True(missing.Count == 0, $"no CSS rule for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_skeleton_mirrors_the_real_shells_dimensions()
    {
        // The placeholder is only worth having if the real UI lands on top of it rather than
        // shoving it around. Both values are read from the live rules, so this fails if either
        // side is changed alone.
        var css = Css();

        Assert.Contains("min-height: var(--density-bar-h)", BlockFor(css, ".boot-skeleton-banner"));
        Assert.Contains("flex: 0 0 240px", BlockFor(css, ".boot-skeleton-sidebar"));
    }

    [Fact]
    public void The_shimmer_stops_for_anyone_who_asked_for_less_motion()
    {
        // An infinite decorative loop with no end state is the textbook case for this setting.
        // Scans EVERY reduced-motion block, not the first one: the stylesheet already had such
        // blocks before the skeleton existed, so anchoring on the first would assert nothing
        // about this animation and pass on a stylesheet that never disables it.
        var css = Css();

        var guarded = Regex.Matches(css, @"prefers-reduced-motion")
            .Select(m => css[m.Index..Math.Min(m.Index + 600, css.Length)])
            .Any(block => block.Contains(".boot-sk", StringComparison.Ordinal));

        Assert.True(guarded, "the boot shimmer keeps animating for users who asked for less motion");
    }

    [Fact]
    public void Density_is_stamped_before_first_paint_so_the_banner_does_not_jump()
    {
        // :root carries the COMPACT tokens. Without this, a comfortable-density user gets a 32px
        // skeleton bar that snaps to 46px the instant Blazor renders - a visible jolt introduced
        // by the very thing meant to smooth the boot.
        var index = Index();

        var stampAt = index.IndexOf("'data-density'", StringComparison.Ordinal);
        var frameworkAt = index.IndexOf("_framework/blazor.webassembly", StringComparison.Ordinal);

        Assert.True(stampAt >= 0, "density is never stamped on the document element");
        Assert.True(stampAt < frameworkAt, "density must be stamped before the framework loads");
    }

    [Fact]
    public void The_skeleton_announces_itself_once_rather_than_reading_out_every_placeholder()
    {
        // A dozen empty bars announced individually is worse than silence. One live region says
        // what is happening; the shapes are decoration and are hidden.
        var index = Index();
        var skeleton = SkeletonMarkup(index);

        Assert.Contains("role=\"status\"", skeleton);
        Assert.Contains("Loading BotNexus", skeleton);
        Assert.True(
            Regex.Matches(skeleton, "aria-hidden=\"true\"").Count >= 2,
            "the decorative regions must be hidden from assistive technology");
    }

    // ── It has to look like the page it is standing in for ────────────────

    [Fact]
    public void The_skeleton_includes_the_nav_row_every_route_has()
    {
        // Leaving it out made the content area start a row too high, so first render pushed
        // everything down - a layout jump introduced by the loading state itself.
        Assert.Contains("boot-skeleton-toolbar", Index());
    }

    [Fact]
    public void Both_content_shapes_ship_in_the_markup_rather_than_being_built_by_script()
    {
        // A shape assembled at runtime would need script to run before anything painted, which
        // is exactly the wait the skeleton exists to cover. Both are in the HTML; CSS picks one.
        var skeleton = SkeletonMarkup(Index());

        Assert.Contains("boot-skeleton-transcript", skeleton);
        Assert.Contains("boot-skeleton-page", skeleton);
    }

    [Fact]
    public void The_transcript_shape_is_reserved_for_routes_that_actually_show_one()
    {
        // Root is a conversation route - the sidebar belongs there - but it renders the Home
        // dashboard, not a transcript. A bottom-aligned skeleton on Home would put every
        // placeholder where no content is about to arrive.
        var css = Css();

        Assert.Contains("[data-boot-transcript=\"true\"] .boot-skeleton-transcript", css);
        Assert.Contains("[data-boot-transcript=\"true\"] .boot-skeleton-page", css);
        Assert.Contains("display: none", BlockFor(css, ".boot-skeleton-transcript"));
    }

    [Fact]
    public void The_page_shape_matches_the_container_the_real_page_uses()
    {
        // Copied from .home-page, not approximated. If the real container moves and this does
        // not, the placeholder cards sit somewhere the real cards will never appear.
        var css = Css();
        var real = BlockFor(css, ".home-page");
        var skeleton = BlockFor(css, ".boot-skeleton-page");

        foreach (var property in new[] { "max-width", "margin", "padding" })
        {
            var expected = PropertyIn(real, property);
            Assert.True(
                skeleton.Contains(expected, StringComparison.Ordinal),
                $".boot-skeleton-page must carry the same {property} as .home-page ({expected})");
        }
    }

    [Fact]
    public void The_sidebar_placeholder_is_off_unless_this_boot_will_actually_have_one()
    {
        // It is a persisted preference that defaults to CLOSED, and it only appears on
        // conversation routes. Drawing a 240px pane that then evaporates is a bigger jump than
        // never drawing one.
        var css = Css();

        Assert.Contains("display: none", BlockFor(css, ".boot-skeleton-sidebar"));
        Assert.Contains("[data-boot-sidebar=\"true\"] .boot-skeleton-sidebar", css);
        Assert.Contains("botnexus-sidebar-open", Index());
    }

    [Fact]
    public void The_boot_route_test_knows_the_same_routes_MainLayout_does()
    {
        // The pre-paint script re-implements MainLayout.IsConversationRoute in JavaScript,
        // because it has to answer before any .NET exists. Two copies of one rule drift
        // silently - the skeleton would simply stop matching, with nothing red anywhere - so
        // this pins them together by reading the route literals out of both sources.
        var layout = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "src", "extensions", "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "Layout", "MainLayout.razor")));

        var start = layout.IndexOf("internal static bool IsConversationRoute", StringComparison.Ordinal);
        Assert.True(start >= 0, "IsConversationRoute has been renamed or removed");

        var end = layout.IndexOf("private void OnLocationChanged", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of IsConversationRoute");

        var routes = Regex.Matches(layout[start..end], "\"([a-z][a-z0-9/-]*)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(routes);

        var script = Index();
        var missing = routes.Where(r => !script.Contains("'" + r + "'", StringComparison.Ordinal)).ToList();

        Assert.True(
            missing.Count == 0,
            $"MainLayout treats these as conversation routes but the boot script does not: {string.Join(", ", missing)}");
    }

    // ── The cache-buster, which has drifted by hand more than once ─────────

    [Fact]
    public void One_cache_buster_token_covers_the_stylesheet_and_every_script()
    {
        // A partial bump ships a new app.css against last build's JS. Nothing fails; the portal
        // just behaves like a version that never existed. This has been caught by eye before,
        // which is not a control.
        var tokens = Regex.Matches(Index(), @"\?v=([a-z0-9]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.True(
            tokens.Count == 1,
            $"index.html carries {tokens.Count} different cache-buster tokens: {string.Join(", ", tokens)}");
    }

    [Fact]
    public void Every_local_script_and_stylesheet_carries_the_cache_buster()
    {
        // An unversioned asset is the one a browser keeps forever.
        var index = Index();

        var unversioned = Regex.Matches(index, @"(?:src|href)=""((?:js|css)/[^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(u => !u.Contains("?v=", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unversioned.Count == 0,
            $"assets served without a cache-buster: {string.Join(", ", unversioned)}");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>Text of the first rule block for <paramref name="selector"/>.</summary>
    private static string BlockFor(string css, string selector)
    {
        var at = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no rule for {selector}");
        var close = css.IndexOf('}', at);
        return css[at..close];
    }

    /// <summary>The full <c>name: value;</c> declaration for one property inside a rule block.</summary>
    private static string PropertyIn(string block, string property)
    {
        var m = Regex.Match(block, $@"\b{property}:\s*[^;]+;");
        Assert.True(m.Success, $"{property} is not declared in the block being compared");
        return Regex.Replace(m.Value, @"\s+", " ").Trim();
    }

    private static string SkeletonMarkup(string index)
    {
        var at = index.IndexOf("class=\"boot-skeleton\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "the boot skeleton is missing from index.html");
        var end = index.IndexOf("<div id=\"blazor-error-ui\">", StringComparison.Ordinal);
        return end > at ? index[at..end] : index[at..];
    }
}
