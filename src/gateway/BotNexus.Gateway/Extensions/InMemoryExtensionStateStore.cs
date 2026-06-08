using System.Collections.Concurrent;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// In-memory implementation of <see cref="Abstractions.Extensions.IExtensionStateStore"/> for testing.
/// </summary>
public sealed class InMemoryExtensionStateStore : Abstractions.Extensions.IExtensionStateStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _store = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string?> GetAsync(string extensionId, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_store.TryGetValue(extensionId, out var keys) && keys.TryGetValue(key, out var value))
            return Task.FromResult<string?>(value);

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string extensionId, string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var keys = _store.GetOrAdd(extensionId, _ => new ConcurrentDictionary<string, string>());
        keys[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string extensionId, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_store.TryGetValue(extensionId, out var keys))
            keys.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string extensionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);

        if (_store.TryGetValue(extensionId, out var keys))
            return Task.FromResult<IReadOnlyList<string>>(keys.Keys.OrderBy(k => k).ToList());

        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task ClearAsync(string extensionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);

        _store.TryRemove(extensionId, out _);
        return Task.CompletedTask;
    }
}
