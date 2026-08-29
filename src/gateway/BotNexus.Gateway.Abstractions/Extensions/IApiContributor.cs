using Microsoft.AspNetCore.Routing;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Contributes to the gateway's shared API surface.
/// Receives a scoped RouteGroupBuilder pre-namespaced to prevent route collisions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Status: zero implementations (decision recorded for #3539).</strong> Nothing in this
/// repository implements this interface - not a built-in, not an extension, not a test double. It
/// is wired at one site, <c>AssemblyLoadContextExtensionLoader.MapExtensionEndpoints</c>, which
/// resolves <c>IApiContributor</c> from DI and maps each into a group at
/// <c>/api/extensions/{assemblyName}</c>. Because the resolution always yields an empty sequence,
/// that loop body has never executed in production or under test.
/// </para>
/// <para>
/// Two consequences follow, and they are the reason this note exists rather than a bare "unused"
/// comment. First, the route-scoping behaviour is unverified: an unimplemented contract cannot be
/// known to work, so the first extension to implement it is also the first to test it. Second, the
/// extension-id derivation at that seam is still an open TODO and falls back to the assembly
/// name, which is not a stable extension identifier - a route namespace that would need to change
/// the moment a real implementation depended on it.
/// </para>
/// <para>
/// Retained rather than removed: the seam is cheap, and <c>IEndpointContributor</c> - which is
/// implemented by six extensions and one built-in - occupies the adjacent role, so a caller
/// reaching for this one has a working alternative today. Anyone adding the first implementation
/// should resolve the extension-id TODO in the same change, and should expect to be writing the
/// contract's first coverage.
/// </para>
/// </remarks>
public interface IApiContributor
{
    /// <summary>
    /// Registers API endpoints within the provided scoped route group
    /// (e.g., /api/extensions/{extensionId}/).
    /// </summary>
    void MapApiRoutes(RouteGroupBuilder apiGroup);
}
