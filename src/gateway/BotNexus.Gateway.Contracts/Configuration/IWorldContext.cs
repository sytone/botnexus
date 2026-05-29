using BotNexus.Domain;

namespace BotNexus.Gateway.Abstractions.Configuration;

/// <summary>
/// Resolves the current gateway's <see cref="WorldIdentity"/> so domain components — stores,
/// routers, triggers, controllers — can stamp the world id on outbound records without depending
/// on <c>PlatformConfig</c> or the static <c>WorldIdentityResolver</c> directly. Introduced as
/// part of Phase 9 (issue #613, work item P9-A) when <c>Conversation.WorldId</c> became a
/// first-class field; expected to be used by all later P9 work items that touch world-stamped
/// records (sessions, citizens, channel bindings).
/// </summary>
/// <remarks>
/// Why an interface rather than re-using <c>IOptionsMonitor&lt;PlatformConfig&gt;</c> at every
/// site: world identity is a single, stable answer for the running gateway, and the indirection
/// keeps store/router/trigger implementations free of platform-configuration shape changes.
/// </remarks>
public interface IWorldContext
{
    /// <summary>
    /// Gets the current world's full identity (id, name, description, emoji). Resolved from
    /// configuration, with a deterministic fallback if not configured. Never <c>null</c>.
    /// </summary>
    WorldIdentity Current { get; }

    /// <summary>
    /// Gets the current world's id — shorthand for <see cref="Current"/>.<see cref="WorldIdentity.Id"/>.
    /// Always a non-empty string.
    /// </summary>
    string CurrentWorldId => Current.Id;
}
