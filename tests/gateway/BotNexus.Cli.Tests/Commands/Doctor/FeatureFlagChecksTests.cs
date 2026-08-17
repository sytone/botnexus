using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using Shouldly;
using Xunit;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Covers the feature-flag doctor surface added by #2767: absent flags reported and seeded (AC3),
/// unrecognised keys surfaced (AC4), and the fix satisfying its own check on re-run (AC5).
/// #2887 moved these off raw <c>JsonObject</c> indexing onto the canonical-path surface; the
/// assertions are unchanged in meaning.
/// </summary>
public class FeatureFlagChecksTests
{
    private static ConfigDocument Root(string json) => ConfigDocument.Parse(json);

    /// <summary>A document carrying every declared flag at its documented default.</summary>
    private static ConfigDocument AllFlagsPresent()
    {
        var config = ConfigDocument.Empty();
        foreach (var flag in FeatureFlags.All)
            config.Set($"{FeatureFlags.SectionName}.{flag.Name}", flag.Default);
        return config;
    }

    /// <summary>
    /// A document carrying every declared flag at its documented default, except
    /// <paramref name="overrideFlag"/> which is written with <paramref name="value"/> under
    /// <paramref name="keySpelling"/> (defaulting to the flag's own name).
    /// <para>
    /// These fixtures were originally written against a single-flag inventory, where "the section
    /// contains this one flag" and "every declared flag has a stated decision" were the same
    /// sentence. They are not, and #2621 adding a second flag is what separated them. The
    /// assertion is unchanged - the check must not nag about a flag whose value is stated - the
    /// fixture is just no longer relying on there being exactly one flag in existence.
    /// </para>
    /// </summary>
    private static ConfigDocument AllFlagsPresentExcept(string overrideFlag, object value, string? keySpelling = null)
    {
        var config = ConfigDocument.Empty();
        foreach (var flag in FeatureFlags.All)
        {
            if (string.Equals(flag.Name, overrideFlag, StringComparison.OrdinalIgnoreCase))
                continue;
            config.Set($"{FeatureFlags.SectionName}.{flag.Name}", flag.Default);
        }

        config.Set($"{FeatureFlags.SectionName}.{keySpelling ?? overrideFlag}", value);
        return config;
    }

    // ── AC3: absent declared flags are reported ─────────────────────────────────────────

    [Fact]
    public void SeedCheck_ApplicableWhenSectionIsAbsentEntirely()
    {
        new FeatureFlagSeedCheck().IsApplicable(Root("{}")).ShouldBeTrue();
    }

    [Fact]
    public void SeedCheck_ApplicableWhenSectionExistsButFlagIsMissing()
    {
        var root = Root("""{"FeatureManagement":{}}""");
        new FeatureFlagSeedCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void SeedCheck_ReportsTheAbsentFlagByName()
    {
        var absent = FeatureFlagSeedCheck.AbsentFlags(Root("{}"));
        absent.ShouldContain(flag => flag.Name == FeatureFlags.GatewayDevOriginEnforcement);
    }

    // AC8 mutation target: making the check ignore absent flags must redden this by name.
    [Fact]
    public void SeedCheck_NotApplicableWhenEveryDeclaredFlagIsPresent()
    {
        new FeatureFlagSeedCheck().IsApplicable(AllFlagsPresent()).ShouldBeFalse();
    }

    [Fact]
    public void SeedCheck_NotApplicableWhenFlagIsExplicitlyFalse()
    {
        // A deliberate "off" IS a stated decision - the check must not nag about it.
        var root = AllFlagsPresentExcept(FeatureFlags.GatewayDevOriginEnforcement, false);
        new FeatureFlagSeedCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void SeedCheck_TreatsDifferentlyCasedKeyAsPresent()
    {
        // Sad path: the binder is case-insensitive, so demanding exact case would make the fix
        // write a duplicate key shadowing the operator's real value.
        var root = AllFlagsPresentExcept(
            FeatureFlags.GatewayDevOriginEnforcement,
            true,
            keySpelling: FeatureFlags.GatewayDevOriginEnforcement.ToLowerInvariant());
        new FeatureFlagSeedCheck().IsApplicable(root).ShouldBeFalse();
    }

    // ── AC3/AC5: seeding writes defaults and satisfies its own check ────────────────────

    [Fact]
    public void SeedCheck_ApplyWritesEveryDeclaredFlagWithItsDocumentedDefault()
    {
        var root = Root("{}");
        new FeatureFlagSeedCheck().Apply(root);

        foreach (var flag in FeatureFlags.All)
            root.GetBool($"{FeatureFlags.SectionName}.{flag.Name}").ShouldBe(flag.Default);
    }

    [Fact]
    public void SeedCheck_ApplyIsIdempotentAndSatisfiesItsOwnCheck()
    {
        // AC5 in full: apply, then a re-run reports nothing, and a second apply changes nothing.
        var check = new FeatureFlagSeedCheck();
        var root = Root("{}");

        check.Apply(root);
        check.IsApplicable(root).ShouldBeFalse("the fix must satisfy its own check (AC5).");

        var afterFirst = root.ToJsonString();
        check.Apply(root);
        root.ToJsonString().ShouldBe(afterFirst);
    }

    [Fact]
    public void SeedCheck_ApplyPreservesAnOperatorsExistingValue()
    {
        // Seeding must never revert a deliberate choice.
        var root = Root("{\"FeatureManagement\":{\"" + FeatureFlags.GatewayDevOriginEnforcement + "\":true}}");
        new FeatureFlagSeedCheck().Apply(root);

        root.GetBool($"{FeatureFlags.SectionName}.{FeatureFlags.GatewayDevOriginEnforcement}")
            .ShouldBe(true);
    }

    [Fact]
    public void SeedCheck_ApplyPreservesUnrelatedKeysAndTheFilterObjectForm()
    {
        var root = Root("""{"FeatureManagement":{"SomeExtensionFlag":{"EnabledFor":[{"Name":"Percentage"}]}}}""");
        new FeatureFlagSeedCheck().Apply(root);

        root.HasObject($"{FeatureFlags.SectionName}.SomeExtensionFlag").ShouldBeTrue();
        root.HasNonEmptyList($"{FeatureFlags.SectionName}.SomeExtensionFlag.EnabledFor").ShouldBeTrue();
    }

    // ── AC7: inventory and config cannot silently diverge ───────────────────────────────

    [Fact]
    public void SeedCheck_IsDrivenByTheInventoryNotAHardCodedList()
    {
        // A config holding every CURRENTLY declared flag is clean; the same config would report a
        // gap the moment a new flag joined the inventory, because the absent set is derived from
        // FeatureFlags.All rather than restated here.
        var root = AllFlagsPresent();
        FeatureFlagSeedCheck.AbsentFlags(root).ShouldBeEmpty();

        root.TryRemoveEntry(FeatureFlags.SectionName, FeatureFlags.GatewayDevOriginEnforcement, out _)
            .ShouldBeTrue();

        FeatureFlagSeedCheck.AbsentFlags(root)
            .ShouldContain(flag => flag.Name == FeatureFlags.GatewayDevOriginEnforcement);
    }

    // ── AC4: unrecognised keys are surfaced ─────────────────────────────────────────────

    [Fact]
    public void UnknownAdvisory_NotApplicableWhenSectionIsAbsent()
    {
        new UnknownFeatureFlagAdvisory().IsApplicable(Root("{}")).ShouldBeFalse();
    }

    [Fact]
    public void UnknownAdvisory_NotApplicableWhenEveryKeyIsDeclared()
    {
        var root = Root("{\"FeatureManagement\":{\"" + FeatureFlags.GatewayDevOriginEnforcement + "\":true}}");
        new UnknownFeatureFlagAdvisory().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void UnknownAdvisory_FlagsAMisspelledKey()
    {
        // The motivating case: a typo evaluates as absent, so the guard stays off while the
        // operator believes it is on.
        var root = Root("""{"FeatureManagement":{"GatewayDevOriginEnforcment":true}}""");
        var advisory = new UnknownFeatureFlagAdvisory();

        advisory.IsApplicable(root).ShouldBeTrue();
        advisory.Describe(root).ShouldContain("GatewayDevOriginEnforcment");
    }

    [Fact]
    public void UnknownAdvisory_DoesNotFlagADifferentlyCasedDeclaredKey()
    {
        var root = Root("""{"FeatureManagement":{"gatewaydevoriginenforcement":true}}""");
        new UnknownFeatureFlagAdvisory().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void UnknownAdvisory_RemediationNamesTheDeclaredFlags()
    {
        new UnknownFeatureFlagAdvisory().Remediation
            .ShouldContain(FeatureFlags.GatewayDevOriginEnforcement);
    }

    // ── Registration ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedCheck_IsRegisteredWithDoctorConfig()
    {
        // An unregistered check is dead code: it would pass every unit test and report nothing.
        DoctorConfigCommand.Checks.ShouldContain(check => check is FeatureFlagSeedCheck);
        DoctorConfigCommand.Advisories.ShouldContain(a => a is UnknownFeatureFlagAdvisory);
    }
}
