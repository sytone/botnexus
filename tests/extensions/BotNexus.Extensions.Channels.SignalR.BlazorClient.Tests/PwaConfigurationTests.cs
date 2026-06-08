using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Validates PWA configuration files exist and contain required entries
/// for the main BlazorClient portal.
/// </summary>
public sealed class PwaConfigurationTests
{
    // AppContext.BaseDirectory is tests/extensions/...Tests/bin/Debug/net10.0/
    // Go up 6 levels to reach repo root, then into src/extensions/...
    private static readonly string WwwrootPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot"));

    private static readonly string CsprojPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.csproj"));

    [Fact]
    public void Manifest_file_exists()
    {
        var path = Path.Combine(WwwrootPath, "manifest.webmanifest");
        Assert.True(File.Exists(path), $"manifest.webmanifest not found at {path}");
    }

    [Fact]
    public void Manifest_contains_required_fields()
    {
        var path = Path.Combine(WwwrootPath, "manifest.webmanifest");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("name", out var name), "Missing 'name'");
        Assert.Equal("BotNexus", name.GetString());

        Assert.True(root.TryGetProperty("short_name", out var shortName), "Missing 'short_name'");
        Assert.Equal("BotNexus", shortName.GetString());

        Assert.True(root.TryGetProperty("start_url", out var startUrl), "Missing 'start_url'");
        Assert.Equal("/", startUrl.GetString());

        Assert.True(root.TryGetProperty("display", out var display), "Missing 'display'");
        Assert.Equal("standalone", display.GetString());

        Assert.True(root.TryGetProperty("icons", out var icons), "Missing 'icons'");
        Assert.True(icons.GetArrayLength() >= 1, "At least one icon required");
    }

    [Fact]
    public void ServiceWorker_dev_file_exists()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.js");
        Assert.True(File.Exists(path), $"service-worker.js not found at {path}");
    }

    [Fact]
    public void ServiceWorker_dev_passes_navigate_requests_through()
    {
        var path = Path.Combine(WwwrootPath, "service-worker.js");
        var content = File.ReadAllText(path);

        // Dev service worker should pass through navigate requests
        Assert.Contains("navigate", content);
    }

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
    public void IndexHtml_contains_manifest_link()
    {
        var path = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(path);

        Assert.Contains("rel=\"manifest\"", content);
        Assert.Contains("manifest.webmanifest", content);
    }

    [Fact]
    public void IndexHtml_contains_theme_color_meta()
    {
        var path = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(path);

        Assert.Contains("name=\"theme-color\"", content);
    }

    [Fact]
    public void IndexHtml_contains_apple_mobile_web_app_meta()
    {
        var path = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(path);

        Assert.Contains("apple-mobile-web-app-capable", content);
    }

    [Fact]
    public void Csproj_contains_service_worker_manifest_property()
    {
        var content = File.ReadAllText(CsprojPath);

        Assert.Contains("ServiceWorkerAssetsManifest", content);
        Assert.Contains("service-worker-assets.js", content);
    }

    [Fact]
    public void Csproj_contains_service_worker_include()
    {
        var content = File.ReadAllText(CsprojPath);

        Assert.Contains("ServiceWorker", content);
        Assert.Contains("service-worker.js", content);
    }
}
