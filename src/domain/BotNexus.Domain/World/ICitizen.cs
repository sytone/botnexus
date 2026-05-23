namespace BotNexus.Domain.World;

/// <summary>
/// Marker for any citizen of a BotNexus world. Both <c>User</c> (Phase 2) and
/// <see cref="BotNexus.Gateway.Abstractions.Models.AgentDescriptor"/> implement this so
/// cross-cutting code (channel routing, permissions, participant lists, audit) can
/// address either species through a single typed lens.
/// </summary>
/// <remarks>
/// <para>Phase 1.5 keeps the surface deliberately narrow:</para>
/// <list type="bullet">
///   <item><see cref="Id"/> — the discriminated <see cref="CitizenId"/>.</item>
///   <item><see cref="DisplayName"/> — short, human-readable label suitable for UI.</item>
/// </list>
/// <para>
/// Other plausible members — <c>WorldIdentity World</c>, profile/avatar, last-seen
/// timestamp — are intentionally deferred. Agents do not yet carry a back-reference to
/// their <see cref="WorldDescriptor"/> (world membership is denormalised through
/// <see cref="WorldDescriptor.HostedAgents"/>); the interface stays minimal so adoption
/// is mechanical and Phase 7 can lift <c>World</c> when worlds become first-class.
/// </para>
/// </remarks>
public interface ICitizen
{
    /// <summary>The citizen's typed identity (user or agent).</summary>
    CitizenId Id { get; }

    /// <summary>Short, human-readable display name (UI lists, participant tags, audit lines).</summary>
    string DisplayName { get; }
}
