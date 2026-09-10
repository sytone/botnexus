using Microsoft.AspNetCore.Builder;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Extension-owned endpoints, static files, middleware, and transport surfaces.
/// Called during app startup after WebApplication is built.
/// </summary>
public interface IEndpointContributor
{
    /// <summary>
    /// Registers extension-owned endpoints (hubs, webhooks), static files, and middleware.
    /// </summary>
    void MapEndpoints(WebApplication app);

    /// <summary>
    /// Relative position in the middleware pipeline; lower registers earlier. Defaults to 0.
    /// </summary>
    /// <remarks>
    /// Contributors were previously invoked in <c>GetServices</c> order, which is registration
    /// order, which is extension LOAD order - derived from a topological sort whose tie-break is
    /// filesystem directory order. That is not deterministic, so a contributor registering a
    /// catch-all could swallow another contributor's route depending on how the directories
    /// happened to enumerate. Anything that must run last declares it here instead of relying on
    /// that accident.
    /// </remarks>
    int Order => 0;
}
