using BotNexus.Persistence.Sqlite.Telemetry;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Default <see cref="IExtensionUsageTelemetry"/> implementation (#1852). A thin decorator over the
/// shared <see cref="IUsageTelemetry"/> store that binds every call to a fixed consumer namespace
/// (the extension id), so an extension shares the single durable usage-telemetry database with the
/// platform and every other extension while remaining isolated to its own namespace.
/// </summary>
public sealed class ExtensionUsageTelemetry : IExtensionUsageTelemetry
{
    private readonly string _extensionId;
    private readonly IUsageTelemetry _store;

    /// <summary>
    /// Creates a namespaced usage-telemetry handle for <paramref name="extensionId"/> over the
    /// shared <paramref name="store"/>.
    /// </summary>
    /// <param name="extensionId">The owning extension id (validated); used as the consumer namespace.</param>
    /// <param name="store">The shared durable usage-telemetry store.</param>
    public ExtensionUsageTelemetry(string extensionId, IUsageTelemetry store)
    {
        _extensionId = ExtensionMeters.ValidateExtensionId(extensionId);
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public string ExtensionId => _extensionId;

    /// <inheritdoc />
    public Task IncrementAsync(string key, string counterName, CancellationToken cancellationToken = default)
        => _store.IncrementAsync(_extensionId, key, counterName, cancellationToken);

    /// <inheritdoc />
    public Task RecordCreatedAsync(string key, string createdBy, CancellationToken cancellationToken = default)
        => _store.RecordCreatedAsync(_extensionId, key, createdBy, cancellationToken);

    /// <inheritdoc />
    public Task SetPinnedAsync(string key, bool pinned, CancellationToken cancellationToken = default)
        => _store.SetPinnedAsync(_extensionId, key, pinned, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<UsageRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        => _store.GetAllAsync(_extensionId, cancellationToken);

    /// <inheritdoc />
    public Task<UsageRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
        => _store.GetAsync(_extensionId, key, cancellationToken);
}
