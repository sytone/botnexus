using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Citizens;

/// <summary>
/// Registry for <see cref="User"/> records — the static directory of who the human citizens
/// of this BotNexus world are. Mirrors <see cref="BotNexus.Gateway.Abstractions.Agents.IAgentRegistry"/>
/// on the User side. This is the "phone book" of users; it does not own runtime presence,
/// authentication, or session state.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe. The gateway reads from this registry when
/// resolving inbound sender identities (Phase 2c) and when populating
/// <see cref="BotNexus.Gateway.Abstractions.Citizens.ICitizenRegistry"/>.
/// </remarks>
public interface IUserRegistry
{
    /// <summary>
    /// Registers a new user. Throws if a user with the same <see cref="User.Id"/> is already registered.
    /// </summary>
    /// <param name="user">The user record to register.</param>
    /// <exception cref="InvalidOperationException">A user with this id is already registered.</exception>
    void Register(User user);

    /// <summary>
    /// Removes a user registration. No-op if the user is not registered.
    /// </summary>
    /// <param name="userId">The user id to unregister.</param>
    void Unregister(UserId userId);

    /// <summary>
    /// Updates an existing user record while preserving registration identity.
    /// </summary>
    /// <param name="userId">The registered user id to update.</param>
    /// <param name="user">The updated user payload.</param>
    /// <returns><c>true</c> when updated; otherwise <c>false</c> if the user does not exist.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="user"/> carries a different user id.</exception>
    bool Update(UserId userId, User user)
        => throw new NotSupportedException("This registry does not support user updates.");

    /// <summary>Gets a user by id, or <c>null</c> if not registered.</summary>
    User? Get(UserId userId);

    /// <summary>Gets all registered users.</summary>
    IReadOnlyList<User> GetAll();

    /// <summary>Checks whether a user with the given id is registered.</summary>
    bool Contains(UserId userId);
}
