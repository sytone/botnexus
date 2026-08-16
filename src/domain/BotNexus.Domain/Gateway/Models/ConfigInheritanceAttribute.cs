namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// How a configuration property behaves when the same property is set at more than one layer of a
/// layered configuration domain (for example world <c>agents.defaults</c> underneath a per-agent
/// block).
/// </summary>
/// <remarks>
/// <para>
/// Before this enum existed, a property's inheritance behaviour was implied by whichever branch of a
/// hand-written merge helper happened to mention it. That made an omission invisible: a property
/// nobody remembered to add to <c>AgentConfigMerger</c> silently took the agent-local value and
/// discarded the inherited one, and no test could tell the difference between "deliberately
/// agent-local" and "forgotten" (#2137, #2423). Declaring the intent turns that silent omission into
/// a failing test.
/// </para>
/// <para>
/// This is a declaration of intent, not an implementation. The value states what SHOULD happen; the
/// merge engine reads it to decide what DOES happen. Divergence between the two is exactly the class
/// of defect the classification exists to expose, so a policy must never be chosen to describe
/// current behaviour that is itself considered wrong - fix the behaviour, or record the disagreement
/// on the issue.
/// </para>
/// </remarks>
public enum ConfigInheritancePolicy
{
    /// <summary>
    /// A single value replaced wholesale by the child layer when the child sets it. The common case
    /// for scalars: a number, string, bool, or enum where "set at the agent" simply means "use the
    /// agent's number instead of the world's".
    /// </summary>
    /// <remarks>
    /// Presence, not nullness, decides. An explicitly-null child value suppresses the inherited value
    /// rather than falling back to it - the tri-state distinction that the config store and shadow
    /// diff preserve end to end (#2646, #2766).
    /// </remarks>
    ScalarOverride = 0,

    /// <summary>
    /// A nested object merged property-by-property, so a child may set one member of a block and
    /// inherit the rest. The behaviour operators expect from <c>heartbeat</c>: setting only
    /// <c>enabled: false</c> must not discard an inherited <c>intervalMinutes</c> and
    /// <c>quietHours</c>.
    /// </summary>
    DeepMerge = 1,

    /// <summary>
    /// A value that is meaningful only as a complete set, replaced atomically when the child sets it.
    /// Correct for a list whose entries are interdependent - an allowlist, a command array - where
    /// element-wise union would silently manufacture a combination no operator ever wrote.
    /// </summary>
    /// <remarks>
    /// Choose this over <see cref="DeepMerge"/> whenever a partially-inherited value would be
    /// incoherent or, worse, quietly permissive: unioning a child's narrow tool allowlist with an
    /// inherited broad one grants access the child was written to deny.
    /// </remarks>
    ReplaceAsUnit = 2,

    /// <summary>
    /// A dictionary merged by key: keys present only in the parent survive, keys present in the child
    /// win, and the child may address one entry without restating the others. Used by the
    /// extension-configuration dictionaries, where each key is an independently-owned subtree.
    /// </summary>
    KeyedMerge = 3,

    /// <summary>
    /// Deliberately not inheritable. The property identifies or distinguishes the specific instance,
    /// so a value inherited from a shared default layer would be wrong by construction - a display
    /// name, an emoji, a description. An operator setting this at the defaults layer has almost
    /// certainly made a mistake.
    /// </summary>
    /// <remarks>
    /// This is a positive assertion, not a way to opt out of thinking. It records that inheritance
    /// was considered and rejected, which is why it satisfies the fitness test that
    /// <see langword="null"/> does not.
    /// </remarks>
    LocalOnly = 4,

    /// <summary>
    /// Populated by the runtime rather than read from operator-authored configuration, so layering
    /// never applies. Present on the DTO for serialisation or diagnostics only.
    /// </summary>
    RuntimeOnly = 5,

    /// <summary>
    /// Resolved by a named strategy that none of the other policies describes. Requires a
    /// justification so the exception stays legible to the next reader; the fitness test rejects a
    /// <see cref="ConfigInheritanceAttribute.Strategy"/> that is missing or blank.
    /// </summary>
    Custom = 6,
}

/// <summary>
/// Declares how a configuration property behaves across layers of a layered configuration domain.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="ConfigFieldAttribute"/>, which describes how a property is PRESENTED.
/// This one describes how a property is RESOLVED when set in more than one place. The two are
/// independent: a field can be hidden from the UI and still inherit, or be prominently editable and
/// deliberately local.
/// </para>
/// <para>
/// Enforced by an architecture fitness test that fails, naming the exact property path, when a
/// property in a participating graph carries no classification. The test's value is that it fails on
/// a NEW property the moment it is added, at the point where the author still knows what the
/// property means - rather than months later when an operator notices their inherited value vanished.
/// </para>
/// </remarks>
[System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConfigInheritanceAttribute : System.Attribute
{
    /// <summary>
    /// Creates a classification declaring how this property resolves across configuration layers.
    /// </summary>
    /// <param name="policy">The inheritance behaviour this property is intended to have.</param>
    public ConfigInheritanceAttribute(ConfigInheritancePolicy policy) => Policy = policy;

    /// <summary>The declared inheritance behaviour for this property.</summary>
    public ConfigInheritancePolicy Policy { get; }

    /// <summary>
    /// Name of the resolution strategy when <see cref="Policy"/> is
    /// <see cref="ConfigInheritancePolicy.Custom"/>. Required in that case and ignored otherwise.
    /// </summary>
    public string? Strategy { get; set; }

    /// <summary>
    /// Why this policy was chosen. Required for
    /// <see cref="ConfigInheritancePolicy.LocalOnly"/>, <see cref="ConfigInheritancePolicy.RuntimeOnly"/>
    /// and <see cref="ConfigInheritancePolicy.Custom"/> - the three that assert a deliberate exception
    /// rather than a default behaviour, and therefore the three where a future reader cannot
    /// reconstruct the reasoning from the policy name alone.
    /// </summary>
    public string? Justification { get; set; }
}
