namespace BotNexus.Gateway.Prompts.Tests;

[Collection(ReflectionScanCollection.Name)]
public sealed class PromptVariantMajorVersionTests
{
    private const string Section = "major-probe";

    [Theory]
    [InlineData("gpt-6")]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6.1-astra")]
    [InlineData("gpt-6-2-codex")]
    [InlineData("COPILOT/GPT-6.2-ASTRA")]
    [InlineData("openai/gpt-6.2-astra:latest")]
    public void Resolve_OptedInMajor_MatchesEverySupportedSixId(string modelId)
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(MajorProbe)]);

        registry.Resolve(Section, " GPT ", modelId)
            .ShouldBe(["major alpha", "default beta", "family extra", "major extra"]);
        registry.ResolveDeclarations(Section, "gpt", modelId)
            .Select(d => d.MatchMajorVersion).ShouldBe([false, false, true]);
    }

    [Theory]
    [InlineData("gpt-5", "gpt")]
    [InlineData("gpt-5.6-sol", "gpt")]
    [InlineData("gpt-7", "gpt")]
    [InlineData("gpt-60", "gpt")]
    [InlineData("gpt", "gpt")]
    [InlineData("unknown-model", "unknown")]
    [InlineData("claude-6", "claude")]
    [InlineData("gpt-6", null)]
    public void Resolve_NonMatchingModel_NeverGetsMajorOverlay(string modelId, string? family)
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(MajorProbe)]);

        registry.ResolveDeclarations(Section, family, modelId).ShouldNotContain(d => d.MatchMajorVersion);
        registry.Resolve(Section, family, modelId).ShouldNotContain("major alpha");
    }

    [Theory]
    [InlineData("gpt-6", "exact zero alpha")]
    [InlineData("gpt-6.0-astra", "exact zero alpha")]
    [InlineData("gpt-6.1-astra", "exact one alpha")]
    public void Resolve_ExactAndMajorCoexist_ExactWinsAndRemovalKeepsPositions(string modelId, string expected)
    {
        // Supply types in reverse specificity order: discovery order must not drive resolution.
        var registry = PromptVariantRegistry.FreezeTypes([typeof(ExactProbe), typeof(MajorProbe), typeof(BaseProbe)]);

        registry.Resolve(Section, "gpt", modelId)
            .ShouldBe([expected, "default beta", "family extra", "exact extra"]);
        var rungs = registry.ResolveDeclarations(Section, "gpt", modelId);
        rungs.Select(d => d.Site.Split('.').Last()).ShouldBe(["Default", "Family", "Major", modelId.Contains("6.1", StringComparison.Ordinal) ? "One" : "Zero"]);
        rungs[2].MatchMajorVersion.ShouldBeTrue();
        rungs[3].MatchMajorVersion.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_ExactWithoutOptIn_RemainsExactIncludingLegacyFive()
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(ExactProbe)]);

        registry.Resolve(Section, "gpt", "gpt-5").ShouldContain("exact five");
        registry.Resolve(Section, "gpt", "gpt-5.1").ShouldNotContain("exact five");
        registry.Resolve(Section, "gpt", "gpt-6.2").ShouldBe(["family alpha", "default beta", "family extra"]);
    }

    [Fact]
    public void Resolve_ReplaceAtMajor_DiscardsEarlierRulesAndStillAllowsExactOverlay()
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(ReplaceMajorProbe), typeof(ExactProbe)]);

        registry.Resolve(Section, "gpt", "gpt-6.2").ShouldBe(["replacement"]);
        registry.Resolve(Section, "gpt", "gpt-6.1").ShouldBe(["exact one alpha", "exact extra"]);
    }

    [Fact]
    public void Resolve_ReplaceAtExact_DiscardsMajorAndEarlierRules()
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(MajorProbe), typeof(ReplaceExactProbe)]);

        registry.Resolve(Section, "gpt", "gpt-6.1").ShouldBe(["exact replacement"]);
    }

    [Fact]
    public void Resolve_MajorAndExactWithoutFamilyRung_RemainUnreachable()
    {
        var registry = PromptVariantRegistry.FreezeTypes([typeof(DefaultOnlyProbe), typeof(MajorProbe), typeof(ExactProbe)]);

        registry.Resolve(Section, "gpt", "gpt-6.1").ShouldBe(["default only"]);
        registry.ResolveDeclarations(Section, "gpt", "gpt-6.1").ShouldHaveSingleItem().IsDefault.ShouldBeTrue();
        registry.ResolveDeclarations("absent", "gpt", "gpt-6.1").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(typeof(MissingFamilyProbe))]
    [InlineData(typeof(MissingVersionProbe))]
    [InlineData(typeof(MissingBothProbe))]
    [InlineData(typeof(MinorSpellingProbe))]
    [InlineData(typeof(ZeroMinorSpellingProbe))]
    [InlineData(typeof(SuffixSpellingProbe))]
    [InlineData(typeof(UnparseableMajorProbe))]
    public void Freeze_MalformedMajorDeclaration_RejectsAtDeclarationSite(Type probe)
    {
        var error = Should.Throw<InvalidOperationException>(() => PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), probe]));
        error.Message.ShouldContain(probe.Name);
    }

    [Fact]
    public void Freeze_DuplicateMajorBucket_RejectsButExactZeroIsDistinct()
    {
        Should.Throw<InvalidOperationException>(() => PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(MajorProbe), typeof(ReplaceMajorProbe)]))
            .Message.ShouldContain("Duplicate");
        PromptVariantRegistry.FreezeTypes([typeof(BaseProbe), typeof(MajorProbe), typeof(ExactProbe)])
            .Declarations.Count(d => d.Version?.Major == 6 && d.Version?.Minor == 0).ShouldBe(2);
    }

    internal static class BaseProbe
    {
        [PromptVariant(Section)]
        internal static IReadOnlyList<PromptRule> Default() => [new("alpha", "default alpha"), new("beta", "default beta")];
        [PromptVariant(Section, Family = "gpt")]
        internal static IReadOnlyList<PromptRule> Family() => [new("alpha", "family alpha"), new("family", "family extra")];
    }

    internal static class MajorProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Major() => [new("alpha", "major alpha"), new("major", "major extra")];
    }

    internal static class ExactProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6")]
        internal static IReadOnlyList<PromptRule> Zero() => [new("ALPHA", "exact zero alpha"), PromptRule.Remove("major"), new("exact", "exact extra")];
        [PromptVariant(Section, Family = "gpt", Version = "6-1")]
        internal static IReadOnlyList<PromptRule> One() => [new("alpha", "exact one alpha"), PromptRule.Remove("major"), new("exact", "exact extra")];
        [PromptVariant(Section, Family = "gpt", Version = "5")]
        internal static IReadOnlyList<PromptRule> Five() => [new("alpha", "exact five")];
    }

    internal static class ReplaceMajorProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6", MatchMajorVersion = true, Replace = true)]
        internal static IReadOnlyList<PromptRule> Major() => [new("alpha", "replacement")];
    }

    internal static class ReplaceExactProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6-1", Replace = true)]
        internal static IReadOnlyList<PromptRule> Exact() => [new("only", "exact replacement")];
    }

    internal static class DefaultOnlyProbe
    {
        [PromptVariant(Section)]
        internal static IReadOnlyList<PromptRule> Default() => [new("only", "default only")];
    }

    internal static class MissingFamilyProbe
    {
        [PromptVariant(Section, Version = "6", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
    internal static class MissingVersionProbe
    {
        [PromptVariant(Section, Family = "gpt", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
    internal static class MissingBothProbe
    {
        [PromptVariant(Section, MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
    internal static class MinorSpellingProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6-1", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
    internal static class ZeroMinorSpellingProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6-0", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
    internal static class SuffixSpellingProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "6-astra", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
    internal static class UnparseableMajorProbe
    {
        [PromptVariant(Section, Family = "gpt", Version = "600", MatchMajorVersion = true)]
        internal static IReadOnlyList<PromptRule> Bad() => [];
    }
}
