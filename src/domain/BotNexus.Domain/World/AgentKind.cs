using System.Text.Json.Serialization;

namespace BotNexus.Domain.World;

/// <summary>
/// Discriminates the species sub-kind of an <see cref="BotNexus.Gateway.Abstractions.Models.AgentDescriptor"/>.
/// A named agent is a first-class World citizen registered by configuration or REST; a
/// sub-agent is a runtime-spawned ephemeral helper created by another agent via
/// <c>spawn_subagent</c>.
/// </summary>
/// <remarks>
/// <para>
/// This enum is the canonical typed signal for "is this descriptor a sub-agent?" — it
/// replaces the prior pattern of inferring sub-agent status by substring-matching
/// <c>::subagent::</c> inside <c>SessionId</c>. The <see cref="SubAgent"/> value is set
/// exactly once, by <c>DefaultSubAgentManager.SpawnAsync</c>, on the child registration
/// it creates; configuration-loaded descriptors must remain <see cref="Named"/>.
/// </para>
/// <para>
/// <see cref="Named"/> is the default so existing JSON / config / REST payloads that
/// omit <c>kind</c> round-trip with the correct intent. The
/// <c>AgentDescriptorValidator.ValidateForConfig</c> overload rejects config-loaded
/// descriptors that attempt to assert <see cref="SubAgent"/> directly — sub-agents are
/// runtime-only by construction.
/// </para>
/// <para>
/// Orthogonal to <see cref="BotNexus.Domain.Primitives.SubAgentArchetype"/>, which
/// describes a sub-agent's <em>role</em> (researcher / coder / reviewer / …). A
/// descriptor with <c>Kind == SubAgent</c> always carries an archetype on its
/// originating <c>SubAgentSpawnRequest</c>; the archetype lives on the spawn info, not
/// on the descriptor.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AgentKind>))]
public enum AgentKind
{
    /// <summary>
    /// A first-class agent registered through configuration or the REST API. The default
    /// for any descriptor that does not explicitly opt in to a different kind.
    /// </summary>
    Named = 0,

    /// <summary>
    /// A runtime-spawned ephemeral sub-agent created by another agent via
    /// <c>spawn_subagent</c>. Sub-agents inherit the parent agent's deny-list, cannot
    /// recursively spawn further sub-agents, and have no configuration persistence.
    /// </summary>
    SubAgent = 1,
}
