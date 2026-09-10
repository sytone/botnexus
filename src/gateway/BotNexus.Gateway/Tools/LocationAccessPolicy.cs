using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Decides which agents may see a configured location.
/// </summary>
/// <remarks>
/// <para>
/// The connection registry already holds the harder property: an agent never receives a
/// credential, and an architecture fence proves no credential-bearing field crosses the
/// projection. What it did not hold is <i>scope</i> — every agent with <c>list_locations</c> saw
/// every configured location, so an agent that legitimately needs one internal API could also
/// enumerate the Proxmox host, the NAS and the database. Not their passwords; but an endpoint
/// plus a username is most of a targeting problem.
/// </para>
/// <para>
/// Deliberately a separate type from the tool that calls it. Acting on a location is a different
/// tool from listing them, and when one is written it must consult the same rule rather than
/// reimplement it — the failure mode being a location an agent cannot see but can still act on.
/// </para>
/// </remarks>
public static class LocationAccessPolicy
{
    /// <summary>Wildcard granting every agent.</summary>
    private const string Everyone = "*";

    /// <summary>
    /// Whether <paramref name="agentId"/> may see <paramref name="location"/>.
    /// </summary>
    /// <param name="location">The configured location.</param>
    /// <param name="agentId">The agent asking; <see langword="null"/> for an unidentified caller.</param>
    /// <returns><see langword="true"/> when the location is visible to that agent.</returns>
    public static bool IsVisibleTo(LocationConfig? location, AgentId? agentId)
    {
        if (location is null)
            return false;

        // ABSENT means unrestricted. This is the upgrade default and the reason adding the field
        // changes nothing for an existing install: no operator has written a list yet, so every
        // location stays visible exactly as before.
        if (location.Agents is null)
            return true;

        // A PRESENT list is exactly what it says - including an empty one, which grants nobody.
        // That asymmetry with `null` is the point: it is how a location is taken out of
        // circulation without deleting its configuration, and it cannot be reached by accident
        // because it requires writing the key.
        if (location.Agents.Count == 0)
            return false;

        if (location.Agents.Any(entry => string.Equals(entry?.Trim(), Everyone, StringComparison.Ordinal)))
            return true;

        // An unidentified caller matches no named grant. It reaches a wildcard above, because a
        // location marked "everyone" genuinely is; it must not fall through to a named list.
        if (agentId is not { } caller)
            return false;

        return location.Agents.Any(entry =>
            !string.IsNullOrWhiteSpace(entry)
            && string.Equals(entry.Trim(), caller.Value, StringComparison.OrdinalIgnoreCase));
    }
}
