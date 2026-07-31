using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins the published service worker's caching strategy for BOTH portals (#2591).
/// </summary>
/// <remarks>
/// <para>
/// The bug these tests exist to prevent: the worker was unconditionally cache-first for every
/// non-navigate GET, including <c>index.html</c> and <c>service-worker-assets.js</c>. Those two
/// files are what tell the app which fingerprinted bundle to load, so caching them meant an
/// installed PWA could never discover a new build. A client holding a pre-#2532 bundle kept
/// running it against an upgraded gateway and died on the changed <c>/api/sessions</c> response
/// shape with <c>DeserializeUnableToConvertValue</c>. The cache was not stale, it was stale
/// permanently, and the only user-side remedy was clearing site data.
/// </para>
/// <para>
/// These are content tests because the repository has no JavaScript engine in the test stack and
/// adding one is a dependency decision that does not belong in a bug fix. They are NOT mere
/// substring assertions, though: <see cref="ImmutableAssetPredicate_matches_only_fingerprinted_framework_assets"/>
/// extracts the worker's ACTUAL <c>fingerprintPattern</c> literal from the file and evaluates it
/// with .NET's regex engine against real published asset names. A test that re-declared the rule
/// in C# would pass even if the worker's own regex were wrong, which is the vacuity being avoided.
/// </para>
/// </remarks>
public sealed class ServiceWorkerCacheStrategyTests
{
    private static string RepoRelative(params string[] parts)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".." }
                .Concat(parts).ToArray()));

    private const string MobileProject = "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile";
    private const string DesktopProject = "BotNexus.Extensions.Channels.SignalR.BlazorClient";

    private static string PublishedWorker(string project)
        => RepoRelative("src", "extensions", project, "wwwroot", "service-worker.published.js");

    private static string IndexHtml(string project)
        => RepoRelative("src", "extensions", project, "wwwroot", "index.html");

    private static string SwUpdateJs(string project)
        => RepoRelative("src", "extensions", project, "wwwroot", "js", "swUpdate.js");

    public static TheoryData<string> Projects => new() { MobileProject, DesktopProject };

    // ── The core defect: the shell must not be served from cache unconditionally ──────────

    [Theory]
    [MemberData(nameof(Projects))]
    public void Published_worker_is_network_first_for_non_fingerprinted_assets(string project)
    {
        var content = File.ReadAllText(PublishedWorker(project));

        // The immutable branch must be GATED on the fingerprint predicate. The pre-fix worker
        // reached `cache.match` for every request with no such gate at all.
        var gateIdx = content.IndexOf("if (isImmutableAsset(url))", StringComparison.Ordinal);
        Assert.True(gateIdx >= 0,
            $"{project}: onFetch must gate the cache-first branch on isImmutableAsset(url); "
            + "without the gate the shell is cached and a new deployment can never be discovered");

        // After the gate, the fall-through must go to the network FIRST.
        var tail = content[gateIdx..];
        var fetchIdx = tail.IndexOf("await fetch(event.request)", StringComparison.Ordinal);
        var fallbackIdx = tail.IndexOf("await cache.match(event.request)", StringComparison.Ordinal);
        Assert.True(fetchIdx >= 0, $"{project}: the non-immutable path must fetch from the network");
        Assert.True(
            fetchIdx < tail.LastIndexOf("await cache.match(event.request)", StringComparison.Ordinal),
            $"{project}: the network fetch must precede the cache fallback on the non-immutable path");
        Assert.True(fallbackIdx >= 0, $"{project}: an offline cache fallback must remain");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Published_worker_keeps_cache_first_for_fingerprinted_framework_assets(string project)
    {
        var content = File.ReadAllText(PublishedWorker(project));

        var gateIdx = content.IndexOf("if (isImmutableAsset(url))", StringComparison.Ordinal);
        Assert.True(gateIdx >= 0, $"{project}: the immutable-asset gate must exist");

        // Inside the gate, the cache is consulted BEFORE the network, so repeat loads still skip
        // the ~1.6 MB runtime download. Regressing this to network-first would be a performance
        // defect, not a correctness one, which is exactly why it needs its own pin.
        var gateBody = content[gateIdx..];
        var closeIdx = gateBody.IndexOf("\n    }", StringComparison.Ordinal);
        Assert.True(closeIdx > 0, $"{project}: could not delimit the immutable-asset branch");
        var body = gateBody[..closeIdx];

        var cacheIdx = body.IndexOf("cache.match(event.request)", StringComparison.Ordinal);
        var fetchIdx = body.IndexOf("fetch(event.request)", StringComparison.Ordinal);
        Assert.True(cacheIdx >= 0, $"{project}: fingerprinted assets must be served from cache");
        Assert.True(cacheIdx < fetchIdx,
            $"{project}: fingerprinted assets must be CACHE-first (cache consulted before network)");
    }

    // ── The predicate itself, evaluated rather than restated ──────────────────────────────

    [Theory]
    [InlineData("/_framework/dotnet.native.veuqw8a0w9.wasm", true)]
    [InlineData("/_framework/System.Private.CoreLib.s1cucomlii.wasm", true)]
    [InlineData("/_framework/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core.vckprhhd4c.wasm", true)]
    // Loader entry points are NOT fingerprinted: "webassembly" is word-like and carries no digit,
    // so it must stay on the revalidating path or the app can never learn about a new build.
    [InlineData("/_framework/blazor.webassembly.js", false)]
    [InlineData("/_framework/blazor.boot.json", false)]
    [InlineData("/_framework/dotnet.js", false)]
    // Too short to be a content hash.
    [InlineData("/_framework/short.ab1.wasm", false)]
    // The shell files whose caching caused #2591 in the first place.
    [InlineData("/index.html", false)]
    [InlineData("/service-worker-assets.js", false)]
    [InlineData("/mobile/css/mobile.css", false)]
    public void ImmutableAssetPredicate_matches_only_fingerprinted_framework_assets(string path, bool expected)
    {
        // Extract the worker's OWN regex literal and run it. If the worker's rule is wrong, this
        // test fails -- which a C#-side reimplementation of the rule could never detect.
        var pattern = ExtractFingerprintPattern(PublishedWorker(MobileProject));
        Assert.Equal(expected, Regex.IsMatch(path, pattern));
    }

    [Fact]
    public void Mobile_and_desktop_workers_share_an_identical_fingerprint_rule()
    {
        // The two published workers are near-copies differing only in cache-name prefix. A
        // divergence in the fingerprint rule would give the two portals different update
        // semantics and would be invisible until one of them bricked in the field.
        Assert.Equal(
            ExtractFingerprintPattern(PublishedWorker(MobileProject)),
            ExtractFingerprintPattern(PublishedWorker(DesktopProject)));
    }

    private static string ExtractFingerprintPattern(string workerPath)
    {
        var content = File.ReadAllText(workerPath);
        var match = Regex.Match(content, @"const fingerprintPattern = /(?<body>.+)/;");
        Assert.True(match.Success,
            $"{workerPath}: could not locate the fingerprintPattern literal. The predicate must "
            + "stay a single extractable regex so this suite evaluates the real rule, not a copy.");

        // A JS regex literal escapes '/' as '\/'; .NET does not require it and treats it literally.
        return match.Groups["body"].Value.Replace(@"\/", "/", StringComparison.Ordinal);
    }

    [Fact]
    public void ImmutableAssetPredicate_delegates_solely_to_the_pinned_regex()
    {
        // MUTATION-DRIVEN. The first version of this suite pinned the regex and the textual shape
        // of onFetch, and a mutant that replaced the predicate body with `return true` -- restoring
        // the exact pre-fix unconditional cache-first behaviour -- SURVIVED all of them. The regex
        // was still correct and still present; it had simply stopped being consulted.
        //
        // So the regex being right is not sufficient: it must also be the ONLY thing deciding.
        // This pins the whole function body, which is why the predicate is deliberately a
        // one-liner in the worker. If a future change genuinely needs more logic here, it must
        // also extend this test rather than delete it.
        foreach (var project in new[] { MobileProject, DesktopProject })
        {
            var content = File.ReadAllText(PublishedWorker(project));
            var match = Regex.Match(
                content,
                @"function isImmutableAsset\(url\)\s*\{(?<body>.*?)\}",
                RegexOptions.Singleline);

            Assert.True(match.Success, $"{project}: isImmutableAsset(url) must exist");

            var body = match.Groups["body"].Value.Trim();
            Assert.Equal("return fingerprintPattern.test(url.pathname);", body);
        }
    }

    // ── Update discovery: registration alone is not enough ────────────────────────────────

    [Theory]
    [MemberData(nameof(Projects))]
    public void Client_registers_the_worker_with_http_cache_bypassed(string project)
    {
        // Without updateViaCache:'none' the browser may satisfy the service-worker.js fetch from
        // the HTTP cache, so a cached WORKER keeps a cached BUNDLE alive indefinitely.
        var content = File.ReadAllText(SwUpdateJs(project));
        Assert.Contains("updateViaCache: 'none'", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Client_checks_for_updates_on_foreground_return(string project)
    {
        // An installed PWA may not navigate for days; without an explicit check on resume it
        // never asks whether a new build exists.
        var content = File.ReadAllText(SwUpdateJs(project));
        Assert.Contains("visibilitychange", content, StringComparison.Ordinal);
        Assert.Contains("registration.update()", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Client_reloads_when_a_new_worker_takes_control(string project)
    {
        // Activating a new worker does not change the ALREADY-RUNNING document; without the
        // reload the user keeps executing the old bundle until they close every tab.
        var content = File.ReadAllText(SwUpdateJs(project));
        Assert.Contains("controllerchange", content, StringComparison.Ordinal);
        Assert.Contains("window.location.reload()", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Client_does_not_reload_on_first_install(string project)
    {
        // 'controllerchange' also fires on the FIRST install, where there is no prior controller
        // and the running document is already the newest bundle. Reloading then is a gratuitous
        // refresh on every user's first visit.
        var content = File.ReadAllText(SwUpdateJs(project));
        Assert.Contains("navigator.serviceWorker.controller", content, StringComparison.Ordinal);
        Assert.Contains("if (!hadController) return;", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Index_html_routes_registration_through_the_update_helper(string project)
    {
        // The helper is only load-bearing if index.html actually calls it. A bare
        // navigator.serviceWorker.register(...) would bypass every guarantee above -- this is the
        // "assert the wiring, not the helper" check.
        var content = File.ReadAllText(IndexHtml(project));
        Assert.Contains("js/swUpdate.js", content, StringComparison.Ordinal);
        Assert.Contains("BotNexusSwUpdate.register('service-worker.js')", content, StringComparison.Ordinal);
        Assert.DoesNotContain("navigator.serviceWorker.register('service-worker.js')", content, StringComparison.Ordinal);
    }

    // ── Regressions guarded from prior issues ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Projects))]
    public void Published_worker_still_bypasses_navigations_and_api(string project)
    {
        // #688: navigate requests must reach the network so HTTP auth challenges surface.
        // The /api/ and /hub/ bypasses must survive too -- caching /api/sessions would produce
        // a subtler variant of the very bug this PR fixes.
        var content = File.ReadAllText(PublishedWorker(project));
        Assert.Contains("event.request.mode === 'navigate'", content, StringComparison.Ordinal);
        Assert.Contains("url.pathname.startsWith('/api/')", content, StringComparison.Ordinal);
        Assert.Contains("url.pathname.startsWith('/hub/')", content, StringComparison.Ordinal);
    }
}
