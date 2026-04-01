using System.Collections.Concurrent;
using BotNexus.Core.Abstractions;

namespace BotNexus.Core.Extensions;

/// <summary>
/// Thread-safe dictionary-backed registry for system actions.
/// </summary>
public sealed class SystemActionRegistry : ISystemActionRegistry
{
    private readonly ConcurrentDictionary<string, ISystemAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public SystemActionRegistry()
    {
    }

    public SystemActionRegistry(IEnumerable<ISystemAction> actions)
    {
        foreach (var action in actions)
            Register(action);
    }

    /// <inheritdoc />
    public void Register(ISystemAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions[action.Name] = action;
    }

    /// <inheritdoc />
    public ISystemAction? Get(string name)
        => _actions.TryGetValue(name, out var action) ? action : null;

    /// <inheritdoc />
    public IReadOnlyList<ISystemAction> GetAll()
        => [.. _actions.Values.OrderBy(static action => action.Name, StringComparer.OrdinalIgnoreCase)];
}
