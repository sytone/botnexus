using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Citizens;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Citizens;

/// <summary>
/// Default in-memory <see cref="IUserRegistry"/>. Thread-safe via a single internal lock.
/// Mirrors <see cref="Agents.DefaultAgentRegistry"/> deliberately so the User and Agent
/// sides have the same operational shape.
/// </summary>
public sealed class DefaultUserRegistry : IUserRegistry
{
    private readonly Dictionary<UserId, User> _users = new();
    private readonly Lock _sync = new();
    private readonly ILogger<DefaultUserRegistry> _logger;

    public DefaultUserRegistry(ILogger<DefaultUserRegistry> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Register(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_sync)
        {
            if (!_users.TryAdd(user.Id, user))
                throw new InvalidOperationException($"User '{user.Id}' is already registered.");

            _logger.LogInformation("Registered user '{UserId}' ({DisplayName})", user.Id, user.DisplayName);
        }
    }

    /// <inheritdoc />
    public void Unregister(UserId userId)
    {
        lock (_sync)
        {
            if (_users.Remove(userId))
                _logger.LogInformation("Unregistered user '{UserId}'", userId);
        }
    }

    /// <inheritdoc />
    public bool Update(UserId userId, User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Id != userId)
            throw new ArgumentException("The user payload Id must match the registered userId.", nameof(user));

        lock (_sync)
        {
            if (!_users.ContainsKey(userId))
                return false;

            _users[userId] = user;
            _logger.LogInformation("Updated user '{UserId}' ({DisplayName})", user.Id, user.DisplayName);
            return true;
        }
    }

    /// <inheritdoc />
    public User? Get(UserId userId)
    {
        lock (_sync) return _users.GetValueOrDefault(userId);
    }

    /// <inheritdoc />
    public IReadOnlyList<User> GetAll()
    {
        lock (_sync) return [.. _users.Values];
    }

    /// <inheritdoc />
    public bool Contains(UserId userId)
    {
        lock (_sync) return _users.ContainsKey(userId);
    }
}
