using System.Reflection;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Behaviour tests for the attribute-declared prompt-variant registry (#2433).
/// </summary>
/// <remarks>
/// Every fixture declares its variants on a NESTED type and freezes a registry scoped to exactly
/// those types via <see cref="PromptVariantRegistry.FreezeTypes"/>. That scoping is not cosmetic:
/// the negative cases require MALFORMED declarations to exist in this assembly, and an
/// assembly-wide scan would then fail every other test in the file.
/// </remarks>
public sealed class PromptVariantRegistryTests
{
    private const string Section = "test-section";
    private const string Ordering = "ordering-section";
    private const string Replacing = "replacing-section";

    private static PromptVariantRegistry LadderRegistry =>
        PromptVariantRegistry.FreezeTypes([typeof(Fixture)]);

    // ---- declarations under test ----

    internal static class Fixture
    {
        [PromptVariant(Section)]
        internal static IReadOnlyList<PromptRule> Default() =>
        [
            new("alpha", "default alpha"),
            new("beta", "default beta"),
            new("gamma", "default gamma")
        ];

        [PromptVariant(Section, Family = "gpt")]
        internal static IReadOnlyList<PromptRule> Gpt() =>
        [
            new("beta", "gpt beta"),
            new("delta", "gpt delta")
        ];

        [PromptVariant(Section, Family = "gpt", Version = "5")]
        internal static IReadOnlyList<PromptRule> Gpt5() =>
        [
            new("beta", "gpt5 beta"),
            PromptRule.Remove("gamma")
        ];

        [PromptVariant(Section, Family = "gemini")]
        internal static IReadOnlyList<PromptRule> Gemini() => [new("epsilon", "gemini epsilon")];
    }

    internal static class OrderingFixture
    {
        [PromptVariant(Ordering)]
        internal static IReadOnlyList<PromptRule> Default() =>
        [
            new("one", "default one"),
            new("two", "default two"),
            new("three", "default three")
        ];

        [PromptVariant(Ordering, Family = "claude")]
        internal static IReadOnlyList<PromptRule> Claude() => [new("two", "claude two")];
    }

    internal static class ReplaceFixture
    {
        [PromptVariant(Replacing)]
        internal static IReadOnlyList<PromptRule> Default() =>
        [
            new("keep", "default keep"),
            new("drop", "default drop")
        ];

        [PromptVariant(Replacing, Family = "llama", Replace = true)]
        internal static IReadOnlyList<PromptRule> Llama() => [new("only", "llama only")];
    }

    /// <summary>A property-declared rung, proving the attribute is not method-only.</summary>
    internal static class PropertyFixture
    {
        [PromptVariant("property-section")]
        internal static IReadOnlyList<PromptRule> Default => [new("p", "property default")];
    }

    // ---- malformed declarations, each isolated to its own probe type ----

    internal static class DuplicateKeyProbe
    {
        [PromptVariant("dup")]
        internal static IReadOnlyList<PromptRule> Default() => [new("a", "a")];

        [PromptVariant("dup", Family = "gpt")]
        internal static IReadOnlyList<PromptRule> One() => [new("a", "one")];

        [PromptVariant("dup", Family = "gpt")]
        internal static IReadOnlyList<PromptRule> Two() => [new("a", "two")];
    }

    internal static class NoDefaultProbe
    {
        [PromptVariant("orphan", Family = "gpt")]
        internal static IReadOnlyList<PromptRule> Gpt() => [new("a", "a")];
    }

    internal static class VersionWithoutFamilyProbe
    {
        [PromptVariant("versionless", Version = "5")]
        internal static IReadOnlyList<PromptRule> Bad() => [new("a", "a")];
    }

    internal static class BadGrammarProbe
    {
        [PromptVariant("grammar", Family = "GPT_5")]
        internal static IReadOnlyList<PromptRule> Bad() => [new("a", "a")];
    }

    internal static class DuplicateRuleIdProbe
    {
        [PromptVariant("dupe-rule")]
        internal static IReadOnlyList<PromptRule> Bad() => [new("a", "first"), new("a", "second")];
    }

    internal static class BlankRuleIdProbe
    {
        [PromptVariant("blank-rule")]
        internal static IReadOnlyList<PromptRule> Bad() => [new("   ", "text")];
    }

    internal static class RemovalOnDefaultProbe
    {
        [PromptVariant("removal-default")]
        internal static IReadOnlyList<PromptRule> Bad() => [new("a", "a"), PromptRule.Remove("b")];
    }

    internal static class ReplaceOnDefaultProbe
    {
        [PromptVariant("replace-default", Replace = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [new("a", "a")];
    }

    internal static class WrongReturnTypeProbe
    {
        [PromptVariant("wrong-return")]
        internal static string Bad() => "not rules";
    }

    internal static class ParameterisedProbe
    {
        [PromptVariant("parameterised")]
        internal static IReadOnlyList<PromptRule> Bad(int unused) => [new("a", unused.ToString())];
    }

    internal static class UnparseableVersionProbe
    {
        [PromptVariant("bad-version")]
        internal static IReadOnlyList<PromptRule> Default() => [new("a", "a")];

        [PromptVariant("bad-version", Family = "gpt", Version = "five")]
        internal static IReadOnlyList<PromptRule> Bad() => [new("a", "b")];
    }

    // ---- default rung: the fail-open is gone ----

    [Fact]
    public void Resolve_UnknownFamily_FallsBackToTheDefaultRung_NotToNothing()
    {
        // The whole point of #2433: the old switch returned [] for any family it had never heard of.
        var lines = LadderRegistry.Resolve(Section, "some-unheard-of-family", "some-unheard-of-family-9");

        lines.ShouldBe(["default alpha", "default beta", "default gamma"]);
    }

    [Fact]
    public void Resolve_NullFamily_FallsBackToTheDefaultRung()
    {
        LadderRegistry.Resolve(Section, family: null)
            .ShouldBe(["default alpha", "default beta", "default gamma"]);
    }

    [Fact]
    public void Resolve_LiteralUnknownFamilySentinel_FallsBackToTheDefaultRung()
    {
        // ModelFamilyDetector returns the literal "unknown" rather than null; that sentinel must not
        // be treated as a family that could match a rung.
        LadderRegistry.Resolve(Section, ModelFamilyDetector.Unknown, "phi-4")
            .ShouldBe(["default alpha", "default beta", "default gamma"]);
    }

    [Fact]
    public void Resolve_SectionThatDeclaresNoVariants_ReturnsEmpty()
    {
        LadderRegistry.Resolve("no-such-section", "gpt").ShouldBeEmpty();
        LadderRegistry.HasSection("no-such-section").ShouldBeFalse();
        LadderRegistry.HasSection(Section).ShouldBeTrue();
    }

    // ---- overlay by rule id ----

    [Fact]
    public void Resolve_FamilyRung_OverlaysDefaultByRuleId()
    {
        var lines = LadderRegistry.Resolve(Section, "gpt", "gpt-4o");

        // beta is REWORDED in place, delta is ADDED, alpha and gamma are inherited untouched.
        lines.ShouldBe(["default alpha", "gpt beta", "default gamma", "gpt delta"]);
    }

    [Fact]
    public void Resolve_OverlayingARule_KeepsItsInheritedPosition()
    {
        // A reworded rule must not jump to the end: the instruction order around it is meaningful.
        var registry = PromptVariantRegistry.FreezeTypes([typeof(OrderingFixture)]);

        registry.Resolve(Ordering, "claude", "claude-opus-5")
            .ShouldBe(["default one", "claude two", "default three"]);
    }

    [Fact]
    public void Resolve_FamilyThatOnlyAdds_KeepsEveryDefaultRule()
    {
        LadderRegistry.Resolve(Section, "gemini", "gemini-2.5-pro")
            .ShouldBe(["default alpha", "default beta", "default gamma", "gemini epsilon"]);
    }

    // ---- ladder precedence ----

    [Fact]
    public void Resolve_FamilyAndVersion_WinsOverFamily_WhichWinsOverDefault()
    {
        var lines = LadderRegistry.Resolve(Section, "gpt", "gpt-5");

        // alpha: default. beta: gpt-5 beats gpt beats default. gamma: removed by the gpt-5 rung.
        // delta: contributed by the gpt rung and inherited through the version rung.
        lines.ShouldBe(["default alpha", "gpt5 beta", "gpt delta"]);
    }

    [Fact]
    public void Resolve_FamilyMatchesButVersionDoesNot_StopsAtTheFamilyRung()
    {
        LadderRegistry.Resolve(Section, "gpt", "gpt-4o")
            .ShouldBe(["default alpha", "gpt beta", "default gamma", "gpt delta"]);
    }

    [Fact]
    public void Resolve_ModelIdCarriesNoVersion_StopsAtTheFamilyRung()
    {
        LadderRegistry.Resolve(Section, "gpt", "gpt")
            .ShouldBe(["default alpha", "gpt beta", "default gamma", "gpt delta"]);
    }

    [Fact]
    public void Resolve_RemovalRule_DropsTheInheritedRuleAndEmitsNoBlankLine()
    {
        var lines = LadderRegistry.Resolve(Section, "gpt", "gpt-5");

        lines.ShouldNotContain("default gamma");
        lines.ShouldAllBe(static line => !string.IsNullOrWhiteSpace(line));
    }

    // ---- Replace escape hatch ----

    [Fact]
    public void Resolve_ReplaceRung_DiscardsTheDefaultEntirely()
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(ReplaceFixture)]);

        var lines = registry.Resolve(Replacing, "llama", "llama-3");

        lines.ShouldBe(["llama only"]);
        lines.ShouldNotContain("default keep");
    }

    [Fact]
    public void Resolve_ReplaceRung_DoesNotAffectOtherFamilies()
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(ReplaceFixture)]);

        registry.Resolve(Replacing, "gpt", "gpt-5").ShouldBe(["default keep", "default drop"]);
    }

    // ---- property-declared rungs ----

    [Fact]
    public void Freeze_AcceptsAPropertyDeclaredRung()
    {
        PromptVariantRegistry.FreezeTypes([typeof(PropertyFixture)])
            .Resolve("property-section", null)
            .ShouldBe(["property default"]);
    }

    // ---- malformed declarations are rejected AT FREEZE TIME ----

    [Fact]
    public void Freeze_DuplicateVariantKey_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(DuplicateKeyProbe)]));

        error.Message.ShouldContain("Duplicate");
    }

    [Fact]
    public void Freeze_FamilyVariantWithNoDefaultRung_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(NoDefaultProbe)]));

        error.Message.ShouldContain("no DEFAULT rung");
    }

    [Fact]
    public void Freeze_VersionWithoutFamily_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(VersionWithoutFamilyProbe)]));

        error.Message.ShouldContain("no Family");
    }

    [Fact]
    public void Freeze_FamilyViolatingTheSharedTokenGrammar_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(BadGrammarProbe)]));

        error.Message.ShouldContain("token grammar");
    }

    [Fact]
    public void Freeze_UnparseableVersion_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(UnparseableVersionProbe)]));

        error.Message.ShouldContain("ModelFamilyVersion");
    }

    [Fact]
    public void Freeze_DuplicateRuleIdWithinOneRung_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(DuplicateRuleIdProbe)]));

        error.Message.ShouldContain("twice");
    }

    [Fact]
    public void Freeze_BlankRuleId_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(BlankRuleIdProbe)]));

        error.Message.ShouldContain("blank id");
    }

    [Fact]
    public void Freeze_RemovalRuleOnTheDefaultRung_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(RemovalOnDefaultProbe)]));

        error.Message.ShouldContain("removal-shaped");
    }

    [Fact]
    public void Freeze_ReplaceOnTheDefaultRung_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(ReplaceOnDefaultProbe)]));

        error.Message.ShouldContain("nothing beneath the default");
    }

    [Fact]
    public void Freeze_MemberReturningTheWrongType_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(WrongReturnTypeProbe)]));

        error.Message.ShouldContain("IReadOnlyList<PromptRule>");
    }

    [Fact]
    public void Freeze_ParameterisedMember_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => PromptVariantRegistry.FreezeTypes([typeof(ParameterisedProbe)]));

        error.Message.ShouldContain("parameterless");
    }

    // ---- the startup-frozen constraint ----

    [Fact]
    public void Resolve_DoesNotReflect_TheScanCounterIsUnchangedAcrossManyPromptBuilds()
    {
        // #2433 states reflection must NOT happen on the prompt-build path -- it would be a per-turn
        // cost on every agent. A counter incremented inside the freeze is the machine-checkable form
        // of that constraint: were Resolve to reflect, this delta could not be zero.
        var registry = LadderRegistry;
        var before = PromptVariantRegistry.ReflectionScans;

        for (var i = 0; i < 500; i++)
        {
            registry.Resolve(Section, "gpt", "gpt-5");
            registry.Resolve(Section, "unheard-of", "phi-4");
        }

        (PromptVariantRegistry.ReflectionScans - before).ShouldBe(0);
    }

    [Fact]
    public void Freeze_DoesReflect_SoTheScanCounterIsNotVacuous()
    {
        // Anti-vacuity for the test above: a counter that never moved at all would make the
        // zero-delta assertion pass for entirely the wrong reason.
        var before = PromptVariantRegistry.ReflectionScans;

        PromptVariantRegistry.FreezeTypes([typeof(Fixture)]);

        PromptVariantRegistry.ReflectionScans.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void SharedRegistry_IsFrozenOnce_AndReturnsTheSameInstance()
    {
        PromptVariantRegistry.Shared.ShouldBeSameAs(PromptVariantRegistry.Shared);
    }

    [Fact]
    public void SharedRegistry_ResolutionIsRepeatableAndTriggersNoNewScan()
    {
        // Touch Shared once first so its lazy construction is not attributed to the measured window.
        PromptVariantRegistry.Shared.HasSection(ModelGuidanceSection.Id).ShouldBeTrue();
        var before = PromptVariantRegistry.ReflectionScans;

        var first = PromptVariantRegistry.Shared.Resolve(ModelGuidanceSection.Id, ModelFamilyDetector.Claude, "claude-opus-5");
        var second = PromptVariantRegistry.Shared.Resolve(ModelGuidanceSection.Id, ModelFamilyDetector.Claude, "claude-opus-5");

        first.ShouldBe(second);
        (PromptVariantRegistry.ReflectionScans - before).ShouldBe(0);
    }

    [Fact]
    public void PromptRule_Remove_ProducesANullTextRule()
    {
        PromptRule.Remove("x").Text.ShouldBeNull();
        PromptRule.Remove("x").Id.ShouldBe("x");
    }

    [Fact]
    public void Attribute_AllowsMultipleDeclarationsOnOneMember()
    {
        var usage = typeof(PromptVariantAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        usage.ShouldNotBeNull();
        usage!.AllowMultiple.ShouldBeTrue();
    }
}
