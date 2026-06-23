using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// The pure result of resolving a <see cref="SubAgentSpawnRequest.Mode"/> (Embody | Mirror)
/// against the parent/target descriptors — everything <see cref="DefaultSubAgentManager.SpawnAsync"/>
/// needs to provision a child agent, with no side effects performed during resolution.
/// </summary>
/// <remarks>
/// Extracting this record lets the Embody/Mirror discriminated-union resolution
/// (<see cref="DefaultSubAgentManager.ResolveSpawnPlan"/>) be unit-tested in isolation —
/// e.g. "Embody applies a model override but Mirror does not", or "Mirror's model fallback
/// derives from the target descriptor, not the parent" (#562 / #1565) — without constructing
/// the full multi-collaborator manager and driving the 200+ line SpawnAsync method.
/// </remarks>
/// <param name="Archetype">The resolved sub-agent archetype (role slot in the child id).</param>
/// <param name="BaseDescriptor">The descriptor the child is cloned from (parent for Embody, target for Mirror).</param>
/// <param name="ChildAgentId">The minted child agent id, encoding the archetype/target slot.</param>
/// <param name="Name">Optional display name override (Embody customisation only).</param>
/// <param name="ModelOverride">Optional model override (Embody customisation only).</param>
/// <param name="ApiProviderOverride">Optional API provider override (Embody customisation only).</param>
/// <param name="ToolIds">Optional explicit tool grant (Embody customisation only).</param>
/// <param name="SystemPromptOverride">Optional system prompt override (Embody customisation only).</param>
internal sealed record SubAgentSpawnPlan(
    SubAgentArchetype Archetype,
    AgentDescriptor BaseDescriptor,
    AgentId ChildAgentId,
    string? Name,
    string? ModelOverride,
    string? ApiProviderOverride,
    IReadOnlyList<string>? ToolIds,
    string? SystemPromptOverride);
