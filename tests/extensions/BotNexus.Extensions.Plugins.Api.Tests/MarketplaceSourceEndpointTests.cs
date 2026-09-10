using System.Text.Json;
using BotNexus.Extensions.Plugins.Lifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BotNexus.Extensions.Plugins.Api.Tests;

/// <summary>
/// Pins the marketplace source routes: add, list, refresh and remove.
/// </summary>
/// <remarks>
/// Every write is read back through a FRESH store, for the same reason the nav-visibility tests
/// do it: a route that only mutated memory would satisfy a same-instance read and still lose the
/// source on restart.
/// </remarks>
public sealed class MarketplaceSourceEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-mp-sources", Guid.NewGuid().ToString("N"));

    private readonly string _staging;
    private readonly ScriptedFetcher _fetcher = new();
    private readonly MarketplaceSourceProber _prober;

    private const string Url = "https://github.com/acme/things.git";

    public MarketplaceSourceEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_staging);
        _prober = new MarketplaceSourceProber(_fetcher, new PluginManifestParser());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task<IResult> AddAsync(string url = Url, string? name = null, string? reference = null) =>
        MarketplaceSourceEndpoints.AddAsync(
            new MarketplaceSourceRequest(url, name, reference), _root, _prober, _staging);

    private IReadOnlyList<MarketplaceSource> Stored() => new MarketplaceSourceStore(_root).Read();

    // ── Adding ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Adding_a_source_stores_it_and_reports_what_it_offers()
    {
        _fetcher.AddPlugin(Url, "acme-things", "2.0.0");

        var result = await AddAsync();

        var created = Assert.IsType<Created<MarketplaceSource>>(result);
        Assert.Equal("plugin", created.Value!.Kind);
        Assert.Equal("acme-things", Assert.Single(created.Value.Offerings).Name);

        var stored = Assert.Single(Stored());
        Assert.Equal("acme-things", Assert.Single(stored.Offerings).Name);
    }

    [Fact]
    public async Task Adding_derives_a_name_from_the_url()
    {
        _fetcher.AddPlugin(Url, "acme-things");

        await AddAsync();

        Assert.Equal(MarketplaceSourceStore.DeriveName(Url), Assert.Single(Stored()).Name);
    }

    [Fact]
    public async Task An_explicit_name_is_used_instead_of_the_derived_one()
    {
        _fetcher.AddPlugin(Url, "acme-things");

        await AddAsync(name: "my-things");

        Assert.Equal("my-things", Assert.Single(Stored()).Name);
    }

    [Fact]
    public async Task The_reference_is_stored_and_used_when_reading()
    {
        _fetcher.AddPlugin(Url, "acme-things");

        await AddAsync(reference: "v2");

        Assert.Equal("v2", Assert.Single(Stored()).Reference);
        Assert.Equal("v2", _fetcher.ReferenceFor(Url));
    }

    /// <summary>
    /// A first read can fail for reasons unrelated to the URL, so the source is kept with its
    /// error rather than discarded - a bad URL is one delete away, a good source lost to a flaky
    /// network is not recoverable by the operator.
    /// </summary>
    [Fact]
    public async Task A_source_that_cannot_be_read_is_still_stored_with_its_error()
    {
        _fetcher.Fail(Url, "could not resolve host");

        var result = await AddAsync();

        var created = Assert.IsType<Created<MarketplaceSource>>(result);
        Assert.Contains("could not resolve host", created.Value!.LastError);
        Assert.Contains("could not resolve host", Assert.Single(Stored()).LastError);
    }

    [Fact]
    public async Task Adding_the_same_source_twice_is_a_conflict()
    {
        _fetcher.AddPlugin(Url, "acme-things");
        await AddAsync();

        var result = await AddAsync();

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Single(Stored());
    }

    [Fact]
    public async Task A_missing_url_is_rejected()
    {
        var result = await MarketplaceSourceEndpoints.AddAsync(
            new MarketplaceSourceRequest("  "), _root, _prober, _staging);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(Stored());
    }

    /// <summary>
    /// The URL is handed to git, so a non-http scheme would let a request name a local path and
    /// have the gateway read a directory the caller cannot otherwise reach.
    /// </summary>
    [Theory]
    [InlineData("file:///etc")]
    [InlineData("/var/lib/secrets")]
    [InlineData("git@github.com:acme/things.git")]
    [InlineData("ftp://example.com/x.git")]
    [InlineData("../../etc")]
    public async Task A_url_that_is_not_http_is_rejected(string url)
    {
        var result = await AddAsync(url);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(Stored());
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Listing_with_no_sources_is_an_empty_list_not_an_error()
    {
        var ok = Assert.IsType<Ok<List<MarketplaceSource>>>(MarketplaceSourceEndpoints.List(_root));

        Assert.Empty(ok.Value!);
    }

    [Fact]
    public async Task Listing_returns_sources_ordered_by_name()
    {
        _fetcher.AddPlugin("https://github.com/acme/zebra.git", "zebra");
        _fetcher.AddPlugin("https://github.com/acme/alpha.git", "alpha");
        await AddAsync("https://github.com/acme/zebra.git");
        await AddAsync("https://github.com/acme/alpha.git");

        var ok = Assert.IsType<Ok<List<MarketplaceSource>>>(MarketplaceSourceEndpoints.List(_root));

        Assert.Equal(["acme-alpha", "acme-zebra"], ok.Value!.Select(s => s.Name));
    }

    // ── Refreshing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Refreshing_picks_up_a_new_version()
    {
        _fetcher.AddPlugin(Url, "acme-things", "1.0.0");
        await AddAsync();

        _fetcher.AddPlugin(Url, "acme-things", "1.1.0");
        var result = await MarketplaceSourceEndpoints.RefreshAsync(
            MarketplaceSourceStore.DeriveName(Url), _root, _prober, _staging);

        var ok = Assert.IsType<Ok<MarketplaceSource>>(result);
        Assert.Equal("1.1.0", Assert.Single(ok.Value!.Offerings).Version);
        Assert.Equal("1.1.0", Assert.Single(Assert.Single(Stored()).Offerings).Version);
    }

    /// <summary>
    /// The call succeeded - the gateway asked and recorded the answer. An error status would
    /// leave the portal unable to tell "this source is unreachable" from "the refresh broke".
    /// </summary>
    [Fact]
    public async Task Refreshing_an_unreachable_source_is_ok_with_the_error_on_the_source()
    {
        _fetcher.AddPlugin(Url, "acme-things", "1.0.0");
        await AddAsync();

        _fetcher.Fail(Url, "network down");
        var result = await MarketplaceSourceEndpoints.RefreshAsync(
            MarketplaceSourceStore.DeriveName(Url), _root, _prober, _staging);

        var ok = Assert.IsType<Ok<MarketplaceSource>>(result);
        Assert.Contains("network down", ok.Value!.LastError);
        // Stale offerings are kept: an unreachable source can still be installed from.
        Assert.Equal("1.0.0", Assert.Single(ok.Value.Offerings).Version);
    }

    [Fact]
    public async Task Refreshing_an_unknown_source_is_a_404()
    {
        var result = await MarketplaceSourceEndpoints.RefreshAsync(
            "nope", _root, _prober, _staging);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Refreshing_all_refreshes_every_source_and_one_failure_does_not_stop_the_rest()
    {
        _fetcher.AddPlugin("https://github.com/acme/alpha.git", "alpha", "1.0.0");
        _fetcher.AddPlugin("https://github.com/acme/beta.git", "beta", "1.0.0");
        await AddAsync("https://github.com/acme/alpha.git");
        await AddAsync("https://github.com/acme/beta.git");

        _fetcher.Fail("https://github.com/acme/alpha.git", "gone");
        _fetcher.AddPlugin("https://github.com/acme/beta.git", "beta", "2.0.0");

        var result = await MarketplaceSourceEndpoints.RefreshAllAsync(_root, _prober, _staging);

        var ok = Assert.IsType<Ok<List<MarketplaceSource>>>(result);
        Assert.Equal(2, ok.Value!.Count);
        Assert.Contains("gone", ok.Value.First(s => s.Name == "acme-alpha").LastError);
        Assert.Equal("2.0.0", Assert.Single(ok.Value.First(s => s.Name == "acme-beta").Offerings).Version);
    }

    [Fact]
    public async Task Refreshing_all_with_no_sources_is_an_empty_list()
    {
        var result = await MarketplaceSourceEndpoints.RefreshAllAsync(_root, _prober, _staging);

        Assert.Empty(Assert.IsType<Ok<List<MarketplaceSource>>>(result).Value!);
    }

    // ── Removing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_source_persists()
    {
        _fetcher.AddPlugin(Url, "acme-things");
        await AddAsync();

        var result = MarketplaceSourceEndpoints.Remove(MarketplaceSourceStore.DeriveName(Url), _root);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(Stored());
    }

    [Fact]
    public void Removing_an_unknown_source_is_a_404()
    {
        var result = MarketplaceSourceEndpoints.Remove("nope", _root);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    /// <summary>
    /// Removing a source removes only the listing. Forgetting where you found something is not
    /// the same as uninstalling it, and a plugin installed from a source owns its own files.
    /// </summary>
    [Fact]
    public async Task Removing_a_source_leaves_plugins_installed_from_it_alone()
    {
        _fetcher.AddPlugin(Url, "acme-things");
        await AddAsync();

        new PluginStateStore(_root).Upsert(new InstalledPlugin
        {
            Name = "acme-things",
            Source = Url,
            ResolvedVersion = "abc123",
            InstalledAtUtc = DateTimeOffset.UnixEpoch,
            Files = ["skills/x/SKILL.md"],
        });

        MarketplaceSourceEndpoints.Remove(MarketplaceSourceStore.DeriveName(Url), _root);

        var still = new PluginStateStore(_root).Find("acme-things");
        Assert.NotNull(still);
        Assert.Equal(["skills/x/SKILL.md"], still.Files);
    }

    // ── The two stores share a directory ─────────────────────────────────────

    /// <summary>
    /// Sources and installed plugins live in the same directory in separate files. A write to one
    /// must not disturb the other - they are written independently and a shared-directory mistake
    /// would only show up as data loss on whichever was written second.
    /// </summary>
    [Fact]
    public async Task Source_writes_do_not_disturb_installed_plugins()
    {
        new PluginStateStore(_root).Upsert(new InstalledPlugin
        {
            Name = "existing",
            Source = "https://example.com/x.git",
            ResolvedVersion = "abc123",
            InstalledAtUtc = DateTimeOffset.UnixEpoch,
            Files = ["a.md"],
        });

        _fetcher.AddPlugin(Url, "acme-things");
        await AddAsync();

        Assert.NotNull(new PluginStateStore(_root).Find("existing"));
        Assert.Single(Stored());
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    /// <summary>A fetcher that writes a single-plugin repository, or fails, per source URL.</summary>
    private sealed class ScriptedFetcher : IPluginSourceFetcher
    {
        private readonly Dictionary<string, string> _manifests = [];
        private readonly Dictionary<string, string> _failures = [];
        private readonly Dictionary<string, string?> _references = [];

        public void AddPlugin(string url, string name, string version = "1.0.0")
        {
            _manifests[url] = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["version"] = version,
            });
            _failures.Remove(url);
        }

        public void Fail(string url, string message)
        {
            _failures[url] = message;
            _manifests.Remove(url);
        }

        public string? ReferenceFor(string url) => _references.GetValueOrDefault(url);

        public Task<PluginFetchResult> FetchAsync(
            string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            _references[source] = reference;

            if (_failures.TryGetValue(source, out var message))
                throw new InvalidOperationException(message);

            if (!_manifests.TryGetValue(source, out var manifest))
                throw new InvalidOperationException($"no script for {source}");

            var dir = Path.Combine(stagingDirectory, ".botnexus-plugin");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest);

            return Task.FromResult(new PluginFetchResult("deadbeef"));
        }
    }
}
