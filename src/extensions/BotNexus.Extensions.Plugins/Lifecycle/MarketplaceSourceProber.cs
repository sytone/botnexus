using System.IO.Abstractions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Reads a marketplace source and works out what it offers.
/// </summary>
/// <remarks>
/// <para>
/// A source is probed, never trusted. It is fetched, inspected, and the result recorded on the
/// source itself, so the portal can list what is on offer without touching the network on every
/// page load and without anything being installed to find out.
/// </para>
/// <para>
/// Two shapes are accepted, decided by what the repository actually contains rather than by what
/// the operator declared when adding it: a <b>catalog</b> carrying <c>marketplace.json</c> at its
/// root, listing plugins that live elsewhere, or a <b>plugin</b> - a repository that is itself one,
/// carrying <c>.botnexus-plugin/plugin.json</c>. Checking the catalog first matters: a repository
/// may legitimately be both, and the catalog is the broader claim.
/// </para>
/// </remarks>
public sealed class MarketplaceSourceProber(
    IPluginSourceFetcher fetcher,
    PluginManifestParser parser,
    TimeProvider? timeProvider = null,
    IFileSystem? fileSystem = null,
    IGitCommandRunner? gitCommandRunner = null)
{
    /// <summary>Path of a catalog document within a source repository.</summary>
    public const string CatalogFileName = "marketplace.json";

    /// <summary>Path of a plugin manifest within a plugin repository.</summary>
    public const string PluginManifestPath = ".botnexus-plugin/plugin.json";

    private readonly IPluginSourceFetcher _fetcher = fetcher;
    private readonly PluginManifestParser _parser = parser;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IFileSystem _fs = fileSystem ?? new FileSystem();

    /// <summary>
    /// Lists a source's refs so a catalog's pinned version can be checked against reality.
    /// Optional: when absent the pin is simply not checked, and every other probe result is
    /// unchanged.
    /// </summary>
    private readonly IGitCommandRunner? _git = gitCommandRunner;

    /// <summary>
    /// Fetches the source and returns it updated with what it offers.
    /// </summary>
    /// <remarks>
    /// Never throws for a source that cannot be read: an unreachable repository, a malformed
    /// catalog and a repository that is neither shape all come back as a record carrying
    /// <see cref="MarketplaceSource.LastError"/>. One bad source must not stop the others being
    /// listed, and the operator needs to see WHY rather than have it vanish.
    /// <para>
    /// A failed probe deliberately keeps the previous offerings. Stale is more useful than empty:
    /// a source that refreshed yesterday and is unreachable today can still be installed from.
    /// </para>
    /// </remarks>
    /// <param name="source">The source to read.</param>
    /// <param name="stagingRoot">Directory to stage fetches under; the caller owns it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MarketplaceSource> ProbeAsync(
        MarketplaceSource source,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        var staging = _fs.Path.Combine(stagingRoot, $"probe-{Guid.NewGuid():N}");

        try
        {
            _fs.Directory.CreateDirectory(staging);
            await _fetcher.FetchAsync(source.Url, source.Reference, staging, cancellationToken);

            var catalogPath = _fs.Path.Combine(staging, CatalogFileName);

            return _fs.File.Exists(catalogPath)
                ? await ProbeCatalogAsync(source, catalogPath, stagingRoot, cancellationToken)
                : ProbeSinglePlugin(source, staging);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(source, ex.Message);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task<MarketplaceSource> ProbeCatalogAsync(
        MarketplaceSource source,
        string catalogPath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var parsed = _parser.ParseMarketplace(_fs.File.ReadAllText(catalogPath));

        if (parsed.Value is not { } catalog)
            return Failed(source, Describe($"{CatalogFileName} is not valid", parsed.Errors));

        var offerings = new List<MarketplaceOffering>();

        foreach (var entry in catalog.Plugins)
        {
            // Each entry's own repository is fetched so its manifest is read from the plugin
            // rather than from the catalog describing it. The catalog states a name, version and
            // description; only the manifest says whether installing runs code in the gateway,
            // and that is the field a person most needs before choosing. The catalog's
            // description is kept as a fallback for a plugin that ships none.
            offerings.Add(await ProbeEntryAsync(entry, stagingRoot, cancellationToken));
        }

        return source with
        {
            Kind = "catalog",
            Offerings = offerings,
            LastRefreshedAtUtc = _time.GetUtcNow(),
            LastError = null,
        };
    }

    private async Task<MarketplaceOffering> ProbeEntryAsync(
        MarketplacePluginEntry entry,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        // The catalog's own claims are the fallback, used when the plugin cannot be read. Better a
        // listing that says what the catalog claims and admits it could not verify, than an entry
        // that silently disappears from the marketplace.
        var fallback = new MarketplaceOffering
        {
            Name = entry.Name,
            Url = entry.Source,
            Version = entry.Version,
            Description = entry.Description,
        };

        if (string.IsNullOrWhiteSpace(entry.Source))
            return fallback with { Error = "the catalog entry has no source" };

        var staging = _fs.Path.Combine(stagingRoot, $"entry-{Guid.NewGuid():N}");

        // Checked BEFORE the fetch and carried onto every outcome. A pin naming no ref makes the
        // fetch itself fail, so a check that only ran on success would miss the very case it was
        // built for - and leave the operator with git's "Remote branch 1.2.1 not found", which says
        // what happened but not that the catalog's version field is what to fix.
        string? pinWarning = null;

        try
        {
            _fs.Directory.CreateDirectory(staging);
            pinWarning = await CheckPinnedVersionAsync(entry, staging, cancellationToken);
            await _fetcher.FetchAsync(entry.Source, entry.Version, staging, cancellationToken);

            var manifestPath = _fs.Path.Combine(staging, PluginManifestPath);

            if (!_fs.File.Exists(manifestPath))
                return fallback with { Error = "no .botnexus-plugin/plugin.json in the entry's repository", VersionWarning = pinWarning };

            var manifest = _parser.ParseManifest(_fs.File.ReadAllText(manifestPath));

            if (manifest.Value is not { } value)
                return fallback with { Error = Describe("the plugin manifest is not valid", manifest.Errors), VersionWarning = pinWarning };

            var offering = ToOffering(value, entry.Source, entry.Version);

            // The catalog's description fills a blank; it never overrides one. A plugin that
            // describes itself always wins, so a catalog cannot restate what a plugin says about
            // itself - it can only speak where the plugin said nothing.
            //
            // Deliberately the description alone. Version and CarriesExtension stay manifest-only
            // because they change what installing DOES, and a listing must not be able to
            // influence that; a description only changes how the entry reads.
            var described = string.IsNullOrWhiteSpace(offering.Description)
                ? offering with { Description = entry.Description }
                : offering;

            return described with { VersionWarning = pinWarning };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback with { Error = ex.Message, VersionWarning = pinWarning };
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private MarketplaceSource ProbeSinglePlugin(MarketplaceSource source, string staging)
    {
        var manifestPath = _fs.Path.Combine(staging, PluginManifestPath);

        if (!_fs.File.Exists(manifestPath))
        {
            return Failed(
                source,
                $"neither {CatalogFileName} nor {PluginManifestPath} was found - "
                + "this repository is not a plugin and does not list any");
        }

        var manifest = _parser.ParseManifest(_fs.File.ReadAllText(manifestPath));

        if (manifest.Value is not { } value)
            return Failed(source, Describe("the plugin manifest is not valid", manifest.Errors));

        return source with
        {
            Kind = "plugin",
            Offerings = [ToOffering(value, source.Url, source.Reference)],
            LastRefreshedAtUtc = _time.GetUtcNow(),
            LastError = null,
        };
    }

    private static MarketplaceOffering ToOffering(PluginManifest manifest, string url, string? reference) => new()
    {
        Name = manifest.Name,
        Url = url,
        Version = manifest.Version,
        Description = manifest.Description,
        Reference = reference,
        CarriesExtension = manifest.Extension is not null,
    };

    /// <summary>
    /// Records the failure while keeping whatever the source last offered.
    /// </summary>
    private MarketplaceSource Failed(MarketplaceSource source, string error) => source with
    {
        LastError = error,
        // LastRefreshedAtUtc is deliberately NOT advanced: it means "when this content was read",
        // and a failed probe read nothing. Advancing it would make stale offerings look fresh.
    };

    private static string Describe(string summary, IReadOnlyList<PluginValidationError>? errors)
    {
        if (errors is null || errors.Count == 0)
            return summary;

        return $"{summary}: {string.Join("; ", errors.Select(e => e.Message))}";
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (_fs.Directory.Exists(directory))
                _fs.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked staging directory is not worth failing a probe over.
        }
    }

    /// <summary>
    /// Compares a catalog entry's pinned <c>version</c> against the refs its source actually has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A catalog's <c>version</c> is the git ref an install resolves, and nothing else reconciles
    /// it with the plugin repository. A pin left behind after a release installs the PREVIOUS
    /// plugin while the listing looks healthy - observed on this fork within two hours of a tag
    /// being cut.
    /// </para>
    /// <para>
    /// Only a tag pin is judged. A branch name is a deliberate choice to track a moving ref, and a
    /// commit SHA cannot be ranked against tags, so neither is warned about - claiming staleness
    /// there would train people to ignore the warning.
    /// </para>
    /// </remarks>
    private async Task<string?> CheckPinnedVersionAsync(
        MarketplacePluginEntry entry,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (_git is null || string.IsNullOrWhiteSpace(entry.Version) || string.IsNullOrWhiteSpace(entry.Source))
            return null;

        GitCommandResult refs;
        try
        {
            refs = await _git.RunAsync(
                workingDirectory, ["ls-remote", "--", entry.Source], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not being able to ask is not a finding about the pin.
            return null;
        }

        if (refs.ExitCode != 0)
            return null;

        var tags = new List<string>();
        var heads = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0)
                continue;

            var name = line[(tab + 1)..].Trim();

            // "refs/tags/x^{}" is the dereferenced commit of an annotated tag - the same tag listed
            // twice. Stripping the suffix is belt-and-braces rather than load-bearing: the
            // rankability filter below already discards "v1.2.1^{}" because it is not
            // dotted-numeric. Kept so the tag list means what it says, and so this stays correct if
            // that filter is ever loosened.
            if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
                tags.Add(name["refs/tags/".Length..].Replace("^{}", string.Empty));
            else if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
                heads.Add(name["refs/heads/".Length..]);
        }

        var pinned = entry.Version.Trim();

        if (heads.Contains(pinned))
            return null;

        if (!tags.Contains(pinned, StringComparer.Ordinal))
        {
            // A SHA is legitimate and unrankable; anything else names nothing that exists. The
            // common case is writing the manifest's bare "1.2.1" where the tag is "v1.2.1", which
            // otherwise surfaces only as a clone failure at install time.
            return LooksLikeCommitSha(pinned)
                ? null
                : $"the catalog pins '{pinned}', which is not a tag or branch in {entry.Source}";
        }

        var newest = tags
            .Where(tag => !string.Equals(tag, pinned, StringComparison.Ordinal))
            .Where(tag => VersionOrder.IsRankable(tag) && VersionOrder.IsRankable(pinned))
            .Where(tag => VersionOrder.Compare(pinned, tag) < 0)
            .OrderByDescending(tag => tag, VersionOrder.Comparer)
            .FirstOrDefault();

        return newest is null
            ? null
            : $"the catalog pins '{pinned}' but '{newest}' has been released";
    }

    private static bool LooksLikeCommitSha(string value) =>
        value.Length >= 7 && value.All(char.IsAsciiHexDigit);
}
