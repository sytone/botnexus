using BotNexus.Extensions.Plugins.Lifecycle;
using Microsoft.AspNetCore.Http;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>
/// The marketplace source routes under <c>/api/plugins/sources</c>: the repositories the portal
/// looks in to find plugins to offer.
/// </summary>
/// <remarks>
/// <para>
/// Adding a source is deliberately separate from installing what it offers. A source is a place to
/// look; adding one records a URL and reads it, and runs nothing. Installing is the step that can
/// put third-party code in the gateway, and it stays behind its own call and its own consent gate.
/// </para>
/// <para>
/// Written as statics with an explicit-root overload for every route, matching
/// <see cref="PluginsEndpointContributor"/>, so the routes are testable without a host.
/// </para>
/// </remarks>
public static class MarketplaceSourceEndpoints
{
    /// <summary>Lists every configured source with whatever the last read found.</summary>
    internal static IResult List() => List(PluginsEndpointContributor.GetPluginRootPath());

    /// <summary>Lists sources under an explicit plugin root, ordered by name.</summary>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static IResult List(string pluginRoot) =>
        Results.Ok(new MarketplaceSourceStore(pluginRoot)
            .Read()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>Adds a source and reads it once so it lists immediately.</summary>
    /// <param name="request">The repository to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static Task<IResult> AddAsync(
        MarketplaceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        AddAsync(
            request,
            PluginsEndpointContributor.GetPluginRootPath(),
            CreateProber(),
            GetStagingRootPath(),
            cancellationToken);

    /// <summary>
    /// Adds a source under explicit roots, probing it through the supplied prober.
    /// </summary>
    /// <remarks>
    /// The new source is probed before the response returns, so the caller sees what it offers
    /// without a second call - the point of adding one is to find out what is in it.
    /// <para>
    /// A probe that fails still stores the source, carrying its error. A first read can fail for
    /// reasons that are nothing to do with the URL - the network, a rate limit, a repository that
    /// is briefly unavailable - and discarding the entry would make the operator retype it. A bad
    /// URL is visible as an error on the row and is one delete away; a good source lost to a flaky
    /// network is not recoverable by the operator at all.
    /// </para>
    /// </remarks>
    /// <param name="request">The repository to add.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="prober">Prober to read the source with.</param>
    /// <param name="stagingRoot">Directory to stage fetches under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<IResult> AddAsync(
        MarketplaceSourceRequest request,
        string pluginRoot,
        MarketplaceSourceProber prober,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Url))
        {
            return Results.BadRequest(new { error = "A repository URL is required." });
        }

        var url = request.Url.Trim();

        if (!IsSupportedUrl(url))
        {
            return Results.BadRequest(new
            {
                error = "The URL must be an absolute http:// or https:// repository address.",
            });
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? MarketplaceSourceStore.DeriveName(url)
            : request.Name.Trim();

        var store = new MarketplaceSourceStore(pluginRoot);

        // Adding the same source twice is a conflict rather than a silent overwrite: the stored
        // record carries the last read, and quietly replacing it would discard offerings the
        // portal may be listing. Refresh is the route that deliberately re-reads.
        if (store.Find(name) is not null)
        {
            return Results.Conflict(new { error = $"A source named '{name}' is already configured." });
        }

        var source = new MarketplaceSource
        {
            Name = name,
            Url = url,
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            AddedAtUtc = DateTimeOffset.UtcNow,
        };

        var probed = await prober.ProbeAsync(source, stagingRoot, cancellationToken);
        store.Upsert(probed);

        return Results.Created($"/api/plugins/sources/{Uri.EscapeDataString(name)}", probed);
    }

    /// <summary>Re-reads one source and stores what it now offers.</summary>
    /// <param name="name">Source identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static Task<IResult> RefreshAsync(string name, CancellationToken cancellationToken = default) =>
        RefreshAsync(
            name,
            PluginsEndpointContributor.GetPluginRootPath(),
            CreateProber(),
            GetStagingRootPath(),
            cancellationToken);

    /// <summary>
    /// Re-reads one source under explicit roots.
    /// </summary>
    /// <remarks>
    /// A refresh that cannot read the source is still a 200 carrying the source with its error,
    /// not a 5xx. The request succeeded - the gateway asked and recorded the answer - and the
    /// operator needs the row back with the reason on it. An error status would leave the portal
    /// unable to distinguish "this source is unreachable" from "the refresh call itself broke".
    /// </remarks>
    /// <param name="name">Source identifier.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="prober">Prober to read the source with.</param>
    /// <param name="stagingRoot">Directory to stage fetches under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<IResult> RefreshAsync(
        string name,
        string pluginRoot,
        MarketplaceSourceProber prober,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A source name is required." });
        }

        var store = new MarketplaceSourceStore(pluginRoot);
        var existing = store.Find(name);

        if (existing is null)
        {
            return Results.NotFound(new { error = $"Source '{name}' is not configured." });
        }

        var probed = await prober.ProbeAsync(existing, stagingRoot, cancellationToken);
        store.Upsert(probed);

        return Results.Ok(probed);
    }

    /// <summary>Re-reads every configured source.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static Task<IResult> RefreshAllAsync(CancellationToken cancellationToken = default) =>
        RefreshAllAsync(
            PluginsEndpointContributor.GetPluginRootPath(),
            CreateProber(),
            GetStagingRootPath(),
            cancellationToken);

    /// <summary>
    /// Re-reads every source under explicit roots. One unreadable source does not stop the others
    /// being refreshed; each carries its own error, which is the whole reason the error lives on
    /// the source rather than on the response.
    /// </summary>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="prober">Prober to read the sources with.</param>
    /// <param name="stagingRoot">Directory to stage fetches under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<IResult> RefreshAllAsync(
        string pluginRoot,
        MarketplaceSourceProber prober,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        var store = new MarketplaceSourceStore(pluginRoot);
        var refreshed = new List<MarketplaceSource>();

        foreach (var source in store.Read())
        {
            var probed = await prober.ProbeAsync(source, stagingRoot, cancellationToken);
            store.Upsert(probed);
            refreshed.Add(probed);
        }

        return Results.Ok(refreshed
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>Removes a source.</summary>
    /// <param name="name">Source identifier.</param>
    internal static IResult Remove(string name) =>
        Remove(name, PluginsEndpointContributor.GetPluginRootPath());

    /// <summary>
    /// Removes a source under an explicit plugin root.
    /// </summary>
    /// <remarks>
    /// Removing a source removes only the listing. Plugins installed from it stay installed and
    /// keep working: an installed plugin owns its own files and its own source URL, and nothing
    /// about it is read back through the source it was found in. Forgetting where you found
    /// something is not the same as uninstalling it.
    /// </remarks>
    /// <param name="name">Source identifier.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static IResult Remove(string name, string pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A source name is required." });
        }

        return new MarketplaceSourceStore(pluginRoot).Delete(name)
            ? Results.Ok(new { removed = name })
            : Results.NotFound(new { error = $"Source '{name}' is not configured." });
    }

    /// <summary>
    /// Whether a URL may be added as a source.
    /// </summary>
    /// <remarks>
    /// Restricted to absolute http/https. The value is handed to <c>git clone</c>, so accepting
    /// any string would let a request name a local path or a <c>file://</c> URL and have the
    /// gateway read a directory the caller cannot otherwise reach. Local sources remain available
    /// to a direct install, which is an operator action against a path they already chose.
    /// </remarks>
    /// <param name="url">Candidate repository URL.</param>
    internal static bool IsSupportedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Composes a prober over the real git transport.</summary>
    internal static MarketplaceSourceProber CreateProber()
    {
        // One runner for both jobs: fetching each entry, and asking its repository what refs it
        // actually has so a catalog's pinned version can be checked against reality.
        var git = new ProcessGitCommandRunner();
        return new MarketplaceSourceProber(
            new GitPluginSourceFetcher(git), new PluginManifestParser(), gitCommandRunner: git);
    }

    /// <summary>
    /// Directory to stage fetches under: <c>.staging</c> inside the plugin root.
    /// </summary>
    /// <remarks>
    /// Derived from the plugin root rather than resolved here. A file that builds its own
    /// <c>~/.botnexus</c> path has never been checked against this world's sentinel (#2836), so
    /// home resolution stays in one place and this consumes it. Staging sits beside the plugin
    /// data rather than in the system temp directory, so probing a large repository is bounded by
    /// the same volume the install would land on; the prober deletes each staging directory itself.
    /// </remarks>
    internal static string GetStagingRootPath() =>
        Path.Combine(PluginsEndpointContributor.GetPluginRootPath(), ".staging");
}

/// <summary>Request body for adding a marketplace source.</summary>
/// <param name="Url">Repository URL to read plugins from.</param>
/// <param name="Name">
/// Identifier for the source, or <c>null</c> to derive one from the URL. Derived names carry owner
/// and repository, so two publishers' similarly named repositories do not collide.
/// </param>
/// <param name="Reference">Branch, tag or commit to read, or <c>null</c> for the default branch.</param>
public sealed record MarketplaceSourceRequest(
    string Url,
    string? Name = null,
    string? Reference = null);
