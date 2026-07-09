using System;
using System.IO;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Validates the full PWA lifecycle wiring (#1841, PBI4 of #1836): the mobile app
/// must react to the complete set of Page Lifecycle events -- <c>visibilitychange</c>,
/// <c>pagehide</c>, <c>freeze</c>, and <c>resume</c> -- mapping them to save / stale-mark /
/// repaint actions, and must funnel a single resume path into the PBI1 liveness-verified
/// hub reset (no duplicate resume handling). Also hardens <c>manifest.json</c> for
/// standalone-PWA correctness.
/// </summary>
public sealed class MobilePwaLifecycleTests
{
    // AppContext.BaseDirectory is tests/extensions/...Mobile.Tests/bin/Debug/net10.0/
    // Go up 6 levels to reach repo root, then into src/extensions/...Mobile.
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile"));

    private static readonly string WwwrootPath = Path.Combine(ProjectRoot, "wwwroot");

    private static string AppResumeJs => File.ReadAllText(Path.Combine(WwwrootPath, "js", "appResume.js"));

    private static string ManifestJson => File.ReadAllText(Path.Combine(WwwrootPath, "manifest.json"));

    // ── Lifecycle event coverage ─────────────────────────────────────────────

    [Fact]
    public void AppResume_listens_for_visibilitychange()
    {
        Assert.Contains("visibilitychange", AppResumeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppResume_listens_for_pagehide()
    {
        Assert.Contains("'pagehide'", AppResumeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppResume_listens_for_freeze()
    {
        Assert.Contains("'freeze'", AppResumeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void AppResume_listens_for_resume()
    {
        Assert.Contains("'resume'", AppResumeJs, StringComparison.Ordinal);
    }

    // ── freeze / pagehide mark state potentially stale so resume repaints ─────

    [Fact]
    public void AppResume_marks_state_stale_on_background_events()
    {
        var js = AppResumeJs;

        // A stale flag must be set when the app is frozen or hidden so the next
        // resume forces a repaint (and via PBI1 a liveness-verified hub reset).
        Assert.Contains("stale", js, StringComparison.OrdinalIgnoreCase);
    }

    // ── resume forces a stale-state repaint (single resume path) ─────────────

    [Fact]
    public void AppResume_invokes_single_dotnet_resume_callback()
    {
        var js = AppResumeJs;

        // Coordinate with PBI1's single resume path: exactly one Blazor callback
        // (OnAppResumed) drives the hub reset. No second resume entry point.
        Assert.Contains("OnAppResumed", js, StringComparison.Ordinal);

        var occurrences = CountOccurrences(js, "invokeMethodAsync('OnAppResumed')");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void AppResume_does_not_declare_a_second_dotnet_resume_method()
    {
        var js = AppResumeJs;

        // There must be no duplicate resume handling: the only .NET method invoked
        // for resume is OnAppResumed. Any additional invokeMethodAsync target would
        // indicate a competing resume path.
        Assert.DoesNotContain("OnAppFrozen", js, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAppHidden", js, StringComparison.Ordinal);
    }

    // ── manifest.json standalone-PWA hardening ───────────────────────────────

    [Fact]
    public void Manifest_uses_standalone_display_mode()
    {
        Assert.Contains("\"display\": \"standalone\"", ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_declares_scope_matching_the_mobile_base()
    {
        Assert.Contains("\"scope\": \"/mobile/\"", ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_start_url_is_the_mobile_base()
    {
        Assert.Contains("\"start_url\": \"/mobile/\"", ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_declares_an_id_for_a_stable_app_identity()
    {
        // A stable "id" keeps the installed app identity fixed even if start_url changes,
        // which is recommended for standalone PWAs.
        Assert.Contains("\"id\"", ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_declares_orientation()
    {
        Assert.Contains("\"orientation\"", ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexHtml_links_the_manifest()
    {
        var indexHtml = File.ReadAllText(Path.Combine(WwwrootPath, "index.html"));
        Assert.Contains("rel=\"manifest\"", indexHtml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
