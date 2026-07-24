namespace BotNexus.Gateway.Nav;

/// <summary>
/// Server-side persistence for per-user portal nav-order overrides (#2236, slice 5 of #2231).
/// Only overrides are stored; built-in defaults live in <see cref="NavOrderDefaults"/>. Persisting
/// overrides server-side means a user's ordering roams with them across browsers and devices, and
/// survives a gateway restart - mirroring the Tools store precedent (#2232).
/// </summary>
public interface INavOrderStore
{
    /// <summary>Ensures the backing schema exists. Safe to call repeatedly.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the effective order for every built-in nav item: the stored user override where one
    /// exists, otherwise the built-in default from <see cref="NavOrderDefaults"/>. Always returns
    /// one entry per built-in key, sorted ascending by effective order.
    /// </summary>
    Task<IReadOnlyList<NavItemOrder>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists an order override for a single nav key. Unknown keys are accepted so future built-ins
    /// or user-defined items can be ordered without a schema change.
    /// </summary>
    Task SetOrderAsync(string key, int order, CancellationToken ct = default);

    /// <summary>Removes a user override, reverting the key to its built-in default. Idempotent.</summary>
    Task ResetAsync(string key, CancellationToken ct = default);
}
