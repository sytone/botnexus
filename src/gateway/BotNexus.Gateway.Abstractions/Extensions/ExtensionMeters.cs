using BotNexus.Gateway.Telemetry;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// Naming rules and guardrails for the extension telemetry seam (#1852). Every metric an
/// extension emits is auto-prefixed into its own <c>botnexus.ext.&lt;id&gt;.*</c> instrument
/// namespace, and every durable usage row it records is isolated under the consumer namespace
/// equal to the extension id. This gives extensions the same telemetry seam the platform core
/// uses (<see cref="IMetrics"/> / <see cref="BotNexusMeters"/>) with no privileged internal-only
/// path, while preventing an extension from colliding with (or spoofing) platform instruments
/// such as <c>botnexus.turns.*</c>.
/// </summary>
public static class ExtensionMeters
{
    /// <summary>
    /// The fixed instrument-name prefix that scopes every extension-emitted instrument, e.g.
    /// <c>botnexus.ext.skills.loads</c>. The trailing dot is included.
    /// </summary>
    public const string InstrumentPrefix = "botnexus.ext.";

    /// <summary>
    /// The reserved platform instrument prefix. An extension may never emit an instrument whose
    /// (unprefixed) leaf begins with this, so it can never shadow a platform instrument.
    /// </summary>
    public const string PlatformPrefix = "botnexus.";

    /// <summary>
    /// Validates an extension id used as both the instrument-namespace segment and the durable
    /// usage-telemetry consumer namespace. Ids must be lowercase, dot-free, and URL/segment safe
    /// so the resulting instrument name stays a single well-formed dotted path.
    /// </summary>
    /// <param name="extensionId">The extension identifier (e.g. <c>skills</c>).</param>
    /// <returns>The validated id.</returns>
    /// <exception cref="ArgumentException">The id is blank or contains disallowed characters.</exception>
    public static string ValidateExtensionId(string extensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);

        foreach (var c in extensionId)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
            {
                throw new ArgumentException(
                    $"Extension id '{extensionId}' is invalid. Use lowercase letters, digits, and hyphens only " +
                    "(no dots, spaces, or uppercase) so it forms a single well-formed instrument-namespace segment.",
                    nameof(extensionId));
            }
        }

        return extensionId;
    }

    /// <summary>
    /// Builds a convention-compliant, namespace-prefixed instrument name of the form
    /// <c>botnexus.ext.&lt;extensionId&gt;.&lt;name&gt;</c>. The caller supplies only the leaf
    /// name (e.g. <c>loads</c>); this method enforces the prefix so extensions cannot escape their
    /// namespace.
    /// </summary>
    /// <param name="extensionId">The owning extension id.</param>
    /// <param name="name">The unprefixed instrument leaf name (e.g. <c>loads</c>).</param>
    /// <returns>The fully-qualified instrument name.</returns>
    /// <exception cref="ArgumentException">
    /// The name is blank, or attempts to reach into the reserved platform namespace by starting with
    /// <c>botnexus.</c> (guarding against, e.g., an extension emitting <c>botnexus.turns.total</c>).
    /// </exception>
    public static string InstrumentName(string extensionId, string name)
    {
        ValidateExtensionId(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.StartsWith(PlatformPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Extension '{extensionId}' attempted to emit instrument '{name}', which reaches into the reserved " +
                $"platform namespace '{PlatformPrefix}*'. Pass only the leaf name (e.g. 'loads'); it is auto-prefixed " +
                $"to '{InstrumentPrefix}{extensionId}.<name>' so it cannot collide with platform instruments such as " +
                "'botnexus.turns.*'.",
                nameof(name));
        }

        return $"{InstrumentPrefix}{extensionId}.{name}";
    }
}
