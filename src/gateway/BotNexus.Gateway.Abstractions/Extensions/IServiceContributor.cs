using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Extension hook for registering services into the host DI container during startup,
/// before the application is built. Implement this when an extension needs to perform
/// arbitrary service registration that the loader's contract-based auto-discovery cannot
/// express — for example configuring authorization policies, options, or replacing a
/// framework-provided default (such as <c>IUserIdProvider</c>).
/// </summary>
/// <remarks>
/// Implementations must expose a public parameterless constructor. The loader instantiates
/// the contributor directly (it is not resolved from the container) and invokes
/// <see cref="ConfigureServices"/> while the service collection is still mutable. This runs
/// in addition to — not instead of — the loader's contract-based registration, so contributors
/// should only register what auto-discovery misses.
/// </remarks>
public interface IServiceContributor
{
    /// <summary>
    /// Registers extension-owned services into the host service collection. Called once during
    /// extension load, before <c>WebApplication</c> is built, so policy/options/default-replacement
    /// registrations take effect for the running host.
    /// </summary>
    void ConfigureServices(IServiceCollection services);
}
