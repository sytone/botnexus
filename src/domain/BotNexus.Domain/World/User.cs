using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.World;

/// <summary>
/// A human inhabitant of a BotNexus world. The User species of <see cref="ICitizen"/>,
/// symmetric with <see cref="BotNexus.Gateway.Abstractions.Models.AgentDescriptor"/>
/// on the Agent side.
/// </summary>
/// <remarks>
/// <para>
/// Phase 2 scope: a User carries a typed identity, a display name, the world they
/// live in, and the channel addresses they are reachable at. Profile data, preferences,
/// and authentication material are intentionally out of scope — they belong on a
/// separate <c>UserProfile</c> aggregate that joins by <see cref="Id"/>.
/// </para>
/// <para>
/// <see cref="World"/> appears here even though <see cref="ICitizen"/> does not yet expose
/// it (Phase 7 will lift <c>World</c> to the interface once worlds become first-class on
/// the agent side too).
/// </para>
/// </remarks>
public sealed record User : ICitizen
{
    /// <summary>The user's typed identity. Stable across renames and channel changes.</summary>
    public required UserId Id { get; init; }

    /// <summary>Short, human-readable label used in UI lists and audit lines.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The world this user is a citizen of. Mirrors the implicit world membership
    /// agents have today via <see cref="WorldDescriptor.HostedAgents"/>; lifts to
    /// <see cref="ICitizen"/> in Phase 7.
    /// </summary>
    public required WorldIdentity World { get; init; }

    /// <summary>
    /// The channel addresses this user is reachable at. Each entry is the user's
    /// own address on that channel (see <see cref="ChannelIdentity.SenderAddress"/>) —
    /// not the conversation address messages are routed through. May be empty when the
    /// user has been provisioned but has not yet bound any channel identities.
    /// </summary>
    public IReadOnlyList<ChannelIdentity> ChannelIdentities { get; init; } = [];

    /// <summary>
    /// Discriminated citizen identity. Always <see cref="CitizenId.Of(UserId)"/> for a user —
    /// satisfies <see cref="ICitizen"/> so the record can flow through cross-cutting code
    /// that addresses users and agents uniformly without losing the typed <see cref="Id"/>.
    /// </summary>
    CitizenId ICitizen.Id => CitizenId.Of(Id);
}
