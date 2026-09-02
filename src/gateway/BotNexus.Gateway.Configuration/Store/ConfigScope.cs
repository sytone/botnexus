namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Which inheritance layer a configuration row belongs to.
///
/// <para>
/// <b>Layering is rows, not columns.</b> A relational column cannot express the tri-state that
/// configuration inheritance depends on: <c>NULL</c> means both "unset, inherit from the layer above"
/// and "explicitly nulled, do not inherit". Storing one row per (layer, key) makes <em>presence</em>
/// the carrier of that distinction - exactly as the JSON document does, where
/// <see cref="ConfigDocumentFlattener"/> detects it by walking the raw node graph before inspecting
/// the value.
/// </para>
///
/// <para>
/// Two consequences worth having: provenance becomes queryable ("where did this agent's
/// <c>toolTimeoutSeconds</c> come from?" is a query rather than a debugging session), and writes to
/// different layers touch different rows, so changing a world default and an agent override no longer
/// contend on a shared document.
/// </para>
/// </summary>
public enum ConfigScope
{
    /// <summary>World-level configuration - the root document outside <c>agents</c>.</summary>
    World = 0,

    /// <summary>Defaults applied to every agent in the world (<c>agents.defaults</c>).</summary>
    AgentDefault = 1,

    /// <summary>A single named agent's overrides.</summary>
    Agent = 2,
}
