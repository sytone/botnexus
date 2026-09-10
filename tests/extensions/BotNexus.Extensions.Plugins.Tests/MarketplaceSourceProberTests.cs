using System.Text.Json;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Probing a marketplace source. Uses real temp directories and a scripted fetcher: the fetcher
/// seam exists precisely so this needs no network and no git binary.
/// </summary>
public sealed class MarketplaceSourceProberTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bn-probe-tests-" + Guid.NewGuid().ToString("N"));

    private readonly ScriptedFetcher _fetcher = new();
    private readonly MarketplaceSourceProber _prober;

    public MarketplaceSourceProberTests()
    {
        Directory.CreateDirectory(_root);
        _prober = new MarketplaceSourceProber(_fetcher, new PluginManifestParser());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static MarketplaceSource Source(string url = "https://github.com/acme/things.git") => new()
    {
        Name = MarketplaceSourceStore.DeriveName(url),
        Url = url,
        AddedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static string Manifest(
        string name,
        string version = "1.0.0",
        string? description = "A plugin.",
        bool withExtension = false)
    {
        var manifest = new Dictionary<string, object?> { ["name"] = name };

        if (version is not null)
            manifest["version"] = version;

        if (description is not null)
            manifest["description"] = description;

        if (withExtension)
        {
            manifest["extension"] = new Dictionary<string, object?>
            {
                ["manifest"] = "extension/botnexus-extension.json",
            };
        }

        return JsonSerializer.Serialize(manifest);
    }

    private static string Catalog(params object[] plugins) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["name"] = "acme-catalog",
            ["owner"] = new Dictionary<string, object?> { ["name"] = "Acme" },
            ["plugins"] = plugins,
        });

    /// <summary>
    /// Builds a catalog entry. Absent fields are omitted rather than written as JSON null: the
    /// catalog schema types them as strings and forbids unknown properties, so a null is a
    /// schema violation and would fail the whole catalog rather than the one field.
    /// </summary>
    private static Dictionary<string, object?> Entry(
        string name, string source, string? version = null, string? description = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["source"] = source,
        };

        if (version is not null)
            entry["version"] = version;

        if (description is not null)
            entry["description"] = description;

        return entry;
    }

    // ── A repository that is itself a plugin ──────────────────────────────────

    [Fact]
    public async Task Probe_repository_carrying_a_plugin_manifest_reports_one_offering()
    {
        _fetcher.Add("https://github.com/acme/things.git", d =>
            WritePlugin(d, Manifest("acme-things", "2.3.0", "Does things.")));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Null(result.LastError);
        Assert.Equal("plugin", result.Kind);
        var offering = Assert.Single(result.Offerings);
        Assert.Equal("acme-things", offering.Name);
        Assert.Equal("2.3.0", offering.Version);
        Assert.Equal("Does things.", offering.Description);
        Assert.False(offering.CarriesExtension);
    }

    [Fact]
    public async Task Probe_reports_a_plugin_that_carries_an_extension()
    {
        _fetcher.Add("https://github.com/acme/things.git", d =>
            WritePlugin(d, Manifest("acme-things", withExtension: true)));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.True(Assert.Single(result.Offerings).CarriesExtension);
    }

    [Fact]
    public async Task Probe_records_the_refresh_time_on_success()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WritePlugin(d, Manifest("acme-things")));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.NotNull(result.LastRefreshedAtUtc);
    }

    // ── A repository carrying a catalog ───────────────────────────────────────

    [Fact]
    public async Task Probe_catalog_lists_every_entry()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git"),
            Entry("beta", "https://github.com/acme/beta.git"))));
        _fetcher.Add("https://github.com/acme/alpha.git", d => WritePlugin(d, Manifest("alpha")));
        _fetcher.Add("https://github.com/acme/beta.git", d => WritePlugin(d, Manifest("beta")));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Null(result.LastError);
        Assert.Equal("catalog", result.Kind);
        Assert.Equal(["alpha", "beta"], result.Offerings.Select(o => o.Name));
        Assert.All(result.Offerings, o => Assert.Null(o.Error));
    }

    /// <summary>
    /// The point of fetching each entry rather than trusting the catalog: a catalog that claims a
    /// plugin ships no code cannot make that true. Whether an install runs third-party code in the
    /// gateway is read from the plugin's own manifest, so a listing cannot understate it.
    /// </summary>
    [Fact]
    public async Task Catalog_entry_takes_its_facts_from_the_plugin_not_the_catalog()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git", description: "Harmless, honest."))));
        _fetcher.Add("https://github.com/acme/alpha.git", d =>
            WritePlugin(d, Manifest("alpha", "9.9.9", "Runs code in your gateway.", withExtension: true)));

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.True(offering.CarriesExtension);
        Assert.Equal("9.9.9", offering.Version);
        Assert.Equal("Runs code in your gateway.", offering.Description);
    }

    /// <summary>
    /// A catalog may describe a plugin that ships no description of its own. Filling that blank is
    /// useful and cannot mislead - the fields that decide what installing DOES are still read from
    /// the manifest alone.
    /// </summary>
    [Fact]
    public async Task The_catalog_description_fills_in_for_a_plugin_that_has_none()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git", description: "Described by the catalog."))));
        _fetcher.Add("https://github.com/acme/alpha.git", d =>
            WritePlugin(d, Manifest("alpha", description: null)));

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.Equal("Described by the catalog.", offering.Description);
        Assert.Null(offering.Error);
    }

    [Fact]
    public async Task The_plugins_own_description_still_wins_over_the_catalogs()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git", description: "The catalog's words."))));
        _fetcher.Add("https://github.com/acme/alpha.git", d =>
            WritePlugin(d, Manifest("alpha", description: "The plugin's own words.")));

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.Equal("The plugin's own words.", offering.Description);
    }

    [Fact]
    public async Task An_entry_described_by_neither_has_no_description()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git"))));
        _fetcher.Add("https://github.com/acme/alpha.git", d =>
            WritePlugin(d, Manifest("alpha", description: null)));

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.Null(offering.Description);
    }

    /// <summary>
    /// The fallback is the description alone. A catalog must not be able to influence what
    /// installing does, so the version on the listing stays the manifest's.
    /// </summary>
    [Fact]
    public async Task The_catalog_does_not_supply_the_version_shown_on_a_readable_entry()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git", version: "9.9.9"))));
        _fetcher.Add("https://github.com/acme/alpha.git", d =>
            WritePlugin(d, Manifest("alpha", version: "1.2.3")));

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.Equal("1.2.3", offering.Version);
    }

    [Fact]
    public async Task A_catalog_is_preferred_over_a_plugin_manifest_in_the_same_repository()
    {
        _fetcher.Add("https://github.com/acme/things.git", d =>
        {
            WriteCatalog(d, Catalog(Entry("alpha", "https://github.com/acme/alpha.git")));
            WritePlugin(d, Manifest("things-itself"));
        });
        _fetcher.Add("https://github.com/acme/alpha.git", d => WritePlugin(d, Manifest("alpha")));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Equal("catalog", result.Kind);
        Assert.Equal("alpha", Assert.Single(result.Offerings).Name);
    }

    [Fact]
    public async Task A_catalog_with_no_plugins_succeeds_with_no_offerings()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog()));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Null(result.LastError);
        Assert.Equal("catalog", result.Kind);
        Assert.Empty(result.Offerings);
    }

    // ── One bad entry must not lose the others ───────────────────────────────

    [Fact]
    public async Task An_unreachable_entry_is_listed_with_its_error_and_the_others_survive()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("broken", "https://github.com/acme/broken.git", "1.0.0", "Claimed."),
            Entry("alpha", "https://github.com/acme/alpha.git"))));
        _fetcher.Fail("https://github.com/acme/broken.git", "repository not found");
        _fetcher.Add("https://github.com/acme/alpha.git", d => WritePlugin(d, Manifest("alpha")));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Null(result.LastError);
        Assert.Equal(2, result.Offerings.Count);

        var broken = result.Offerings.First(o => o.Name == "broken");
        Assert.Contains("repository not found", broken.Error);
        // The catalog's claims remain as the fallback listing rather than the entry vanishing.
        Assert.Equal("Claimed.", broken.Description);

        Assert.Null(result.Offerings.First(o => o.Name == "alpha").Error);
    }

    [Fact]
    public async Task An_entry_whose_repository_is_not_a_plugin_is_listed_with_its_error()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("empty", "https://github.com/acme/empty.git"))));
        _fetcher.Add("https://github.com/acme/empty.git", _ => { });

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.Contains("plugin.json", offering.Error);
    }

    [Fact]
    public async Task An_entry_with_a_malformed_manifest_is_listed_with_its_error()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("bad", "https://github.com/acme/bad.git"))));
        _fetcher.Add("https://github.com/acme/bad.git", d => WritePlugin(d, "{ not json"));

        var offering = Assert.Single((await _prober.ProbeAsync(Source(), _root)).Offerings);

        Assert.NotNull(offering.Error);
    }

    // ── Sources that cannot be read ──────────────────────────────────────────

    [Fact]
    public async Task An_unreachable_source_records_the_error_rather_than_throwing()
    {
        _fetcher.Fail("https://github.com/acme/things.git", "could not resolve host");

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Contains("could not resolve host", result.LastError);
    }

    [Fact]
    public async Task A_repository_that_is_neither_shape_says_so_plainly()
    {
        _fetcher.Add("https://github.com/acme/things.git", _ => { });

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Contains("marketplace.json", result.LastError);
        Assert.Contains("plugin.json", result.LastError);
    }

    [Fact]
    public async Task A_malformed_catalog_records_the_error()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, "{ not json"));

        var result = await _prober.ProbeAsync(Source(), _root);

        Assert.Contains("marketplace.json", result.LastError);
    }

    /// <summary>
    /// A source that worked yesterday and is unreachable today can still be installed from, so a
    /// failed probe keeps the previous offerings and leaves the refresh time where it was - that
    /// timestamp means "when this content was read", and a failed probe read nothing.
    /// </summary>
    [Fact]
    public async Task A_failed_probe_keeps_the_previous_offerings_and_refresh_time()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WritePlugin(d, Manifest("acme-things")));
        var good = await _prober.ProbeAsync(Source(), _root);

        _fetcher.Fail("https://github.com/acme/things.git", "network down");
        var stale = await _prober.ProbeAsync(good, _root);

        Assert.Equal("acme-things", Assert.Single(stale.Offerings).Name);
        Assert.Equal(good.LastRefreshedAtUtc, stale.LastRefreshedAtUtc);
        Assert.Contains("network down", stale.LastError);
    }

    [Fact]
    public async Task A_successful_probe_clears_a_previous_error()
    {
        _fetcher.Fail("https://github.com/acme/things.git", "network down");
        var failed = await _prober.ProbeAsync(Source(), _root);
        Assert.NotNull(failed.LastError);

        _fetcher.Add("https://github.com/acme/things.git", d => WritePlugin(d, Manifest("acme-things")));
        var recovered = await _prober.ProbeAsync(failed, _root);

        Assert.Null(recovered.LastError);
    }

    // ── Housekeeping ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Probing_leaves_no_staging_directories_behind()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WriteCatalog(d, Catalog(
            Entry("alpha", "https://github.com/acme/alpha.git"),
            Entry("broken", "https://github.com/acme/broken.git"))));
        _fetcher.Add("https://github.com/acme/alpha.git", d => WritePlugin(d, Manifest("alpha")));
        _fetcher.Fail("https://github.com/acme/broken.git", "gone");

        await _prober.ProbeAsync(Source(), _root);

        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task The_configured_reference_is_passed_to_the_fetcher()
    {
        _fetcher.Add("https://github.com/acme/things.git", d => WritePlugin(d, Manifest("acme-things")));

        await _prober.ProbeAsync(Source() with { Reference = "v2" }, _root);

        Assert.Equal("v2", _fetcher.ReferenceFor("https://github.com/acme/things.git"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void WritePlugin(string directory, string manifestJson)
    {
        var dir = Path.Combine(directory, ".botnexus-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
    }

    private static void WriteCatalog(string directory, string catalogJson) =>
        File.WriteAllText(Path.Combine(directory, "marketplace.json"), catalogJson);

    /// <summary>A fetcher that writes known content, or fails, per source URL.</summary>
    private sealed class ScriptedFetcher : IPluginSourceFetcher
    {
        private readonly Dictionary<string, Action<string>> _writers = [];
        private readonly Dictionary<string, string> _failures = [];
        private readonly Dictionary<string, string?> _references = [];

        public void Add(string url, Action<string> write)
        {
            _writers[url] = write;
            _failures.Remove(url);
        }

        public void Fail(string url, string message)
        {
            _failures[url] = message;
            _writers.Remove(url);
        }

        public string? ReferenceFor(string url) => _references.GetValueOrDefault(url);

        public Task<PluginFetchResult> FetchAsync(
            string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            _references[source] = reference;

            if (_failures.TryGetValue(source, out var message))
                throw new InvalidOperationException(message);

            if (!_writers.TryGetValue(source, out var write))
                throw new InvalidOperationException($"no script for {source}");

            write(stagingDirectory);
            return Task.FromResult(new PluginFetchResult("deadbeef"));
        }
    }
}
