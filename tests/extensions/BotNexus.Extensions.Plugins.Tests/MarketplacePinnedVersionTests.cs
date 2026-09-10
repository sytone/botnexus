using System.Text.Json;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the check that a catalog's <c>version</c> still names something real in the plugin's
/// repository.
/// </summary>
/// <remarks>
/// A catalog version is the git ref an install resolves. A pin left behind after a release
/// installs the PREVIOUS plugin while the listing looks perfectly healthy - which happened on this
/// fork within two hours of a tag being cut, and is why this check exists.
/// </remarks>
public sealed class MarketplacePinnedVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bn-pin-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedFetcher _fetcher = new();
    private readonly ScriptedGit _git = new();

    public MarketplacePinnedVersionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private MarketplaceSourceProber Prober() =>
        new(_fetcher, new PluginManifestParser(), gitCommandRunner: _git);

    private static MarketplaceSource Source() => new()
    {
        Name = "acme",
        Url = "https://github.com/acme/catalog.git",
        AddedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private const string PluginUrl = "https://github.com/acme/alpha.git";

    private void SetupCatalog(string? pinnedVersion)
    {
        var entry = new Dictionary<string, object?>
        {
            ["name"] = "alpha",
            ["source"] = PluginUrl,
        };

        // Omitted, never null: the schema types version as a string, so a JSON null fails
        // validation and would take the whole catalog down rather than leaving one entry unpinned.
        if (pinnedVersion is not null)
            entry["version"] = pinnedVersion;

        var catalog = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = "acme-catalog",
            ["owner"] = new Dictionary<string, object?> { ["name"] = "Acme" },
            ["plugins"] = new[] { entry },
        });

        _fetcher.Add(Source().Url, d => File.WriteAllText(Path.Combine(d, "marketplace.json"), catalog));
        _fetcher.Add(PluginUrl, d =>
        {
            var dir = Path.Combine(d, ".botnexus-plugin");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["name"] = "alpha",
                    ["version"] = "1.2.1",
                }));
        });
    }

    private async Task<MarketplaceOffering> ProbeAsync()
    {
        var result = await Prober().ProbeAsync(Source(), _root);
        return Assert.Single(result.Offerings);
    }

    // ── The case this exists for ─────────────────────────────────────────────

    [Fact]
    public async Task A_pin_left_behind_a_newer_tag_is_reported()
    {
        SetupCatalog("v1.2.0");
        _git.Tags(PluginUrl, "v1.0.0", "v1.1.0", "v1.2.0", "v1.2.1");

        var offering = await ProbeAsync();

        Assert.Contains("v1.2.0", offering.VersionWarning);
        Assert.Contains("v1.2.1", offering.VersionWarning);
        // Still listed and installable - a stale pin is a warning, not a failure.
        Assert.Null(offering.Error);
        Assert.Equal("alpha", offering.Name);
    }

    [Fact]
    public async Task A_pin_on_the_newest_tag_is_silent()
    {
        SetupCatalog("v1.2.1");
        _git.Tags(PluginUrl, "v1.0.0", "v1.1.0", "v1.2.0", "v1.2.1");

        Assert.Null((await ProbeAsync()).VersionWarning);
    }

    /// <summary>
    /// The footgun the schema allows: catalog <c>version</c> is a git ref while the plugin
    /// manifest's version is bare semver, so writing the manifest's "1.2.1" pins a ref that does
    /// not exist. Without this it surfaces only as a clone failure at install time.
    /// </summary>
    [Fact]
    public async Task A_pin_that_names_no_ref_at_all_is_reported()
    {
        SetupCatalog("1.2.1");
        _git.Tags(PluginUrl, "v1.2.0", "v1.2.1");

        var warning = (await ProbeAsync()).VersionWarning;

        Assert.Contains("1.2.1", warning);
        Assert.Contains("not a tag or branch", warning);
    }

    /// <summary>
    /// The pin that names no ref makes the FETCH fail, so the warning has to survive the failure
    /// path. A check that only ran on success would miss the exact case it was built for, leaving
    /// only git's "Remote branch 1.2.1 not found" - true, but silent about which field to fix.
    /// </summary>
    [Fact]
    public async Task A_pin_that_breaks_the_fetch_still_explains_the_pin()
    {
        SetupCatalog("1.2.1");
        _git.Tags(PluginUrl, "v1.2.0", "v1.2.1");
        _fetcher.Fail(PluginUrl, "fatal: Remote branch 1.2.1 not found in upstream origin");

        var offering = await ProbeAsync();

        // Both: what git said, AND which field is wrong.
        Assert.Contains("Remote branch", offering.Error);
        Assert.Contains("not a tag or branch", offering.VersionWarning);
        // The entry is still listed rather than vanishing.
        Assert.Equal("alpha", offering.Name);
    }

    [Fact]
    public async Task A_stale_pin_survives_an_unreadable_entry()
    {
        SetupCatalog("v1.1.0");
        _git.Tags(PluginUrl, "v1.1.0", "v1.2.1");
        _fetcher.Fail(PluginUrl, "network down");

        var offering = await ProbeAsync();

        Assert.Contains("network down", offering.Error);
        Assert.Contains("v1.2.1", offering.VersionWarning);
    }

    // ── Deliberate pins that must NOT be nagged about ────────────────────────

    [Fact]
    public async Task Tracking_a_branch_is_never_reported_as_stale()
    {
        SetupCatalog("main");
        _git.Refs(PluginUrl, tags: ["v1.2.0", "v9.9.9"], heads: ["main", "develop"]);

        Assert.Null((await ProbeAsync()).VersionWarning);
    }

    [Fact]
    public async Task A_commit_sha_is_not_ranked_against_tags()
    {
        SetupCatalog("a4ac0609a4f01d8397dc08c5993e1ddf8e3a3b74");
        _git.Tags(PluginUrl, "v1.2.0", "v9.9.9");

        Assert.Null((await ProbeAsync()).VersionWarning);
    }

    [Fact]
    public async Task An_unrankable_tag_is_not_compared()
    {
        // A pre-release pin cannot be ordered, so no release is claimed to supersede it.
        SetupCatalog("v2.0.0-beta");
        _git.Tags(PluginUrl, "v2.0.0-beta", "v1.0.0");

        Assert.Null((await ProbeAsync()).VersionWarning);
    }

    /// <summary>
    /// Guards the outcome, not the mechanism: this passes whether the "^{}" suffix is stripped or
    /// merely discarded as unrankable, so it does not pin the stripping itself.
    /// </summary>
    [Fact]
    public async Task An_annotated_tag_listed_twice_is_not_mistaken_for_a_newer_release()
    {
        SetupCatalog("v1.2.1");
        // ls-remote prints refs/tags/v1.2.1 AND refs/tags/v1.2.1^{} for an annotated tag.
        _git.RawRefs(PluginUrl,
            "aaa\trefs/tags/v1.2.1",
            "bbb\trefs/tags/v1.2.1^{}");

        Assert.Null((await ProbeAsync()).VersionWarning);
    }

    // ── Degradation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_entry_with_no_version_is_not_checked()
    {
        SetupCatalog(pinnedVersion: null);
        _git.Tags(PluginUrl, "v9.9.9");

        Assert.Null((await ProbeAsync()).VersionWarning);
    }

    [Fact]
    public async Task A_failing_ls_remote_is_not_a_finding_about_the_pin()
    {
        SetupCatalog("v1.2.0");
        _git.Fail(PluginUrl);

        var offering = await ProbeAsync();

        Assert.Null(offering.VersionWarning);
        Assert.Null(offering.Error);
    }

    [Fact]
    public async Task Without_a_git_runner_the_probe_is_unchanged()
    {
        SetupCatalog("v1.2.0");
        _git.Tags(PluginUrl, "v1.2.1");

        var result = await new MarketplaceSourceProber(_fetcher, new PluginManifestParser())
            .ProbeAsync(Source(), _root);

        var offering = Assert.Single(result.Offerings);
        Assert.Null(offering.VersionWarning);
        Assert.Equal("alpha", offering.Name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class ScriptedGit : IGitCommandRunner
    {
        private readonly Dictionary<string, string> _output = [];
        private readonly HashSet<string> _failing = [];

        public void Tags(string url, params string[] tags) =>
            _output[url] = string.Join("\n", tags.Select((t, i) => $"{i:x40}\trefs/tags/{t}"));

        public void Refs(string url, string[] tags, string[] heads) =>
            _output[url] = string.Join("\n",
                tags.Select((t, i) => $"{i:x40}\trefs/tags/{t}")
                    .Concat(heads.Select((h, i) => $"{i:x40}\trefs/heads/{h}")));

        public void RawRefs(string url, params string[] lines) => _output[url] = string.Join("\n", lines);

        public void Fail(string url) => _failing.Add(url);

        public Task<GitCommandResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            var url = arguments.LastOrDefault() ?? string.Empty;

            if (_failing.Contains(url))
                return Task.FromResult(new GitCommandResult(1, string.Empty, "fatal: repository not found"));

            return Task.FromResult(
                new GitCommandResult(0, _output.GetValueOrDefault(url, string.Empty), string.Empty));
        }
    }

    private sealed class ScriptedFetcher : IPluginSourceFetcher
    {
        private readonly Dictionary<string, Action<string>> _writers = [];

        private readonly Dictionary<string, string> _failures = [];

        public void Add(string url, Action<string> write) => _writers[url] = write;

        public void Fail(string url, string message) => _failures[url] = message;

        public Task<PluginFetchResult> FetchAsync(
            string source, string? reference, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            if (_failures.TryGetValue(source, out var message))
                throw new InvalidOperationException(message);

            if (!_writers.TryGetValue(source, out var write))
                throw new InvalidOperationException($"no script for {source}");

            write(stagingDirectory);
            return Task.FromResult(new PluginFetchResult("deadbeef"));
        }
    }
}
