using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>
/// Registers the plugins read/preference API (<c>/api/plugins</c>) backing the portal plugins
/// page (#2687, slice 8 of #2623).
/// </summary>
/// <remarks>
/// <para>
/// Install, update and remove are exposed alongside the read and preference routes. These are
/// endpoint-routed rather than middleware, so they execute after <c>GatewayAuthMiddleware</c> and
/// are subject to the gateway's authentication like every other API route - unlike a contributor
/// that registers <c>app.Use</c>, which maps ahead of it.
/// </para>
/// <para>
/// Registered as an <see cref="IEndpointContributor"/> in an extension rather than as a
/// controller in <c>BotNexus.Gateway.Api</c>, because a gateway project may not reference an
/// extension project (<c>GatewayProjectDependencyBoundaryTests</c>). This follows the
/// <c>SkillsEndpointContributor</c> precedent, which moved the skills file browser out of the
/// gateway for the same reason.
/// </para>
/// </remarks>
public sealed class PluginsEndpointContributor : IEndpointContributor
{
    /// <inheritdoc />
    public void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/plugins");

        group.MapGet("/", () => List());
        group.MapGet("/{name}", (string name) => Get(name));
        group.MapPut("/{name}/update-preference",
            (string name, PluginUpdatePreferenceRequest request) => SetUpdatePreference(name, request));
        group.MapPut("/{name}/nav-visibility",
            (string name, PluginNavVisibilityRequest request) => SetNavVisibility(name, request));
        group.MapPost("/install", (PluginInstallApiRequest request) => InstallAsync(request));
        group.MapPost("/{name}/update", (string name) => UpdateAsync(name));
        group.MapDelete("/{name}", (string name) => Remove(name));

        // Marketplace sources: the repositories the portal looks in to FIND plugins. Mapped
        // before /{name} would otherwise claim them - "sources" is a literal segment and minimal
        // APIs prefer it over the parameter, but keeping the group explicit avoids relying on
        // that precedence being obvious to the next reader.
        var sources = group.MapGroup("/sources");

        sources.MapGet("/", () => MarketplaceSourceEndpoints.List());
        sources.MapPost("/", (MarketplaceSourceRequest request, CancellationToken ct) =>
            MarketplaceSourceEndpoints.AddAsync(request, ct));
        sources.MapPost("/refresh", (CancellationToken ct) =>
            MarketplaceSourceEndpoints.RefreshAllAsync(ct));
        sources.MapPost("/{name}/refresh", (string name, CancellationToken ct) =>
            MarketplaceSourceEndpoints.RefreshAsync(name, ct));
        sources.MapDelete("/{name}", (string name) => MarketplaceSourceEndpoints.Remove(name));
    }

    /// <summary>
    /// Absolute path of the deployed-extensions root: <c>~/.botnexus/extensions</c>, honouring the
    /// same <c>BOTNEXUS_HOME</c> override as <see cref="GetPluginRootPath"/>.
    /// </summary>
    internal static string GetExtensionsRootPath()
    {
        var homeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            return Path.Combine(Path.GetFullPath(homeOverride), "extensions");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".botnexus", "extensions");
    }

    /// <summary>
    /// Composes a lifecycle manager over the real git transport. Built per request rather than
    /// injected, matching how every other route here constructs its store: these operations are
    /// rare, and a cached manager would pin the plugin root read at startup.
    /// </summary>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="extensionsRoot">Directory holding deployed extensions.</param>
    internal static PluginLifecycleManager CreateManager(string pluginRoot, string extensionsRoot) =>
        new(new PluginStateStore(pluginRoot),
            new GitPluginSourceFetcher(new ProcessGitCommandRunner()),
            extensionsRoot: extensionsRoot);

    /// <summary>Shows or hides a plugin's contributed nav entries.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="request">New visibility.</param>
    internal static IResult SetNavVisibility(string name, PluginNavVisibilityRequest request) =>
        SetNavVisibility(name, request, GetPluginRootPath());

    /// <summary>
    /// Sets nav visibility under an explicit plugin root. Written back to the installed record
    /// rather than held in memory, for the same reason the auto-update preference is: a toggle
    /// that did not survive a restart would assert something the gateway never stored.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="request">New visibility.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static IResult SetNavVisibility(
        string name,
        PluginNavVisibilityRequest request,
        string pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body is required." });
        }

        var store = new PluginStateStore(pluginRoot);
        var existing = store.Find(name);
        if (existing is null)
        {
            return Results.NotFound(new { error = $"Plugin '{name}' is not installed." });
        }

        // `with` preserves the recorded file sets: they are the only description of what the
        // plugin owns, and a preference write that dropped them would orphan every file it wrote.
        store.Upsert(existing with { NavHidden = request.NavHidden });

        return Results.Ok(new PluginPortalProjector(store).Find(name));
    }

    /// <summary>Installs a plugin from a marketplace source.</summary>
    /// <param name="request">What to install.</param>
    internal static Task<IResult> InstallAsync(PluginInstallApiRequest request) =>
        InstallAsync(request, GetPluginRootPath(), GetExtensionsRootPath());

    /// <summary>
    /// Installs a plugin under explicit roots.
    /// </summary>
    /// <remarks>
    /// A failure is a 400 carrying the operation's own field-named errors rather than a bare
    /// message. The lifecycle manager already names the offending manifest field, and flattening
    /// that to a string would throw away the only thing that tells an author what to fix.
    /// </remarks>
    /// <param name="request">What to install.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="extensionsRoot">Directory holding deployed extensions.</param>
    internal static async Task<IResult> InstallAsync(
        PluginInstallApiRequest request,
        string pluginRoot,
        string extensionsRoot)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Source))
        {
            return Results.BadRequest(new { error = "A plugin source is required." });
        }

        return await InstallAsync(request, CreateManager(pluginRoot, extensionsRoot), pluginRoot);
    }

    /// <summary>
    /// Installs through a supplied lifecycle manager, so the route's validation and response
    /// shaping can be pinned without a git binary or a network.
    /// </summary>
    /// <param name="request">What to install.</param>
    /// <param name="manager">Lifecycle manager to install through.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static async Task<IResult> InstallAsync(
        PluginInstallApiRequest request,
        PluginLifecycleManager manager,
        string pluginRoot)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Source))
        {
            return Results.BadRequest(new { error = "A plugin source is required." });
        }

        var result = await manager.InstallAsync(new PluginInstallRequest
        {
            Source = request.Source,
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name,
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference,
            UpdatesEnabled = request.UpdatesEnabled ?? true,
            AllowCarriedExtension = request.AcknowledgeCarriedExtension,
        });

        return Respond(result, pluginRoot);
    }

    /// <summary>Re-resolves a plugin's source and replaces its content if the source moved.</summary>
    /// <param name="name">Plugin identifier.</param>
    internal static Task<IResult> UpdateAsync(string name) =>
        UpdateAsync(name, GetPluginRootPath(), GetExtensionsRootPath());

    /// <summary>Updates a plugin under explicit roots.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="extensionsRoot">Directory holding deployed extensions.</param>
    internal static async Task<IResult> UpdateAsync(string name, string pluginRoot, string extensionsRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        return await UpdateAsync(name, CreateManager(pluginRoot, extensionsRoot), pluginRoot);
    }

    /// <summary>Updates through a supplied lifecycle manager.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="manager">Lifecycle manager to update through.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static async Task<IResult> UpdateAsync(
        string name,
        PluginLifecycleManager manager,
        string pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        return Respond(await manager.UpdateAsync(name), pluginRoot);
    }

    /// <summary>Removes an installed plugin and any extension it deployed.</summary>
    /// <param name="name">Plugin identifier.</param>
    internal static IResult Remove(string name) =>
        Remove(name, GetPluginRootPath(), GetExtensionsRootPath());

    /// <summary>Removes a plugin under explicit roots.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    /// <param name="extensionsRoot">Directory holding deployed extensions.</param>
    internal static IResult Remove(string name, string pluginRoot, string extensionsRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        return Remove(name, CreateManager(pluginRoot, extensionsRoot));
    }

    /// <summary>Removes through a supplied lifecycle manager.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="manager">Lifecycle manager to remove through.</param>
    internal static IResult Remove(string name, PluginLifecycleManager manager)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        var result = manager.Remove(name);
        if (result.Outcome == PluginOperationOutcome.Failed)
        {
            // "Not installed" is the only way remove fails, and a 404 says that more precisely
            // than a 400 would.
            return Results.NotFound(new { error = result.Errors[0].Message });
        }

        return Results.Ok(new PluginOperationResponse(
            result.Outcome.ToString(),
            name,
            result.PreviousVersion,
            null,
            RestartRequired: false,
            Plugin: null));
    }

    /// <summary>
    /// Projects a lifecycle outcome onto an HTTP result, carrying the portal's own row shape on
    /// success so a caller can render the result without a second round trip.
    /// </summary>
    private static IResult Respond(PluginOperationResult result, string pluginRoot)
    {
        if (result.Outcome == PluginOperationOutcome.Failed)
        {
            return Results.BadRequest(new
            {
                error = result.Errors.Count > 0 ? result.Errors[0].Message : "The operation failed.",
                errors = result.Errors.Select(e => new { field = e.Field, message = e.Message }),
            });
        }

        var store = new PluginStateStore(pluginRoot);
        var record = store.Find(result.Name);

        // Activation is a startup concern: extension endpoints are mapped once, post-build, so a
        // carried extension is on disk but inert until the gateway restarts. Saying so in the
        // response is the difference between "installed" and "installed and working".
        var restartRequired = record?.DeployedExtensionId is not null;

        return Results.Ok(new PluginOperationResponse(
            result.Outcome.ToString(),
            result.Name,
            result.PreviousVersion,
            record?.ResolvedVersion,
            restartRequired,
            new PluginPortalProjector(store).Find(result.Name)));
    }

    /// <summary>
    /// Absolute path of the plugin root: <c>~/.botnexus/plugins</c>, honouring the
    /// <c>BOTNEXUS_HOME</c> override so a container deployment or a test is not pinned to the real
    /// user profile. Mirrors <c>SkillsEndpointContributor.GetSkillsRootPath</c>.
    /// </summary>
    internal static string GetPluginRootPath()
    {
        var homeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            return Path.Combine(Path.GetFullPath(homeOverride), "plugins");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".botnexus", "plugins");
    }

    /// <summary>Lists every installed plugin, ordered by name.</summary>
    internal static IResult List() => List(GetPluginRootPath());

    /// <summary>Lists every installed plugin under an explicit plugin root.</summary>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static IResult List(string pluginRoot) =>
        Results.Ok(new PluginPortalProjector(new PluginStateStore(pluginRoot)).List());

    /// <summary>Returns one installed plugin by name.</summary>
    /// <param name="name">Plugin identifier.</param>
    internal static IResult Get(string name) => Get(name, GetPluginRootPath());

    /// <summary>
    /// Returns one installed plugin under an explicit plugin root. An unknown name is a 404 rather
    /// than an empty 200: the portal distinguishes "not installed" from "installed with nothing to
    /// show", and collapsing the two would make a typo indistinguishable from an empty plugin.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static IResult Get(string name, string pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        var row = new PluginPortalProjector(new PluginStateStore(pluginRoot)).Find(name);
        return row is null
            ? Results.NotFound(new { error = $"Plugin '{name}' is not installed." })
            : Results.Ok(row);
    }

    /// <summary>Sets whether scheduled updates may replace a plugin's content.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="request">New preference.</param>
    internal static IResult SetUpdatePreference(string name, PluginUpdatePreferenceRequest request) =>
        SetUpdatePreference(name, request, GetPluginRootPath());

    /// <summary>
    /// Sets the auto-update preference under an explicit plugin root. The change is written back to
    /// the installed record, not held in memory, so it survives a restart - a preference that did
    /// not persist would leave the portal's toggle asserting something the gateway never stored.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="request">New preference.</param>
    /// <param name="pluginRoot">Directory holding installed plugins.</param>
    internal static IResult SetUpdatePreference(
        string name,
        PluginUpdatePreferenceRequest request,
        string pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { error = "A plugin name is required." });
        }

        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body is required." });
        }

        var store = new PluginStateStore(pluginRoot);
        var existing = store.Find(name);
        if (existing is null)
        {
            return Results.NotFound(new { error = $"Plugin '{name}' is not installed." });
        }

        // `with` preserves the recorded file set and every other field: the file list is the only
        // description of what the plugin owns, and a preference write that dropped it would orphan
        // every file the install wrote.
        store.Upsert(existing with { UpdatesEnabled = request.UpdatesEnabled });

        return Results.Ok(new PluginPortalProjector(store).Find(name));
    }
}

/// <summary>Request body for toggling a plugin's auto-update preference.</summary>
/// <param name="UpdatesEnabled">Whether scheduled updates may replace this plugin's content.</param>
public sealed record PluginUpdatePreferenceRequest(bool UpdatesEnabled);

/// <summary>Request body for showing or hiding a plugin's contributed nav entries.</summary>
/// <param name="NavHidden">Whether to hide this plugin's nav entries from the sidebar.</param>
public sealed record PluginNavVisibilityRequest(bool NavHidden);

/// <summary>Request body for installing a plugin from a marketplace source.</summary>
/// <param name="Source">Repository URL to install from.</param>
/// <param name="Name">Expected plugin name, or <c>null</c> to accept whatever the manifest declares.</param>
/// <param name="Reference">Branch, tag or commit to install, or <c>null</c> for the default branch.</param>
/// <param name="UpdatesEnabled">Whether scheduled updates may replace the content; defaults to true.</param>
/// <param name="AcknowledgeCarriedExtension">
/// Whether the caller accepts a plugin that carries a gateway extension - code loaded in-process at
/// full trust. Defaults to false, so install refuses an unacknowledged code plugin and the caller
/// re-issues knowing what it is agreeing to.
/// </param>
public sealed record PluginInstallApiRequest(
    string Source,
    string? Name = null,
    string? Reference = null,
    bool? UpdatesEnabled = null,
    bool AcknowledgeCarriedExtension = false);

/// <summary>Result of an install, update or remove.</summary>
/// <param name="Outcome">Lifecycle outcome name.</param>
/// <param name="Name">Plugin identifier.</param>
/// <param name="PreviousVersion">Revision that was installed before, when there was one.</param>
/// <param name="ResolvedVersion">Revision now on disk.</param>
/// <param name="RestartRequired">
/// Whether a gateway restart is needed before the result takes effect. True when a carried
/// extension was deployed: extension endpoints are mapped once at startup, so it is on disk but
/// inert until then.
/// </param>
/// <param name="Plugin">The installed plugin's portal row, when it is still installed.</param>
public sealed record PluginOperationResponse(
    string Outcome,
    string Name,
    string? PreviousVersion,
    string? ResolvedVersion,
    bool RestartRequired,
    PluginPortalRow? Plugin);
