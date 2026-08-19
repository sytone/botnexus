using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BotNexus.Extensions.Plugins.Api.Tests;

/// <summary>
/// Tests the plugins read/preference API backing the portal plugins page (#2687).
/// </summary>
/// <remarks>
/// The persistence assertions read the preference back through a FRESH <see cref="PluginStateStore"/>.
/// A toggle that only mutated in-memory state would satisfy a same-instance read and still lose the
/// preference on restart, which is precisely the failure AC3 exists to prevent.
/// </remarks>
public sealed class PluginsEndpointContributorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-plugins-api-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void List_returns_every_installed_plugin_ordered_by_name()
    {
        Seed(Record("zeta"), Record("alpha"));

        var result = Assert.IsType<Ok<IReadOnlyList<PluginPortalRow>>>(PluginsEndpointContributor.List(_root));

        Assert.Equal(["alpha", "zeta"], result.Value!.Select(r => r.Name));
    }

    [Fact]
    public void List_returns_an_empty_collection_when_nothing_is_installed()
    {
        var result = Assert.IsType<Ok<IReadOnlyList<PluginPortalRow>>>(PluginsEndpointContributor.List(_root));

        Assert.Empty(result.Value!);
    }

    [Fact]
    public void Get_returns_the_named_plugin()
    {
        Seed(Record("alpha"));

        var result = Assert.IsType<Ok<PluginPortalRow>>(PluginsEndpointContributor.Get("alpha", _root));

        Assert.Equal("alpha", result.Value!.Name);
    }

    [Fact]
    public void Get_returns_404_for_a_plugin_that_is_not_installed()
    {
        Seed(Record("alpha"));

        var result = PluginsEndpointContributor.Get("nope", _root);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void Setting_the_update_preference_persists_it_to_the_installed_record()
    {
        Seed(Record("alpha"));

        var result = Assert.IsType<Ok<PluginPortalRow>>(
            PluginsEndpointContributor.SetUpdatePreference("alpha", new PluginUpdatePreferenceRequest(false), _root));

        Assert.False(result.Value!.UpdatesEnabled);
        // Read back through a NEW store: this is the assertion that distinguishes a persisted
        // preference from one that only ever lived in the request's own instance.
        Assert.False(new PluginStateStore(_root).Find("alpha")!.UpdatesEnabled);
    }

    [Fact]
    public void Re_enabling_the_update_preference_persists_too()
    {
        Seed(Record("alpha") with { UpdatesEnabled = false });

        PluginsEndpointContributor.SetUpdatePreference("alpha", new PluginUpdatePreferenceRequest(true), _root);

        Assert.True(new PluginStateStore(_root).Find("alpha")!.UpdatesEnabled);
    }

    [Fact]
    public void A_pinned_plugin_reports_the_pinned_update_state_after_the_toggle()
    {
        Seed(Record("alpha"));

        var result = Assert.IsType<Ok<PluginPortalRow>>(
            PluginsEndpointContributor.SetUpdatePreference("alpha", new PluginUpdatePreferenceRequest(false), _root));

        Assert.Equal(PluginUpdateState.Pinned, result.Value!.UpdateState);
    }

    [Fact]
    public void Setting_the_preference_preserves_the_rest_of_the_installed_record()
    {
        var original = Record("alpha") with { Files = ["a.md", "b.md"], ManifestVersion = "3.0.0" };
        Seed(original);

        PluginsEndpointContributor.SetUpdatePreference("alpha", new PluginUpdatePreferenceRequest(false), _root);

        var persisted = new PluginStateStore(_root).Find("alpha")!;
        // The recorded file set is the only description of what the plugin owns; a preference
        // write that dropped it would orphan every file the install wrote.
        Assert.Equal(["a.md", "b.md"], persisted.Files);
        Assert.Equal("3.0.0", persisted.ManifestVersion);
        Assert.Equal(original.ResolvedVersion, persisted.ResolvedVersion);
        Assert.Equal(original.InstalledAtUtc, persisted.InstalledAtUtc);
    }

    [Fact]
    public void Setting_the_preference_on_an_unknown_plugin_is_404_and_writes_nothing()
    {
        Seed(Record("alpha"));

        var result = PluginsEndpointContributor.SetUpdatePreference(
            "nope", new PluginUpdatePreferenceRequest(false), _root);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.True(new PluginStateStore(_root).Find("alpha")!.UpdatesEnabled);
    }

    [Fact]
    public void A_blank_plugin_name_is_rejected_as_a_bad_request()
    {
        var result = PluginsEndpointContributor.SetUpdatePreference(
            "  ", new PluginUpdatePreferenceRequest(false), _root);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void The_plugin_root_honours_the_botnexus_home_override()
    {
        var previous = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        try
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _root);

            Assert.Equal(
                Path.Combine(Path.GetFullPath(_root), "plugins"),
                PluginsEndpointContributor.GetPluginRootPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", previous);
        }
    }

    private void Seed(params InstalledPlugin[] plugins)
    {
        new PluginStateStore(_root).Write(plugins);
        foreach (var plugin in plugins)
        {
            foreach (var relative in plugin.Files)
            {
                var path = Path.Combine(_root, plugin.Name, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "content");
            }
        }
    }

    private static InstalledPlugin Record(string name) => new()
    {
        Name = name,
        Source = $"https://example.com/{name}.git",
        ResolvedVersion = "abcdef0123456789",
        InstalledAtUtc = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        Files = ["one.md"],
    };
}
