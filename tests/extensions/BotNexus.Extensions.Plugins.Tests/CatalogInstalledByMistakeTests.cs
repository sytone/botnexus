using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Installing a marketplace catalog URL by mistake.
/// </summary>
/// <remarks>
/// The two inputs sit next to each other on the Plugins page, so pasting a catalog into the
/// install box is an easy slip. Install is right to refuse - a catalog has no plugin manifest -
/// but "manifest not found" sends someone hunting in their own repository for a file that was
/// never supposed to be there.
/// </remarks>
public sealed class CatalogInstalledByMistakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bn-catinst-" + Guid.NewGuid().ToString("N"));
    private readonly StubFetcher _fetcher = new();

    public CatalogInstalledByMistakeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private PluginLifecycleManager Manager() =>
        new(new PluginStateStore(_root), _fetcher, extensionsRoot: Path.Combine(_root, "extensions"));

    [Fact]
    public async Task Installing_a_catalog_says_it_is_a_catalog_and_where_it_belongs()
    {
        _fetcher.Writes = d => File.WriteAllText(Path.Combine(d, "marketplace.json"), """
            { "name": "acme", "owner": { "name": "Acme" }, "plugins": [] }
            """);

        var result = await Manager().InstallAsync(new PluginInstallRequest
        {
            Source = "https://github.com/acme/catalog.git",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        var message = string.Join(" ", result.Errors.Select(e => e.Message));

        Assert.Contains("marketplace catalog", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Repositories", message, StringComparison.OrdinalIgnoreCase);
        // The old message named a temp path and a missing file, which is what misled.
        Assert.DoesNotContain("Plugin manifest not found", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A repository that is neither must keep the ordinary error - the catalog wording would be a
    /// confident wrong answer for someone who simply forgot the manifest.
    /// </summary>
    [Fact]
    public async Task A_repository_that_is_not_a_catalog_keeps_the_ordinary_manifest_error()
    {
        _fetcher.Writes = d => File.WriteAllText(Path.Combine(d, "README.md"), "nothing here");

        var result = await Manager().InstallAsync(new PluginInstallRequest
        {
            Source = "https://github.com/acme/empty.git",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        var message = string.Join(" ", result.Errors.Select(e => e.Message));

        Assert.Contains(".botnexus-plugin/plugin.json", message);
        Assert.DoesNotContain("marketplace catalog", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_real_plugin_still_installs()
    {
        _fetcher.Writes = d =>
        {
            var dir = Path.Combine(d, ".botnexus-plugin");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), """
                { "name": "acme-thing", "version": "1.0.0" }
                """);
        };

        var result = await Manager().InstallAsync(new PluginInstallRequest
        {
            Source = "https://github.com/acme/thing.git",
        });

        Assert.Equal(PluginOperationOutcome.Installed, result.Outcome);
    }

    private sealed class StubFetcher : IPluginSourceFetcher
    {
        public Action<string> Writes { get; set; } = _ => { };

        public Task<PluginFetchResult> FetchAsync(
            string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            Writes(stagingDirectory);
            return Task.FromResult(new PluginFetchResult("deadbeef"));
        }
    }
}
