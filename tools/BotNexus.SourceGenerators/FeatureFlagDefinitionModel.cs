namespace BotNexus.SourceGenerators;

using System;

/// <summary>
/// One parsed entry from <c>feature-flags.json</c>, carried between
/// <see cref="FeatureFlagJsonParser"/> and <see cref="FeatureFlagCodeGenerator"/>.
/// <para>
/// Deliberately narrower than the Oro reference model this design is derived from: BotNexus has
/// no stamps, no multi-application deployment topology and no dev/prod split, so
/// <c>stampNames</c>, <c>stampNumber</c>, <c>applications</c> and the
/// <c>defaultProductionState</c>/<c>defaultDevelopmentState</c> pair are absent by design rather
/// than pending. A single <see cref="DefaultState"/> is the whole answer to "what is this set to
/// when nobody has said?" - an environment-dependent default would reintroduce exactly the
/// ambiguity the inventory exists to remove.
/// </para>
/// </summary>
public sealed class FeatureFlagDefinitionModel
{
    /// <summary>Gets or sets the flag key as it appears under the <c>FeatureManagement</c> config section.</summary>
    public string FeatureName { get; set; } = string.Empty;

    /// <summary>Gets or sets the operator-facing explanation of what enabling the flag changes.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets who to ask about this flag. Required, so a flag cannot outlive its owner's memory.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets when the flag was introduced; the basis for the staleness warning.</summary>
    public DateTime DateAdded { get; set; }

    /// <summary>Gets or sets the value applied when the flag is absent from configuration.</summary>
    public bool DefaultState { get; set; }

    /// <summary>Gets or sets the retirement date. When set, every call site is marked obsolete.</summary>
    public DateTime? DateRetired { get; set; }

    /// <summary>Gets or sets a value opting an intentionally enduring flag out of the staleness warning.</summary>
    public bool IgnoreFlagAge { get; set; }
}
