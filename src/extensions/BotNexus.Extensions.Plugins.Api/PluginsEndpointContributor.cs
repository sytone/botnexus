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
/// Read and preference-toggle only. Installing a plugin from the portal is out of scope for this
/// slice - install remains a CLI operation, so there is deliberately no <c>POST</c> here.
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
