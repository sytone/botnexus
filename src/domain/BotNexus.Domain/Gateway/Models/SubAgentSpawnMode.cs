using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Discriminated union of the two ways a sub-agent can be brought into existence:
/// <see cref="Embody"/> — embodying a role (researcher / coder / planner / etc.)
/// with optional per-spawn customisation; or <see cref="Mirror"/> — mirroring an
/// already-registered named agent, using its descriptor as-is.
///
/// <para>This type was introduced as part of the Phase 5 / F-6 typed-discriminator
/// refactor that replaced the bag of optional <c>TargetAgentId</c> /
/// <c>SystemPromptOverride</c> / etc. fields on <see cref="SubAgentSpawnRequest"/>
/// with an explicit one-of. See GitHub issue #562.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(Embody), "embody")]
[JsonDerivedType(typeof(Mirror), "mirror")]
public abstract record SubAgentSpawnMode;

/// <summary>
/// Spawn a sub-agent that embodies the supplied role. The role is required and
/// must be one of the known <see cref="SubAgentArchetype"/> values
/// (Researcher / Coder / Planner / Reviewer / Writer / General); unknown values
/// throw <see cref="ArgumentException"/> at construction.
/// </summary>
public sealed record Embody : SubAgentSpawnMode
{
    private static readonly HashSet<string> s_knownRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        SubAgentArchetype.Researcher.Value,
        SubAgentArchetype.Coder.Value,
        SubAgentArchetype.Planner.Value,
        SubAgentArchetype.Reviewer.Value,
        SubAgentArchetype.Writer.Value,
        SubAgentArchetype.General.Value
    };

    /// <summary>
    /// Gets the role this sub-agent embodies. Required and validated against the
    /// 6 known <see cref="SubAgentArchetype"/> static values at construction time
    /// (closed-enum semantics — unknown smart-enum values produced by the open
    /// <see cref="SubAgentArchetype.FromString"/> registry are rejected here so
    /// the spawn contract cannot smuggle arbitrary role names through).
    /// </summary>
    public SubAgentArchetype Role { get; }

    /// <summary>
    /// Gets the optional per-spawn customisations applied on top of the role's
    /// defaults. Use <see cref="EmbodyCustomizations.Default"/> when no overrides
    /// are required.
    /// </summary>
    public EmbodyCustomizations Customizations { get; }

    /// <summary>
    /// Initialises a new <see cref="Embody"/> spawn mode.
    /// </summary>
    /// <param name="Role">Required role; must be a known <see cref="SubAgentArchetype"/> static value.</param>
    /// <param name="Customizations">Optional per-spawn overrides. Defaults to <see cref="EmbodyCustomizations.Default"/>.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="Role"/> is not a known archetype.</exception>
    [JsonConstructor]
    public Embody(SubAgentArchetype Role, EmbodyCustomizations Customizations)
    {
        ArgumentNullException.ThrowIfNull(Role);
        ArgumentNullException.ThrowIfNull(Customizations);
        if (!s_knownRoles.Contains(Role.Value))
            throw new ArgumentException(
                $"Embody.Role must be one of the known SubAgentArchetype values " +
                $"(researcher / coder / planner / reviewer / writer / general); got '{Role.Value}'.",
                nameof(Role));

        this.Role = Role;
        this.Customizations = Customizations;
    }

    /// <summary>
    /// Convenience constructor that defaults <see cref="Customizations"/> to
    /// <see cref="EmbodyCustomizations.Default"/>.
    /// </summary>
    public Embody(SubAgentArchetype Role) : this(Role, EmbodyCustomizations.Default) { }
}

/// <summary>
/// Spawn a sub-agent that mirrors a registered named agent. The named agent's
/// descriptor (system prompt, model, tools, display name) is used as-is. Only
/// the delegated <see cref="SubAgentSpawnRequest.Task"/> (top-level on the spawn
/// request) differs from the named agent's normal operation. By design no
/// per-spawn overrides are accepted; the agent-facing JSON tool returns a tool
/// error when callers mix <c>targetAgentId</c> with override fields.
/// </summary>
/// <param name="TargetAgentId">Identifier of the registered agent to mirror. Must resolve in the agent registry at spawn time.</param>
public sealed record Mirror(AgentId TargetAgentId) : SubAgentSpawnMode;
