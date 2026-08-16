namespace BotNexus.SourceGenerators.Tests;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Shouldly;

/// <summary>
/// Drives the generator over a real in-memory compilation, which is the only way to assert the
/// claims #2769 actually makes: that a typo at a call site FAILS TO COMPILE, that a malformed file
/// produces a build ERROR rather than silence, and that a retired flag WARNS at its call site.
/// <para>
/// Asserting on the generated string alone would prove none of those - it would prove only that
/// the generator emitted text a human believes the compiler will reject. These tests compile it.
/// </para>
/// </summary>
public class FeatureFlagSourceGeneratorTests
{
    private const string OneFlag = """
        {
          "flags": [
            {
              "featureName": "GatewayDevOriginEnforcement",
              "description": "Dev-mode browser Origin guard.",
              "owner": "sytone",
              "dateAdded": "2026-07-01",
              "defaultState": false,
              "ignoreFlagAge": true
            }
          ]
        }
        """;

    // ── AC2: the inventory and its lookups exist as compile-time symbols ───────────────────

    [Fact]
    public void Generator_EmitsAMemberPerFlag_PlusGetAllAndFromName()
    {
        var result = RunGenerator(OneFlag, """
            using BotNexus.Gateway.Configuration;
            public static class CallSite
            {
                public static string Name = FeatureFlags.GatewayDevOriginEnforcement;
                public static int Count = FeatureFlags.GetAll().Count;
                public static bool Declared = FeatureFlags.FromName("GatewayDevOriginEnforcement") is not null;
            }
            """);

        result.Errors.ShouldBeEmpty(result.Report);
        result.RequireGeneratedSource().ShouldContain("public const string GatewayDevOriginEnforcement");
    }

    // ── AC4: a misspelled flag is a COMPILE ERROR, not a silent false ──────────────────────

    [Fact]
    public void CallSiteReferencingAnUndeclaredFlag_FailsToCompile()
    {
        // The single most valuable property of the whole design. Before the inventory existed a
        // misspelling evaluated as absent and returned the default, so the feature stayed off
        // while the code read as though it were on.
        var result = RunGenerator(OneFlag, """
            using BotNexus.Gateway.Configuration;
            public static class CallSite
            {
                public static string Name = FeatureFlags.GatewayDevOriginEnforcment; // deliberate typo
            }
            """);

        result.Errors.ShouldNotBeEmpty("a misspelled flag must not compile");
        result.Errors.ShouldContain(
            diagnostic => diagnostic.Id == "CS0117" || diagnostic.Id == "CS0103",
            result.Report);
    }

    [Fact]
    public void RemovingAFlagFromTheJson_BreaksItsCallSiteByName()
    {
        // AC9 non-vacuity, asserted rather than asserted-about: the SAME call site that compiles
        // above must fail once the flag is no longer declared. If this passed with the flag
        // removed, the inventory would not be load-bearing at all.
        var result = RunGenerator("{ \"flags\": [] }", """
            using BotNexus.Gateway.Configuration;
            public static class CallSite
            {
                public static string Name = FeatureFlags.GatewayDevOriginEnforcement;
            }
            """);

        result.Errors.ShouldNotBeEmpty("removing a flag must break its call sites");
        result.Report.ShouldContain("GatewayDevOriginEnforcement");
    }

    // ── AC5: a malformed file is a build ERROR with a diagnostic ID ────────────────────────

    [Fact]
    public void MalformedJson_ReportsBnff001_AndGeneratesNothing()
    {
        var result = RunGenerator("{ \"flags\": [ {", string.Empty);

        var diagnostic = result.GeneratorDiagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe(FeatureFlagSourceGenerator.ParseErrorId);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        result.GeneratedSource.ShouldBeNull("no inventory may be emitted from an unparseable file");
    }

    [Fact]
    public void DuplicateFlag_ReportsBnff001_NamingTheFlag()
    {
        var json = """
            {
              "flags": [
                { "featureName": "Dup", "description": "a", "owner": "sytone", "dateAdded": "2026-01-01", "defaultState": false },
                { "featureName": "Dup", "description": "b", "owner": "sytone", "dateAdded": "2026-01-01", "defaultState": false }
              ]
            }
            """;

        var diagnostic = RunGenerator(json, string.Empty).GeneratorDiagnostics.ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe(FeatureFlagSourceGenerator.ParseErrorId);
        diagnostic.GetMessage().ShouldContain("Dup");
    }

    [Fact]
    public void MissingOwner_ReportsBnff001_RatherThanEmittingAnOwnerlessFlag()
    {
        var json = """
            { "flags": [ { "featureName": "NoOwner", "description": "a", "dateAdded": "2026-01-01", "defaultState": false } ] }
            """;

        var result = RunGenerator(json, string.Empty);

        result.GeneratorDiagnostics.ShouldHaveSingleItem().Id.ShouldBe(FeatureFlagSourceGenerator.ParseErrorId);
        result.GeneratedSource.ShouldBeNull();
    }

    // ── AC6: a retired flag emits [Obsolete] and WARNS at the call site ────────────────────

    [Fact]
    public void RetiredFlag_ProducesAnObsoleteWarningAtTheCallSite()
    {
        var json = """
            {
              "flags": [
                {
                  "featureName": "RetiredFlag",
                  "description": "Gone.",
                  "owner": "sytone",
                  "dateAdded": "2025-01-01",
                  "defaultState": false,
                  "dateRetired": "2026-02-03"
                }
              ]
            }
            """;

        var result = RunGenerator(json, """
            using BotNexus.Gateway.Configuration;
            public static class CallSite
            {
                public static string Name = FeatureFlags.RetiredFlag;
            }
            """);

        result.Errors.ShouldBeEmpty(result.Report);
        result.Warnings.ShouldContain(
            diagnostic => diagnostic.Id == "CS0618" && diagnostic.GetMessage().Contains("2026-02-03"),
            result.Report);
    }

    [Fact]
    public void RetiredFlag_IsStillEnumerableAndResolvable()
    {
        // A retired flag must stay in the inventory: config files in the wild still carry the key,
        // and doctor has to recognise it as declared-but-obsolete rather than report it as an
        // unknown typo. If the generator skipped retired flags, this reddens by name (AC9).
        var json = """
            {
              "flags": [
                { "featureName": "RetiredFlag", "description": "Gone.", "owner": "sytone", "dateAdded": "2025-01-01", "defaultState": false, "dateRetired": "2026-02-03" }
              ]
            }
            """;

        var result = RunGenerator(json, string.Empty);

        result.Errors.ShouldBeEmpty(result.Report);
        result.RequireGeneratedSource().ShouldContain("[Obsolete(");
        result.RequireGeneratedSource().ShouldContain("Name: RetiredFlag");
    }

    [Fact]
    public void LiveFlag_CarriesNoObsoleteAttribute()
    {
        RunGenerator(OneFlag, string.Empty).RequireGeneratedSource().ShouldNotContain("[Obsolete(");
    }

    // ── AC7: staleness warning names the flag and its owner ───────────────────────────────

    [Fact]
    public void FlagPastTheAgeThreshold_ProducesAStalenessWarningNamingFlagAndOwner()
    {
        var flags = FeatureFlagJsonParser.ParseJson("""
            { "flags": [ { "featureName": "OldOne", "description": "a", "owner": "ada", "dateAdded": "2026-01-01", "defaultState": false } ] }
            """);

        var diagnostics = FeatureFlagSourceGenerator.BuildStaleDiagnostics(
            flags,
            new GeneratorOptions { AgeWarningDays = 90 },
            new DateTime(2026, 12, 1));

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe(FeatureFlagSourceGenerator.StaleFlagId);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().ShouldContain("OldOne");
        diagnostic.GetMessage().ShouldContain("ada");
    }

    [Fact]
    public void FlagWithinTheAgeThreshold_ProducesNoStalenessWarning()
    {
        var flags = FeatureFlagJsonParser.ParseJson("""
            { "flags": [ { "featureName": "NewOne", "description": "a", "owner": "ada", "dateAdded": "2026-01-01", "defaultState": false } ] }
            """);

        FeatureFlagSourceGenerator.BuildStaleDiagnostics(
                flags,
                new GeneratorOptions { AgeWarningDays = 90 },
                new DateTime(2026, 2, 1))
            .ShouldBeEmpty();
    }

    [Fact]
    public void IgnoreFlagAge_SuppressesTheStalenessWarning()
    {
        var flags = FeatureFlagJsonParser.ParseJson("""
            { "flags": [ { "featureName": "Enduring", "description": "a", "owner": "ada", "dateAdded": "2020-01-01", "defaultState": false, "ignoreFlagAge": true } ] }
            """);

        FeatureFlagSourceGenerator.BuildStaleDiagnostics(
                flags,
                new GeneratorOptions { AgeWarningDays = 90 },
                new DateTime(2026, 12, 1))
            .ShouldBeEmpty();
    }

    [Fact]
    public void RetiredFlag_EarnsNoStalenessWarning()
    {
        // It already carries [Obsolete]; warning about its age as well is a second warning for a
        // decision that has already been made.
        var flags = FeatureFlagJsonParser.ParseJson("""
            { "flags": [ { "featureName": "Old", "description": "a", "owner": "ada", "dateAdded": "2020-01-01", "defaultState": false, "dateRetired": "2026-01-01" } ] }
            """);

        FeatureFlagSourceGenerator.BuildStaleDiagnostics(
                flags,
                new GeneratorOptions { AgeWarningDays = 90 },
                new DateTime(2026, 12, 1))
            .ShouldBeEmpty();
    }

    [Fact]
    public void ZeroAgeThreshold_DisablesTheStalenessWarningEntirely()
    {
        var flags = FeatureFlagJsonParser.ParseJson("""
            { "flags": [ { "featureName": "Ancient", "description": "a", "owner": "ada", "dateAdded": "2000-01-01", "defaultState": false } ] }
            """);

        FeatureFlagSourceGenerator.BuildStaleDiagnostics(
                flags,
                new GeneratorOptions { AgeWarningDays = 0 },
                new DateTime(2026, 12, 1))
            .ShouldBeEmpty();
    }

    // ── Determinism: generated output may not depend on the ambient clock ──────────────────

    [Fact]
    public void GeneratedSource_IsIdenticalAcrossRuns()
    {
        // Roslyn caches generator output; content that varied with the clock would invalidate that
        // cache daily and make two builds of the same commit produce different source.
        RunGenerator(OneFlag, string.Empty).RequireGeneratedSource()
            .ShouldBe(RunGenerator(OneFlag, string.Empty).RequireGeneratedSource());
    }

    private static GeneratorRun RunGenerator(string flagsJson, string callSiteSource)
    {
        var syntaxTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
        if (!string.IsNullOrWhiteSpace(callSiteSource))
        {
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                callSiteSource,
                new CSharpParseOptions(LanguageVersion.Latest)));
        }

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            "FeatureFlagGeneratorTestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            generators: [new FeatureFlagSourceGenerator().AsSourceGenerator()],
            additionalTexts: [new InMemoryAdditionalText("feature-flags.json", flagsJson)]);

        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        var generated = outputCompilation.SyntaxTrees
            .FirstOrDefault(tree => tree.FilePath.EndsWith("FeatureFlags.g.cs", StringComparison.Ordinal));

        var compilationDiagnostics = outputCompilation.GetDiagnostics();

        return new GeneratorRun(
            generated?.ToString(),
            generatorDiagnostics,
            compilationDiagnostics);
    }

    private sealed record GeneratorRun(
        string? GeneratedSource,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompilationDiagnostics)
    {
        public IReadOnlyList<Diagnostic> Errors =>
            CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        public IReadOnlyList<Diagnostic> Warnings =>
            CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

        /// <summary>
        /// The generated source, asserting it exists. Used by every test that expects generation to
        /// have succeeded, so "the generator emitted nothing" fails as a clear assertion rather
        /// than as a null-reference further down.
        /// </summary>
        public string RequireGeneratedSource()
        {
            GeneratedSource.ShouldNotBeNull("the generator emitted no inventory. " + Report);
            return GeneratedSource!;
        }

        /// <summary>Every diagnostic, rendered for assertion failure messages.</summary>
        public string Report => string.Join(
            Environment.NewLine,
            GeneratorDiagnostics.Concat(CompilationDiagnostics).Select(d => $"{d.Severity} {d.Id}: {d.GetMessage()}"));
    }

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default)
            => SourceText.From(content, Encoding.UTF8);
    }
}
