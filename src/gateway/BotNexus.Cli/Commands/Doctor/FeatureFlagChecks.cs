using BotNexus.Gateway.Configuration;
using BotNexus.Cli.Commands.Doctor.Generated;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Reports every declared feature flag that is absent from configuration, and seeds each one with
/// its documented default (#2767 AC3/AC5/AC7).
/// <para>
/// A flag missing from config.json is not "off" in any way an operator can observe - it is an
/// unstated decision. Absence, a deliberate <c>false</c>, an unrecognised name and a failed
/// evaluation all produce the same runtime answer, so reading the file cannot distinguish a feature
/// someone turned off from one nobody has heard of. This check makes the decision explicit.
/// </para>
/// <para>
/// Seeding writes the <b>documented default</b>, so applying it is behaviour-preserving by
/// construction: the value written is the value that was already being applied implicitly. That is
/// what makes it safe to auto-apply, unlike <see cref="DevOriginEnforcementCheck"/>, which proposes
/// an actual behaviour change and is therefore a separate, narrower recommendation.
/// </para>
/// <para>
/// Because the inventory drives this check rather than a hard-coded list, adding a flag to
/// <see cref="FeatureFlags.All"/> without adding it to config makes this check applicable
/// automatically (AC7) - the inventory and the config cannot silently diverge.
/// </para>
/// </summary>
[DoctorCheck(Id = "feature-flags-explicit", Suite = DoctorSuite.Config, Order = 7)]
public sealed class FeatureFlagSeedCheck : IConfigCheck
{
    public string Id => "feature-flags-explicit";

    public string Description =>
        "One or more declared feature flags are absent from config - their state is an unstated "
        + "decision that cannot be read back from the file.";

    public string FixDescription =>
        $"Seed the absent flags under {FeatureFlags.SectionName} with their documented defaults "
        + "(behaviour-preserving: each written value is the default already being applied).";

    /// <inheritdoc />
    public bool IsApplicable(ConfigDocument config) => AbsentFlags(config).Count > 0;

    /// <inheritdoc />
    public void Apply(ConfigDocument config)
    {
        // Only absent flags are written. An operator's existing value - including a deliberate
        // false, and including the richer EnabledFor filter form - is never overwritten, or
        // "seeding defaults" would quietly revert their configuration.
        foreach (var flag in AbsentFlags(config))
            config.Set($"{FeatureFlags.SectionName}.{flag.Name}", flag.Default);
    }

    /// <summary>
    /// The declared flags carrying no value in configuration. Shared by <see cref="IsApplicable"/>
    /// and <see cref="Apply"/> so the set that gets reported is exactly the set that gets written -
    /// AC5 requires a re-run after applying to report nothing, which only holds if these agree.
    /// </summary>
    /// <remarks>
    /// The comparison is case-insensitive. A flag written as <c>gatewaydevoriginenforcement</c> is
    /// bound by Microsoft.FeatureManagement's configuration binder, so treating it as absent here
    /// would make this check demand a duplicate key that shadowed the operator's real value.
    /// </remarks>
    internal static IReadOnlyList<FeatureFlagDefinition> AbsentFlags(ConfigDocument config)
    {
        var present = config.GetEntryKeys(FeatureFlags.SectionName);

        return FeatureFlags.All
            .Where(flag => !present.Any(key => string.Equals(key, flag.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}

/// <summary>
/// Reports keys under <c>FeatureManagement</c> that match no declared flag (#2767 AC4).
/// <para>
/// Advisory rather than a check, deliberately: an unrecognised key is most often a typo - a
/// misspelled <c>GatewayDevOriginEnforcment</c> evaluates as absent, so the flag stays off while the
/// operator believes it is on - but it may equally be a flag belonging to an extension, or one left
/// behind by a feature that has since been removed. The tool cannot tell which, and deleting a key
/// it does not recognise risks destroying configuration it simply does not own. So it reports and
/// lets the operator decide; <see cref="IConfigAdvisory"/> has no <c>Apply</c>, which is what makes
/// that guarantee structural rather than a matter of reviewer vigilance.
/// </para>
/// </summary>
[DoctorCheck(Id = "feature-flags-unknown-key", Suite = DoctorSuite.Advisory, Order = 1)]
public sealed class UnknownFeatureFlagAdvisory : IConfigAdvisory
{
    /// <inheritdoc />
    public string Id => "feature-flags-unknown-key";

    /// <inheritdoc />
    public bool IsApplicable(ConfigDocument config) => UnknownKeys(config).Count > 0;

    /// <inheritdoc />
    public string Describe(ConfigDocument config)
    {
        var unknown = UnknownKeys(config);
        return $"{FeatureFlags.SectionName} contains {(unknown.Count == 1 ? "a key" : "keys")} matching no "
               + $"declared feature flag: {string.Join(", ", unknown)}. A misspelled flag evaluates as absent, "
               + "so the feature stays at its default while the setting appears to be applied.";
    }

    /// <inheritdoc />
    public string Remediation =>
        "Check the spelling against the declared flags ("
        + string.Join(", ", FeatureFlags.All.Select(flag => flag.Name))
        + "). If the key belongs to a feature that has been removed, delete it. Not changed "
        + "automatically - an unrecognised key may belong to an extension this tool does not know about.";

    /// <summary>Keys present under the section that are not in the inventory.</summary>
    internal static IReadOnlyList<string> UnknownKeys(ConfigDocument config)
        => config.GetEntryKeys(FeatureFlags.SectionName)
            .Where(key => !FeatureFlags.IsDeclared(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
}
