using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Citizens;

/// <summary>
/// Umbrella registry that resolves any <see cref="ICitizen"/> by its <see cref="CitizenId"/>,
/// regardless of species. Implementations compose the per-species registries
/// (<see cref="IUserRegistry"/> and <see cref="BotNexus.Gateway.Abstractions.Agents.IAgentRegistry"/>)
/// behind a single typed lens.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>composite</b> by design — do not make <c>IAgentRegistry</c> or
/// <c>IUserRegistry</c> inherit it. Two reasons:
/// </para>
/// <list type="number">
///   <item>It would be source-breaking for any custom registry implementations.</item>
///   <item>
///     If both per-species registries were registered in DI as
///     <see cref="ICitizenRegistry"/>, resolving the umbrella interface would return
///     only the last registration and silently drop one species.
///   </item>
/// </list>
/// <para>Implementations must be thread-safe.</para>
/// </remarks>
public interface ICitizenRegistry
{
    /// <summary>
    /// Resolves a citizen by id, or returns <c>null</c> if no matching citizen is registered.
    /// </summary>
    /// <param name="citizenId">The citizen identifier (User or Agent).</param>
    ICitizen? Resolve(CitizenId citizenId);

    /// <summary>
    /// Returns all registered citizens (users and agents combined). The order is
    /// implementation-defined; callers must not rely on it.
    /// </summary>
    IReadOnlyList<ICitizen> GetAll();

    /// <summary>Checks whether a citizen with the given id is registered.</summary>
    bool Contains(CitizenId citizenId);
}
