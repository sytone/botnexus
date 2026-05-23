using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Citizens;

namespace BotNexus.Gateway.Citizens;

/// <summary>
/// Composite <see cref="ICitizenRegistry"/> that dispatches to per-species registries.
/// Reads only — both <see cref="IUserRegistry"/> and <see cref="IAgentRegistry"/> remain
/// the source of truth for their own kind of citizen.
/// </summary>
/// <remarks>
/// This is the singleton registered for <see cref="ICitizenRegistry"/> in DI. The two
/// per-species registries continue to be registered under their own interfaces — see
/// <see cref="ICitizenRegistry"/> for why we never make the typed registries implement
/// this interface directly.
/// </remarks>
public sealed class DefaultCitizenRegistry : ICitizenRegistry
{
    private readonly IUserRegistry _users;
    private readonly IAgentRegistry _agents;

    public DefaultCitizenRegistry(IUserRegistry users, IAgentRegistry agents)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <inheritdoc />
    public ICitizen? Resolve(CitizenId citizenId)
    {
        if (!citizenId.IsValid)
            return null;

        return citizenId.Kind switch
        {
            CitizenKind.User => _users.Get(citizenId.AsUser!.Value),
            CitizenKind.Agent => _agents.Get(citizenId.AsAgent!.Value),
            _ => null,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<ICitizen> GetAll()
    {
        var users = _users.GetAll();
        var agents = _agents.GetAll();

        var combined = new List<ICitizen>(users.Count + agents.Count);
        combined.AddRange(users);
        combined.AddRange(agents);
        return combined;
    }

    /// <inheritdoc />
    public bool Contains(CitizenId citizenId)
    {
        if (!citizenId.IsValid)
            return false;

        return citizenId.Kind switch
        {
            CitizenKind.User => _users.Contains(citizenId.AsUser!.Value),
            CitizenKind.Agent => _agents.Contains(citizenId.AsAgent!.Value),
            _ => false,
        };
    }
}
