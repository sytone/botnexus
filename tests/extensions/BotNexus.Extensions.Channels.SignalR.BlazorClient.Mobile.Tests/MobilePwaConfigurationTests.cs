using System;
using System.IO;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Validates PWA / service-worker configuration files exist and contain the
/// required entries for the mobile BlazorClient portal (#1780).
/// </summary>
public sealed class MobilePwaConfigurationTests
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

    private static readonly string CsprojPath = Path.Combine(
        ProjectRoot,
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.csproj");

    [Fact]
    public void ServiceWorker_published_file_exists()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.published.js");
        Assert.True(File.Exists(path), $"service-worker.published.js not found at {path}");
    }

    [Fact]
    public void ServiceWorker_published_imports_assets_manifest()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.published.js");
        var content = File.ReadAllText(path);

        Assert.Contains("service-worker-assets.js", content);
    }

    [Fact]
    public void ServiceWorker_published_bypasses_signalr_and_api_requests()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.published.js");
        var content = File.ReadAllText(path);

        // Must not cache SignalR or API requests
        Assert.Contains("/hub/", content);
        Assert.Contains("/api/", content);
    }

    [Fact]
    public void ServiceWorker_published_passes_navigate_requests_through()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.published.js");
        var content = File.ReadAllText(path);

        Assert.Contains("event.request.mode === 'navigate'", content);
    }

    [Fact]
    public void ServiceWorker_published_uses_mobile_cache_prefix()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.published.js");
        var content = File.ReadAllText(path);

        // Mobile SW must use a distinct cache-name prefix so it does not collide
        // with the desktop SW (which uses 'botnexus-offline-').
        Assert.Contains("botnexus-mobile-offline-", content);
    }

    [Fact]
    public void IndexHtml_registers_service_worker()
    {
        var path = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(path);

        Assert.Contains("navigator.serviceWorker.register", content);
    }

    [Fact]
    public void Csproj_uses_published_content_service_worker_form()
    {
        var content = File.ReadAllText(CsprojPath);

        Assert.Contains("ServiceWorker", content);
        Assert.Contains("PublishedContent=\"wwwroot\\service-worker.published.js\"", content);
    }
}
