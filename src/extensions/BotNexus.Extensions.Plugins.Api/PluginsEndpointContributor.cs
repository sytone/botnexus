using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>
/// Registers the plugin read and lifecycle API at <c>/api/plugins</c>.
/// </summary>
/// <remarks>
/// This surface remains in the extension so gateway projects do not acquire a dependency on an
/// extension implementation assembly. All mapped writes receive the production lifecycle manager
/// from dependency injection, keeping HTTP and scheduled updates on the same state graph.
/// </remarks>
public sealed class PluginsEndpointContributor : IEndpointContributor
{
    /// <inheritdoc />
    public void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/plugins");
        MapMarketplaceEndpoints(group);

        group.MapGet("/", (PluginStateStore store) => List(store.PluginRoot));
        group.MapGet("/{name}", (string name, PluginStateStore store) => Get(name, store.PluginRoot));
        group.MapPost("/", Install);
        group.MapPost("/{name}/update", Update);
        group.MapDelete("/{name}", Remove);
        group.MapPost("/{name}/pin", Pin);
        group.MapPost("/{name}/unpin", Unpin);
        group.MapPut("/{name}/update-preference",
            (string name, PluginUpdatePreferenceRequest request, PluginLifecycleManager manager) =>
                SetUpdatePreference(name, request, manager));
    }

    /// <summary>
    /// Resolves the compatibility plugin root for direct callers that predate production DI.
    /// </summary>
    internal static string GetPluginRootPath()
    {
        var homeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            return Path.Combine(Path.GetFullPath(homeOverride), PluginSkillRootResolver.PluginRootDirectoryName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".botnexus", PluginSkillRootResolver.PluginRootDirectoryName);
    }

    /// <summary>Lists every installed plugin, ordered by name.</summary>
    internal static IResult List() => List(GetPluginRootPath());

    /// <summary>Lists every installed plugin under an explicit plugin root.</summary>
    internal static IResult List(string pluginRoot) =>
        Results.Ok(new PluginPortalProjector(new PluginStateStore(pluginRoot)).List());

    /// <summary>Returns one installed plugin by name.</summary>
    internal static IResult Get(string name) => Get(name, GetPluginRootPath());

    /// <summary>Returns one installed plugin under an explicit plugin root.</summary>
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

    /// <summary>Installs plugin content from the requested source.</summary>
    internal static async Task<IResult> Install(
        PluginInstallRequest request,
        PluginLifecycleManager manager,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ToWriteResult(
                PluginLifecycleOperation.Install,
                PluginOperationResult.Failure(string.Empty, "request", "A request body is required."),
                missingIsNotFound: false);
        }

        var result = await manager.InstallAsync(request, cancellationToken).ConfigureAwait(false);
        return ToWriteResult(
            PluginLifecycleOperation.Install,
            result,
            missingIsNotFound: false,
            request.Reference);
    }

    /// <summary>Updates an installed plugin from its recorded source.</summary>
    internal static async Task<IResult> Update(
        string name,
        PluginLifecycleManager manager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return MissingName(PluginLifecycleOperation.Update);
        }

        var result = await manager.UpdateAsync(name, cancellationToken).ConfigureAwait(false);
        return ToWriteResult(PluginLifecycleOperation.Update, result, missingIsNotFound: true);
    }

    /// <summary>Removes an installed plugin's recorded files.</summary>
    internal static IResult Remove(string name, PluginLifecycleManager manager)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return MissingName(PluginLifecycleOperation.Remove);
        }

        return ToWriteResult(
            PluginLifecycleOperation.Remove,
            manager.Remove(name),
            missingIsNotFound: true);
    }

    /// <summary>Prevents source updates for an installed plugin.</summary>
    internal static IResult Pin(string name, PluginLifecycleManager manager) =>
        SetUpdatePreference(name, updatesEnabled: false, manager);

    /// <summary>Allows source updates for an installed plugin.</summary>
    internal static IResult Unpin(string name, PluginLifecycleManager manager) =>
        SetUpdatePreference(name, updatesEnabled: true, manager);

    /// <summary>Sets whether scheduled updates may replace a plugin's content.</summary>
    internal static IResult SetUpdatePreference(
        string name,
        PluginUpdatePreferenceRequest request,
        PluginLifecycleManager manager)
    {
        if (request is null)
        {
            return ToWriteResult(
                PluginLifecycleOperation.SetUpdatePreference,
                PluginOperationResult.Failure(name, "request", "A request body is required."),
                missingIsNotFound: false);
        }

        return SetUpdatePreference(name, request.UpdatesEnabled, manager);
    }

    /// <summary>Compatibility entry point for direct callers using the default plugin root.</summary>
    internal static IResult SetUpdatePreference(string name, PluginUpdatePreferenceRequest request) =>
        SetUpdatePreference(name, request, GetPluginRootPath());

    /// <summary>Compatibility entry point for direct callers using an explicit plugin root.</summary>
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

        store.Upsert(existing with { UpdatesEnabled = request.UpdatesEnabled });
        return Results.Ok(new PluginPortalProjector(store).Find(name));
    }

    private static IResult SetUpdatePreference(
        string name,
        bool updatesEnabled,
        PluginLifecycleManager manager)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return MissingName(PluginLifecycleOperation.SetUpdatePreference);
        }

        return ToWriteResult(
            PluginLifecycleOperation.SetUpdatePreference,
            manager.SetUpdatePreference(name, updatesEnabled),
            missingIsNotFound: true);
    }

    private static IResult ToWriteResult(
        PluginLifecycleOperation operation,
        PluginOperationResult result,
        bool missingIsNotFound,
        string? requestedReference = null)
    {
        var receipt = PluginOperationReceipt.FromResult(operation, result, requestedReference);
        if (result.IsSuccess)
        {
            return Results.Ok(receipt);
        }

        return missingIsNotFound && result.Errors.Any(error =>
                string.Equals(error.Field, "name", StringComparison.Ordinal) &&
                error.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            ? Results.NotFound(receipt)
            : Results.BadRequest(receipt);
    }

    private static void MapMarketplaceEndpoints(RouteGroupBuilder plugins)
    {
        var group = plugins.MapGroup("/marketplaces");
        // World configuration is not agent-scoped. Match the operator-only global route
        // policy, regardless of an allowed agent selector or the caller's admin flag.
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Items.TryGetValue(GatewayAuthHttpContext.CallerIdentityItemKey, out var value)
                && value is GatewayCallerIdentity { AllowedAgents.Count: 0 } identity
                && !identity.CallerId.StartsWith("satellite:", StringComparison.OrdinalIgnoreCase))
                return await next(context);

            return Results.Json(new MarketplaceError("Marketplace registry access requires an unrestricted operator identity."),
                statusCode: StatusCodes.Status403Forbidden);
        });
        group.MapGet("/", ListMarketplaces);
        group.MapGet("/{id}", GetMarketplace);
        group.MapPost("/", AddMarketplace);
        group.MapPut("/{id}", UpdateMarketplace);
        group.MapDelete("/{id}", RemoveMarketplace);
        group.MapPost("/{id}/enable", (string id, MarketplaceRegistryService registry, CancellationToken ct) => SetMarketplaceEnabled(id, true, registry, ct));
        group.MapPost("/{id}/disable", (string id, MarketplaceRegistryService registry, CancellationToken ct) => SetMarketplaceEnabled(id, false, registry, ct));
        // Reserve static paths for every overlapping installed-plugin verb. Otherwise DELETE
        // /marketplaces would unexpectedly uninstall an installed plugin named "marketplaces".
        group.MapDelete("/", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        group.MapPost("/update", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        group.MapPost("/pin", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        group.MapPost("/unpin", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        group.MapPut("/update-preference", (MarketplaceRegistrationRequest request, MarketplaceRegistryService registry, CancellationToken ct) =>
            UpdateMarketplace("update-preference", request, registry, ct));
    }

    internal static Task<IResult> ListMarketplaces(MarketplaceRegistryService registry, CancellationToken ct) =>
        MarketplaceResult(async () => Results.Ok(await registry.ListAsync(ct)));

    internal static Task<IResult> GetMarketplace(string id, MarketplaceRegistryService registry, CancellationToken ct) =>
        MarketplaceResult(async () => Results.Ok(await registry.GetAsync(ParseMarketplaceId(id), ct)));

    internal static Task<IResult> AddMarketplace(MarketplaceAddRequest request, MarketplaceRegistryService registry, CancellationToken ct) =>
        MarketplaceResult(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var id = ParseMarketplaceId(request.Id);
            await registry.AddAsync(id, request.Source, request.Reference, ct);
            return Results.Created($"/api/plugins/marketplaces/{id.Value}", await registry.GetAsync(id, ct));
        });

    internal static Task<IResult> UpdateMarketplace(string id, MarketplaceRegistrationRequest request, MarketplaceRegistryService registry, CancellationToken ct) =>
        MarketplaceResult(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var identity = ParseMarketplaceId(id);
            await registry.UpdateAsync(identity, request.Source, request.Reference, ct);
            return Results.Ok(await registry.GetAsync(identity, ct));
        });

    internal static Task<IResult> SetMarketplaceEnabled(string id, bool enabled, MarketplaceRegistryService registry, CancellationToken ct) =>
        MarketplaceResult(async () =>
        {
            var identity = ParseMarketplaceId(id);
            await registry.SetEnabledAsync(identity, enabled, ct);
            return Results.Ok(await registry.GetAsync(identity, ct));
        });

    internal static Task<IResult> RemoveMarketplace(string id, MarketplaceRegistryService registry, CancellationToken ct) =>
        MarketplaceResult(async () =>
        {
            await registry.RemoveAsync(ParseMarketplaceId(id), ct);
            return Results.NoContent();
        });

    private static MarketplaceId ParseMarketplaceId(string id) => MarketplaceId.TryFrom(id, out var identity)
        ? identity : throw new ArgumentException("Marketplace id must use lowercase alphanumeric segments separated by single hyphens (maximum 128 characters).", nameof(id));

    private static async Task<IResult> MarketplaceResult(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (InvalidDataException ex) { return Results.Conflict(new MarketplaceError(ex.Message)); }
        catch (ArgumentException ex) { return Results.BadRequest(new MarketplaceError(ex.Message)); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new MarketplaceError(ex.Message)); }
        catch (InvalidOperationException ex) { return Results.Conflict(new MarketplaceError(ex.Message)); }
    }

    private static IResult MissingName(PluginLifecycleOperation operation) =>
        ToWriteResult(
            operation,
            PluginOperationResult.Failure(string.Empty, "name", "A plugin name is required."),
            missingIsNotFound: false);
}

/// <summary>Request body for toggling a plugin's auto-update preference.</summary>
/// <param name="UpdatesEnabled">Whether scheduled updates may replace this plugin's content.</param>
public sealed record PluginUpdatePreferenceRequest(bool UpdatesEnabled);
