using System.IO;
using System.Reflection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Content-level tests verifying the service worker respects HTTP auth challenge
/// semantics by bypassing the cache for top-level navigations (#688).
/// </summary>
public sealed class ServiceWorkerTests
{
    private static readonly string s_serviceWorkerPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "wwwroot",
        "service-worker.js");

    [Fact]
    public void ServiceWorker_HasNavigateBypass_BeforeApiBypass()
    {
        // Arrange
        var content = File.ReadAllText(s_serviceWorkerPath);

        // Act
        var navigateIdx = content.IndexOf("event.request.mode === 'navigate'", StringComparison.Ordinal);
        var apiIdx = content.IndexOf("url.pathname.startsWith('/api/')", StringComparison.Ordinal);

        // Assert: both guards are present
        Assert.True(navigateIdx >= 0, "service-worker.js must contain the navigate-mode bypass guard");
        Assert.True(apiIdx >= 0, "service-worker.js must contain the /api/ pass-through guard");

        // Navigate bypass should appear before the URL-based guards so top-level
        // navigations are short-circuited before URL parsing.
        Assert.True(navigateIdx < apiIdx,
            "navigate-mode bypass must appear before the /api/ URL guard to short-circuit navigate requests early");
    }

    [Fact]
    public void ServiceWorker_NavigateBypass_IsInsideFetchHandler()
    {
        var content = File.ReadAllText(s_serviceWorkerPath);

        // The fetch listener body must contain the navigate guard
        var fetchListenerStart = content.IndexOf("addEventListener('fetch'", StringComparison.Ordinal);
        var navigateIdx = content.IndexOf("event.request.mode === 'navigate'", StringComparison.Ordinal);

        Assert.True(fetchListenerStart >= 0, "service-worker.js must have a fetch event listener");
        Assert.True(navigateIdx > fetchListenerStart,
            "navigate-mode bypass must be inside the fetch event listener body");
    }

    [Fact]
    public void ServiceWorker_NavigateBypass_Returns_WhenModeIsNavigate()
    {
        var content = File.ReadAllText(s_serviceWorkerPath);

        // The guard must return (not just log or continue)
        // Pattern: if (event.request.mode === 'navigate') return;
        Assert.Contains("if (event.request.mode === 'navigate') return;", content,
            StringComparison.Ordinal);
    }
}
