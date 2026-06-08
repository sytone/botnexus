namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Key-value state persistence for extensions. Provides a unified SQLite-backed
/// alternative to per-extension JSON file state management.
/// </summary>
public interface IExtensionStateStore
{
    /// <summary>
    /// Initializes the store, creating the backing database and schema if needed.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a value by extension ID and key. Returns <c>null</c> if the key does not exist.
    /// </summary>
    Task<string?> GetAsync(string extensionId, string key, CancellationToken ct = default);

    /// <summary>
    /// Sets (upserts) a value for the given extension ID and key.
    /// </summary>
    Task SetAsync(string extensionId, string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Deletes a single key for the given extension. No-op if the key does not exist.
    /// </summary>
    Task DeleteAsync(string extensionId, string key, CancellationToken ct = default);

    /// <summary>
    /// Lists all keys stored for the given extension.
    /// </summary>
    Task<IReadOnlyList<string>> ListKeysAsync(string extensionId, CancellationToken ct = default);

    /// <summary>
    /// Clears all keys for the given extension.
    /// </summary>
    Task ClearAsync(string extensionId, CancellationToken ct = default);
}
