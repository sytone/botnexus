using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Where a configuration document was actually read from.
///
/// <para>
/// Reported rather than inferred. During a cutover the most valuable diagnostic is which source served
/// the configuration currently in memory, and an operator must never have to deduce that from flag
/// state - flags describe intent, this describes what happened.
/// </para>
/// </summary>
public enum ConfigDocumentOrigin
{
    /// <summary>Read from <c>config.json</c>.</summary>
    File = 0,

    /// <summary>Read from the SQLite configuration store.</summary>
    Store = 1,
}

/// <summary>
/// The outcome of a configuration read (#2646 PBI 3).
/// </summary>
/// <param name="Document">
/// The document, or <see langword="null"/> when no configuration exists at all. Distinct from an empty
/// object, which is a real document that sets nothing.
/// </param>
/// <param name="Origin">Which source produced <paramref name="Document"/>.</param>
/// <param name="FellBack">
/// <see langword="true"/> when the store was meant to serve this read and could not.
///
/// <para>
/// Carried as a separate flag rather than being inferred from <paramref name="Origin"/> because
/// "file, because that is the configured behaviour" and "file, because the store failed" are the same
/// origin and completely different operational facts. Collapsing them would make a degraded platform
/// indistinguishable from a healthy one.
/// </para>
/// </param>
public readonly record struct ConfigDocumentRead(
    JsonObject? Document,
    ConfigDocumentOrigin Origin,
    bool FellBack);

/// <summary>
/// Supplies the raw platform configuration document, from whichever source is currently authoritative.
/// </summary>
public interface IConfigDocumentSource
{
    /// <summary>Reads the current configuration document.</summary>
    Task<ConfigDocumentRead> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates whether the configuration store is authoritative.
///
/// <para>
/// Separate from <see cref="Shadow.IConfigShadowGate"/> because the two flags are independent by design
/// (#2766 AC1/AC2). A single gate would let one flag's evaluation failure silently change the other's
/// answer, which is precisely the coupling the two-flag split exists to prevent.
/// </para>
/// </summary>
public interface IConfigStoreAuthoritativeGate
{
    /// <summary>Whether <see cref="Shadow.ConfigStoreFeatures.Authoritative"/> is enabled.</summary>
    Task<bool> IsAuthoritativeAsync(CancellationToken cancellationToken = default);
}
