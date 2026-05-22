using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Verifies that PWA assets are present and correctly configured for the desktop Blazor client.
/// These tests assert the static file requirements for installability.
/// </summary>
public class PwaAssetsTests
{
    /// <summary>
    /// Walks up from AppContext.BaseDirectory until we find the solution root
    /// (identified by the presence of BotNexus.slnx), then navigates to wwwroot.
    /// </summary>
    private static string FindWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
            throw new InvalidOperationException("Could not find solution root (BotNexus.slnx)");
        return Path.Combine(
            dir.FullName,
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot");
    }

    private static readonly string WwwrootPath = FindWwwroot();

    [Fact]
    public void ManifestWebmanifest_Exists()
    {
        var manifestPath = Path.Combine(WwwrootPath, "manifest.webmanifest");
        Assert.True(File.Exists(manifestPath), $"manifest.webmanifest not found at: {manifestPath}");
    }

    [Fact]
    public void ManifestWebmanifest_IsValidJson()
    {
        var manifestPath = Path.Combine(WwwrootPath, "manifest.webmanifest");
        var content = File.ReadAllText(manifestPath);
        var doc = JsonDocument.Parse(content);
        Assert.NotNull(doc);
    }

    [Fact]
    public void ManifestWebmanifest_HasRequiredFields()
    {
        var manifestPath = Path.Combine(WwwrootPath, "manifest.webmanifest");
        var content = File.ReadAllText(manifestPath);
        var doc = JsonDocument.Parse(content).RootElement;

        Assert.True(doc.TryGetProperty("name", out var name), "manifest missing 'name'");
        Assert.False(string.IsNullOrEmpty(name.GetString()), "manifest 'name' is empty");

        Assert.True(doc.TryGetProperty("short_name", out var shortName), "manifest missing 'short_name'");
        Assert.False(string.IsNullOrEmpty(shortName.GetString()), "manifest 'short_name' is empty");

        Assert.True(doc.TryGetProperty("start_url", out _), "manifest missing 'start_url'");
        Assert.True(doc.TryGetProperty("display", out _), "manifest missing 'display'");
        Assert.True(doc.TryGetProperty("icons", out var icons), "manifest missing 'icons'");
        Assert.True(icons.GetArrayLength() > 0, "manifest 'icons' array is empty");
    }

    [Fact]
    public void ManifestWebmanifest_HasIcon192And512()
    {
        var manifestPath = Path.Combine(WwwrootPath, "manifest.webmanifest");
        var content = File.ReadAllText(manifestPath);
        var doc = JsonDocument.Parse(content).RootElement;

        var icons = doc.GetProperty("icons");
        var sizes = icons.EnumerateArray()
            .Select(i => i.GetProperty("sizes").GetString())
            .ToList();

        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
    }

    [Fact]
    public void ServiceWorkerJs_Exists()
    {
        var swPath = Path.Combine(WwwrootPath, "service-worker.js");
        Assert.True(File.Exists(swPath), $"service-worker.js not found at: {swPath}");
    }

    [Fact]
    public void ServiceWorkerPublishedJs_Exists()
    {
        var swPath = Path.Combine(WwwrootPath, "service-worker.published.js");
        Assert.True(File.Exists(swPath), $"service-worker.published.js not found at: {swPath}");
    }

    [Fact]
    public void Icon192_Exists()
    {
        var iconPath = Path.Combine(WwwrootPath, "icon-192.png");
        Assert.True(File.Exists(iconPath), $"icon-192.png not found at: {iconPath}");
    }

    [Fact]
    public void Icon512_Exists()
    {
        var iconPath = Path.Combine(WwwrootPath, "icon-512.png");
        Assert.True(File.Exists(iconPath), $"icon-512.png not found at: {iconPath}");
    }

    [Fact]
    public void IndexHtml_HasManifestLink()
    {
        var indexPath = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(indexPath);
        Assert.Contains("manifest.webmanifest", content);
    }

    [Fact]
    public void IndexHtml_HasThemeColorMeta()
    {
        var indexPath = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(indexPath);
        Assert.Contains("theme-color", content);
    }

    [Fact]
    public void IndexHtml_HasAppleMobileWebAppCapable()
    {
        var indexPath = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(indexPath);
        Assert.Contains("apple-mobile-web-app-capable", content);
    }

    [Fact]
    public void IndexHtml_HasAppleTouchIcon()
    {
        var indexPath = Path.Combine(WwwrootPath, "index.html");
        var content = File.ReadAllText(indexPath);
        Assert.Contains("apple-touch-icon", content);
    }
}
