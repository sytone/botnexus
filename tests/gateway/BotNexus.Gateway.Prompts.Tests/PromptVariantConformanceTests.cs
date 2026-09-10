using System.Reflection;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Structural conformance of the frozen prompt-variant corpus, driven by reflection over the
/// registry itself rather than over a restated copy of it (#2434).
/// </summary>
/// <remarks>
/// <para>
/// #2433 made the variants declarative. #2434 is the payoff: because the declarations are
/// ENUMERABLE, whole classes of defect become machine-checkable properties of the corpus instead of
/// anecdotes -- an orphaned <c>sectionId</c> left by a rename, a family token typo, a removal that
/// targets a rule id nobody declares, a section that quietly lost its default rung.
/// </para>
/// <para>
/// Every check here reads <see cref="PromptVariantRegistry.Declarations"/>, which is the same
/// corpus the prompt-build path resolves from. A second reflection walk written inside this test
/// project would be free to drift away from the one that ships, and would then be asserting over
/// its own data.
/// </para>
/// <para>
/// Each rule is implemented as a static <c>FindXViolations</c> helper so it can be pointed at both
/// the production registry (which must report nothing) and a deliberately malformed probe fixture
/// (which must report the violation). That pairing is the in-suite anti-vacuity evidence: a check
/// that cannot fail is indistinguishable from no check at all.
/// </para>
/// </remarks>
[Collection(ReflectionScanCollection.Name)]
public sealed class PromptVariantConformanceTests
{
    private static PromptVariantRegistry Production => PromptVariantRegistry.Shared;

    /// <summary>
    /// The section ids the prompt assembly actually registers, discovered by reflection: a built-in
    /// section is a static class carrying a <c>public const string Id</c> and a <c>Create()</c>
    /// factory. Discovering them rather than listing them is what makes the orphan check meaningful
    /// -- a hand-maintained list would be updated by the same rename that orphaned the variant.
    /// </summary>
    private static IReadOnlySet<string> RegisteredSectionIds { get; } = DiscoverSectionIds();

    // ---- rule 1: every declared sectionId resolves to a real registered section ----

    [Fact]
    public void EveryDeclaredSectionId_ResolvesToARegisteredPromptSection()
    {
        FindOrphanSectionIds(Production.Declarations, RegisteredSectionIds).ShouldBeEmpty();
    }

    [Fact]
    public void OrphanSectionIdCheck_ReportsADeclarationWhoseSectionNoLongerExists()
    {
        // Anti-vacuity: the production corpus is clean, so the check above proves nothing on its own.
        var violations = FindOrphanSectionIds(
            PromptVariantRegistry.FreezeTypes([typeof(OrphanSectionProbe)]).Declarations,
            RegisteredSectionIds);

        violations.ShouldNotBeEmpty();
        violations.ShouldContain(v => v.Contains("section-that-was-renamed-away", StringComparison.Ordinal));
    }

    [Fact]
    public void SectionDiscovery_FindsTheKnownBuiltInSections_SoTheOrphanCheckIsNotVacuous()
    {
        // A discovery walk that silently found nothing would make rule 1 pass for the wrong reason:
        // every declaration would be an orphan, and the empty-violations assertion would fail --
        // but a walk that found EVERYTHING (e.g. by returning all strings) would pass wrongly.
        RegisteredSectionIds.ShouldContain(ModelGuidanceSection.Id);
        RegisteredSectionIds.ShouldContain(ShellEfficiencySection.Id);
        RegisteredSectionIds.ShouldContain(ToolEnforcementSection.Id);
        RegisteredSectionIds.ShouldContain(SkillsGuidanceSection.Id);
        RegisteredSectionIds.ShouldNotContain("model-guidance-typo");
    }

    // ---- rule 2: every Family/Version parses via ModelFamilyVersion ----

    [Fact]
    public void EveryDeclaredFamilyAndVersion_ParsesViaModelFamilyVersion()
    {
        FindUnparseableFamilyVersions(Production.Declarations).ShouldBeEmpty();
    }

    [Fact]
    public void EveryDeclaredFamily_IsAFamilyModelFamilyDetectorCanActuallyProduce()
    {
        // A rung declared for a family string the detector never emits is unreachable: it would sit
        // in the registry looking like coverage while every real model fell through to the default.
        var detectable = typeof(ModelFamilyDetector)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(static f => (string)f.GetRawConstantValue()!)
            .Where(static family => !string.Equals(family, ModelFamilyDetector.Unknown, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        detectable.ShouldNotBeEmpty();

        var unreachable = Production.Declarations
            .Where(static d => d.Family is not null)
            .Where(d => !detectable.Contains(d.Family!))
            .Select(static d => $"{d.Site} declares unreachable family '{d.Family}'")
            .ToList();

        unreachable.ShouldBeEmpty();
    }

    // ---- rule 3: every section declaring any variant also declares a default rung ----

    [Fact]
    public void EverySectionDeclaringAnyVariant_AlsoDeclaresADefaultRung()
    {
        FindSectionsMissingADefaultRung(Production.Declarations).ShouldBeEmpty();
    }

    [Fact]
    public void EverySectionDeclaringAnyVariant_ResolvesNonEmptyForAnUnrecognisedFamily()
    {
        // The behavioural face of rule 3: no reachable state resolves to silence because the model
        // was unrecognised. This is the fail-open #2433 removed, asserted over the whole corpus.
        foreach (var sectionId in Production.SectionIds)
        {
            Production.Resolve(sectionId, "a-family-that-does-not-exist", "a-family-that-does-not-exist-9")
                .ShouldNotBeEmpty($"section '{sectionId}' resolved nothing for an unrecognised family");
        }
    }

    // ---- rule 4: no duplicate (sectionId, family, version) tuples ----

    [Fact]
    public void NoDuplicateSectionFamilyVersionTuplesAreDeclared()
    {
        FindDuplicateTuples(Production.Declarations).ShouldBeEmpty();
    }

    [Fact]
    public void DuplicateTupleCheck_ReportsTwoRungsSharingOneKey()
    {
        // The registry itself throws on a duplicate, so the corpus can never contain one; the probe
        // therefore exercises the CHECK against a synthesised declaration list rather than a frozen
        // registry. Without this the empty-violations assertion above is unfalsifiable.
        var declarations = new List<PromptVariantDeclaration>
        {
            new("s", null, null, false, [new("a", "a")], "Probe.Default"),
            new("s", "gpt", null, false, [new("a", "one")], "Probe.One"),
            new("s", "gpt", null, false, [new("a", "two")], "Probe.Two")
        };

        FindDuplicateTuples(declarations).ShouldNotBeEmpty();
    }

    [Fact]
    public void MajorAndExactZero_AreDistinctConformanceKeys()
    {
        var declarations = PromptVariantRegistry.FreezeTypes([
            typeof(PromptVariantMajorVersionTests.BaseProbe),
            typeof(PromptVariantMajorVersionTests.MajorProbe),
            typeof(PromptVariantMajorVersionTests.ExactProbe)]).Declarations;
        FindDuplicateTuples(declarations).ShouldBeEmpty();
        FindDanglingOverlayRules(declarations).ShouldBeEmpty();
    }

    [Fact]
    public void ExactRewordCheck_SeesMajorInheritedTextAndReplaceBoundary()
    {
        var declarations = new List<PromptVariantDeclaration>
        {
            new("s", null, null, false, [new("base", "base")], "Probe.Default"),
            new("s", "gpt", null, false, [new("family", "family")], "Probe.Family"),
            new("s", "gpt", new ModelVersion(6, 0), true, [new("major", "identical")], "Probe.Major") { MatchMajorVersion = true },
            new("s", "gpt", new ModelVersion(6, 1), false, [new("major", "identical"), PromptRule.Remove("base")], "Probe.Exact")
        };
        FindNoOpRewords(declarations).ShouldHaveSingleItem().ShouldContain("Probe.Exact");
        FindDanglingOverlayRules(declarations).ShouldHaveSingleItem().ShouldContain("base");
    }

    // ---- rule 5: overlay rule ids referenced by a variant exist beneath them ----

    [Fact]
    public void EveryRemovalRule_TargetsARuleIdThatActuallyExistsBeneathIt()
    {
        FindDanglingOverlayRules(Production.Declarations).ShouldBeEmpty();
    }

    [Fact]
    public void DanglingOverlayCheck_ReportsARemovalTargetingAnIdNobodyDeclares()
    {
        var violations = FindDanglingOverlayRules(
            PromptVariantRegistry.FreezeTypes([typeof(DanglingRemovalProbe)]).Declarations);

        violations.ShouldNotBeEmpty();
        violations.ShouldContain(v => v.Contains("rule-that-does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void NoOverlayRule_RestatesTheInheritedTextVerbatim()
    {
        // A reword that reproduces the default text exactly is a dead rung: it looks like the family
        // was tuned while changing nothing, and it survives every edit to the default it shadows.
        FindNoOpRewords(Production.Declarations).ShouldBeEmpty();
    }

    [Fact]
    public void NoOpRewordCheck_ReportsAnOverlayThatRestatesTheDefault()
    {
        var violations = FindNoOpRewords(
            PromptVariantRegistry.FreezeTypes([typeof(NoOpRewordProbe)]).Declarations);

        violations.ShouldNotBeEmpty();
        violations.ShouldContain(v => v.Contains("identical", StringComparison.Ordinal));
    }

    // ---- the acceptance test: total resolution over BuiltInModels ----

    [Fact]
    public void EveryBuiltInModel_ResolvesACompleteNonEmptyInstructionSet()
    {
        var models = AllBuiltInModels();

        // Guard the guard: an empty catalogue would make the loop below vacuously green.
        models.Count.ShouldBeGreaterThan(20);

        foreach (var (provider, model) in models)
        {
            var family = ModelFamilyDetector.GetModelFamily(model.Id, provider);

            foreach (var sectionId in Production.SectionIds)
            {
                var lines = Production.Resolve(sectionId, family, model.Id);

                lines.ShouldNotBeEmpty(
                    $"'{provider}/{model.Id}' (family '{family}') resolved NO lines for section '{sectionId}'");
                lines.ShouldAllBe(
                    static line => !string.IsNullOrWhiteSpace(line),
                    $"'{provider}/{model.Id}' resolved a blank line for section '{sectionId}'");
            }
        }
    }

    [Fact]
    public void EveryBuiltInModel_ResolvesDeterministically()
    {
        foreach (var (provider, model) in AllBuiltInModels())
        {
            var family = ModelFamilyDetector.GetModelFamily(model.Id, provider);

            foreach (var sectionId in Production.SectionIds)
            {
                Production.Resolve(sectionId, family, model.Id)
                    .ShouldBe(Production.Resolve(sectionId, family, model.Id));
            }
        }
    }

    [Fact]
    public void EveryBuiltInModel_ResolvesAFamilyOrTheDefaultRung_NeverAnEmptyLadder()
    {
        // Total resolution means: for a family the corpus knows, the model gets at least the default
        // set; for one it does not, it STILL gets the default set. Both directions asserted, so the
        // test cannot be satisfied by a registry that answers the default for everything by accident
        // -- at least one built-in model must resolve strictly more than the default.
        var defaultLines = Production.Resolve(ModelGuidanceSection.Id, family: null);
        defaultLines.ShouldNotBeEmpty();

        var anyModelGetsFamilySpecificGuidance = AllBuiltInModels().Any(entry =>
            !Production
                .Resolve(ModelGuidanceSection.Id, ModelFamilyDetector.GetModelFamily(entry.Model.Id, entry.Provider), entry.Model.Id)
                .SequenceEqual(defaultLines));

        anyModelGetsFamilySpecificGuidance.ShouldBeTrue(
            "no built-in model resolved anything beyond the default rung, so the family ladder is inert");
    }

    [Fact]
    public void BuiltInModelIds_AreEnumerableAndDistinctPerProvider()
    {
        // Anti-vacuity for the acceptance test's corpus: if RegisterAll ever silently stopped
        // registering, every loop above would pass over an empty set.
        var models = AllBuiltInModels();

        models.Select(static entry => $"{entry.Provider}/{entry.Model.Id}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(models.Count);

        models.Select(static entry => entry.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            .ShouldBeGreaterThan(1);
    }

    // ---- structural checks, factored so they can be pointed at a violating corpus ----

    private static IReadOnlyList<string> FindOrphanSectionIds(
        IEnumerable<PromptVariantDeclaration> declarations,
        IReadOnlySet<string> registeredSectionIds) =>
    [
        .. declarations
            .Where(d => !registeredSectionIds.Contains(d.SectionId))
            .Select(static d => $"{d.Site} declares section id '{d.SectionId}', which no registered prompt section owns")
    ];

    private static IReadOnlyList<string> FindUnparseableFamilyVersions(IEnumerable<PromptVariantDeclaration> declarations)
    {
        var violations = new List<string>();

        foreach (var declaration in declarations.Where(static d => d.Family is not null))
        {
            var family = declaration.Family!;

            // The declared version is round-tripped through the ONE sanctioned parser: a version the
            // registry accepted but that ModelFamilyVersion cannot read back off a real id would be a
            // rung no model could ever reach.
            if (declaration.Version is not { } version)
                continue;

            var synthesisedId = $"{family}-{version.Major}.{version.Minor}";
            if (!ModelFamilyVersion.TryParse(synthesisedId, family, out var parsed) || parsed != version)
                violations.Add($"{declaration.Site} declares version '{version}' for family '{family}', which ModelFamilyVersion does not round-trip");
        }

        return violations;
    }

    private static IReadOnlyList<string> FindSectionsMissingADefaultRung(IReadOnlyList<PromptVariantDeclaration> declarations)
    {
        var defaults = declarations
            .Where(static d => d.IsDefault)
            .Select(static d => d.SectionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. declarations
                .Where(d => !d.IsDefault && !defaults.Contains(d.SectionId))
                .Select(static d => $"section '{d.SectionId}' declares a variant at {d.Site} but no default rung")
                .Distinct(StringComparer.Ordinal)
        ];
    }

    private static IReadOnlyList<string> FindDuplicateTuples(IEnumerable<PromptVariantDeclaration> declarations) =>
    [
        .. declarations
            .GroupBy(static d => $"{d.SectionId}|{d.Family ?? "<default>"}|{d.Version?.ToString() ?? "<none>"}|{d.MatchMajorVersion}", StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => $"tuple '{group.Key}' is declared {group.Count()} times: {string.Join(", ", group.Select(static d => d.Site))}")
    ];

    private static IReadOnlyList<string> FindDanglingOverlayRules(IReadOnlyList<PromptVariantDeclaration> declarations)
    {
        var violations = new List<string>();

        foreach (var declaration in declarations.Where(static d => !d.IsDefault && !d.Replace))
        {
            var inherited = InheritedRuleIds(declarations, declaration);

            foreach (var rule in declaration.Rules.Where(static rule => rule.Text is null))
            {
                if (!inherited.Contains(rule.Id))
                    violations.Add(
                        $"{declaration.Site} removes rule '{rule.Id}', which no rung beneath it declares -- the removal is a silent no-op");
            }
        }

        return violations;
    }

    private static IReadOnlyList<string> FindNoOpRewords(IReadOnlyList<PromptVariantDeclaration> declarations)
    {
        var violations = new List<string>();

        foreach (var declaration in declarations.Where(static d => !d.IsDefault && !d.Replace))
        {
            var inherited = InheritedRules(declarations, declaration);

            foreach (var rule in declaration.Rules.Where(static rule => rule.Text is not null))
            {
                if (inherited.TryGetValue(rule.Id, out var inheritedText) &&
                    string.Equals(inheritedText, rule.Text, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{declaration.Site} restates rule '{rule.Id}' with text identical to the rung beneath it");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The rules visible to <paramref name="declaration"/> from the rungs beneath it: the section
    /// default always, plus the bare family rung when the declaration carries a version, and
    /// the matching major overlay beneath an exact-version rung.
    /// </summary>
    private static Dictionary<string, string> InheritedRules(
        IReadOnlyList<PromptVariantDeclaration> declarations,
        PromptVariantDeclaration declaration)
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Absorb(PromptVariantDeclaration rung)
        {
            foreach (var rule in rung.Rules)
            {
                if (rule.Text is null)
                    inherited.Remove(rule.Id);
                else
                    inherited[rule.Id] = rule.Text;
            }
        }

        var below = declarations.FirstOrDefault(d =>
            d.IsDefault && string.Equals(d.SectionId, declaration.SectionId, StringComparison.OrdinalIgnoreCase));

        if (below is not null)
            Absorb(below);

        if (declaration.Version is not null)
        {
            var familyRung = declarations.FirstOrDefault(d =>
                !d.IsDefault &&
                d.Version is null &&
                string.Equals(d.SectionId, declaration.SectionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Family, declaration.Family, StringComparison.OrdinalIgnoreCase));

            if (familyRung is not null)
            {
                if (familyRung.Replace)
                    inherited.Clear();

                Absorb(familyRung);

                if (!declaration.MatchMajorVersion)
                {
                    var majorRung = declarations.FirstOrDefault(d =>
                        d.MatchMajorVersion &&
                        d.Version?.Major == declaration.Version.Value.Major &&
                        string.Equals(d.SectionId, declaration.SectionId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(d.Family, declaration.Family, StringComparison.OrdinalIgnoreCase));

                    if (majorRung is not null)
                    {
                        if (majorRung.Replace)
                            inherited.Clear();

                        Absorb(majorRung);
                    }
                }
            }
        }

        return inherited;
    }

    private static HashSet<string> InheritedRuleIds(
        IReadOnlyList<PromptVariantDeclaration> declarations,
        PromptVariantDeclaration declaration) =>
        InheritedRules(declarations, declaration).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---- corpus discovery ----

    private static IReadOnlySet<string> DiscoverSectionIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in typeof(PromptVariantRegistry).Assembly.GetTypes())
        {
            // A built-in section is a static class exposing a stable id AND a Create() factory the
            // pipeline can call. Requiring both is what stops an unrelated class with an `Id`
            // constant from widening the allow-list until the orphan check means nothing.
            if (type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static) is null)
                continue;

            var idField = type.GetField("Id", BindingFlags.Public | BindingFlags.Static);
            if (idField is { IsLiteral: true } && idField.FieldType == typeof(string))
                ids.Add((string)idField.GetRawConstantValue()!);
        }

        return ids;
    }

    private static IReadOnlyList<(string Provider, LlmModel Model)> AllBuiltInModels()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry);

        return
        [
            .. registry.GetProviders()
                .SelectMany(provider => registry.GetModels(provider).Select(model => (Provider: provider, Model: model)))
        ];
    }

    // ---- probes: malformed corpora the checks above must reject ----

    internal static class OrphanSectionProbe
    {
        [PromptVariant("section-that-was-renamed-away")]
        internal static IReadOnlyList<PromptRule> Default() => [new("a", "orphaned")];
    }

    internal static class DanglingRemovalProbe
    {
        [PromptVariant("dangling-removal-probe")]
        internal static IReadOnlyList<PromptRule> Default() => [new("declared", "declared text")];

        [PromptVariant("dangling-removal-probe", Family = "gpt")]
        internal static IReadOnlyList<PromptRule> Gpt() => [PromptRule.Remove("rule-that-does-not-exist")];
    }

    internal static class NoOpRewordProbe
    {
        [PromptVariant("no-op-reword-probe")]
        internal static IReadOnlyList<PromptRule> Default() => [new("shared", "identical text")];

        [PromptVariant("no-op-reword-probe", Family = "claude")]
        internal static IReadOnlyList<PromptRule> Claude() => [new("shared", "identical text")];
    }
}
