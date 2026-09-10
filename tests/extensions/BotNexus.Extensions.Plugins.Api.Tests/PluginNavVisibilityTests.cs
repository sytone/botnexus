using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BotNexus.Extensions.Plugins.Api.Tests;

/// <summary>
/// Pins the per-plugin menu-visibility preference.
/// </summary>
/// <remarks>
/// The preference is read back through a FRESH store, because a toggle that only mutated in-memory
/// state would satisfy a same-instance read and still be lost on restart - which is exactly the lie
/// a persistence control must not tell. Hiding is presentation only: the assertions check that the
/// plugin, its files and its deployed extension are all untouched.
/// </remarks>
public sealed class PluginNavVisibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-nav-visibility", Guid.NewGuid().ToString("N"));

    public PluginNavVisibilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Seed(bool navHidden = false) =>
        new PluginStateStore(_root).Upsert(new InstalledPlugin
        {
            Name = "code-plugin",
            Source = "https://example.com/x.git",
            ResolvedVersion = "abc123",
            InstalledAtUtc = DateTimeOffset.UnixEpoch,
            Files = ["botnexus-extension.json", "lib/x.dll"],
            DeployedExtensionId = "carried-ext",
            ExtensionFiles = ["lib/x.dll"],
            NavHidden = navHidden,
        });

    [Fact]
    public void Hiding_persists_to_a_fresh_store()
    {
        Seed();

        var result = PluginsEndpointContributor.SetNavVisibility(
            "code-plugin", new PluginNavVisibilityRequest(NavHidden: true), _root);

        Assert.IsType<Ok<PluginPortalRow>>(result);
        Assert.True(new PluginStateStore(_root).Find("code-plugin")!.NavHidden);
    }

    [Fact]
    public void Showing_again_persists_too()
    {
        Seed(navHidden: true);

        PluginsEndpointContributor.SetNavVisibility(
            "code-plugin", new PluginNavVisibilityRequest(NavHidden: false), _root);

        Assert.False(new PluginStateStore(_root).Find("code-plugin")!.NavHidden);
    }

    // Presentation only. Hiding must not disturb what the plugin owns, or removal would later
    // orphan the files it no longer claims.
    [Fact]
    public void Hiding_leaves_the_recorded_content_and_deployment_untouched()
    {
        Seed();

        PluginsEndpointContributor.SetNavVisibility(
            "code-plugin", new PluginNavVisibilityRequest(NavHidden: true), _root);

        var record = new PluginStateStore(_root).Find("code-plugin")!;
        Assert.Equal(["botnexus-extension.json", "lib/x.dll"], record.Files);
        Assert.Equal("carried-ext", record.DeployedExtensionId);
        Assert.Equal(["lib/x.dll"], record.ExtensionFiles);
        Assert.Equal("abc123", record.ResolvedVersion);
    }

    [Fact]
    public void The_returned_row_reports_the_new_state_and_its_extension()
    {
        Seed();

        var result = Assert.IsType<Ok<PluginPortalRow>>(PluginsEndpointContributor.SetNavVisibility(
            "code-plugin", new PluginNavVisibilityRequest(NavHidden: true), _root));

        Assert.True(result.Value!.NavHidden);
        Assert.Equal("carried-ext", result.Value.DeployedExtensionId);
    }

    [Fact]
    public void An_unknown_plugin_is_not_found()
    {
        var result = PluginsEndpointContributor.SetNavVisibility(
            "ghost", new PluginNavVisibilityRequest(NavHidden: true), _root);

        Assert.Equal(StatusCodes.Status404NotFound,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
