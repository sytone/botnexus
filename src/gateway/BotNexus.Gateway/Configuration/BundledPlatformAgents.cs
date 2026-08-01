using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// A declaratively-described agent that BotNexus ships with and reconciles into
/// <c>config.json</c> on startup when the user does not already have an entry for it.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2635. The catalog is deliberately a <em>template</em>, not a live descriptor: the
/// reconciler only ever uses it to synthesise a brand-new entry. Once the entry exists on disk it
/// is the user's, and later stages that widen this template must not rewrite user values. The
/// <see cref="DefinitionVersion"/> exists so a future stage can tell "the user has never seen this
/// agent" from "the user has an older shipped shape" without diffing free-form JSON.
/// </para>
/// </remarks>
/// <param name="AgentId">The config key under <c>agents</c> (e.g. <c>nexus-trailguide</c>).</param>
/// <param name="DefinitionVersion">Monotonic version of the shipped descriptor shape.</param>
/// <param name="CreateTemplate">
/// Produces a fresh, unshared <see cref="JsonObject"/> for the agent entry. Provider, model and
/// <c>enabled</c> are filled in by the reconciler because they depend on what the installation
/// already has configured; everything else comes from here.
/// </param>
public sealed record BundledAgentDefinition(
    string AgentId,
    int DefinitionVersion,
    Func<JsonObject> CreateTemplate);

/// <summary>
/// The catalog of agents BotNexus bundles and additively reconciles into user configuration.
/// </summary>
public static class BundledPlatformAgents
{
    /// <summary>Config key for the Nexus Trailguide onboarding agent.</summary>
    public const string TrailguideAgentId = "nexus-trailguide";

    /// <summary>
    /// Current shipped shape of the Trailguide descriptor. Bump when the template gains fields
    /// that a later stage needs to distinguish from a user-authored entry.
    /// </summary>
    public const int TrailguideDefinitionVersion = 1;

    /// <summary>
    /// Metadata key under which <see cref="BundledAgentDefinition.DefinitionVersion"/> is stamped
    /// on the inserted entry.
    /// </summary>
    public const string DefinitionVersionMetadataKey = "definitionVersion";

    /// <summary>
    /// Description used when no provider/model could be resolved, so the entry is inserted
    /// disabled. It has to tell the operator what to do, because nothing else will.
    /// </summary>
    public const string UnresolvedProviderDescription =
        "Guided onboarding agent for BotNexus. Disabled because no configured agent supplied a "
        + "provider and model to copy. Set 'provider' and 'model' on agents."
        + TrailguideAgentId
        + " and set 'enabled': true to use it.";

    /// <summary>Description used when the entry is inserted ready to run.</summary>
    public const string DefaultDescription =
        "Guided onboarding agent for BotNexus. Explains the platform, its concepts and how to get started.";

    /// <summary>The Nexus Trailguide bundled agent (#2154).</summary>
    public static BundledAgentDefinition Trailguide { get; } = new(
        TrailguideAgentId,
        TrailguideDefinitionVersion,
        () => new JsonObject
        {
            ["displayName"] = "Nexus Trailguide",
            ["emoji"] = "🧭",
            ["description"] = DefaultDescription
        });

    /// <summary>All bundled agents, in reconciliation order.</summary>
    public static IReadOnlyList<BundledAgentDefinition> All { get; } = [Trailguide];
}
