namespace BotNexus.Architecture.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

/// <summary>
/// Fitness function for the generated feature-flag inventory (#2769).
/// <para>
/// The inventory only removes the duplication that motivated it if <c>feature-flags.json</c> stays
/// the ONLY place a flag name is written down. The prior state is the argument: the single declared
/// flag was a <c>const string</c> in two files with nothing binding them, so a rename in one was
/// silently unobserved by the other. Nothing in the compiler prevents someone re-introducing a
/// third spelling tomorrow - a literal is always legal C#. This test is what prevents it, and a
/// comment asking reviewers to watch for it would not.
/// </para>
/// </summary>
public sealed class FeatureFlagInventoryArchitectureTests : ArchitectureTest
{

    private string FlagsFile => Path.Combine(Repository.Root, "feature-flags.json");

    /// <summary>
    /// Files permitted to contain a flag name as a literal, each with the reason. The generator
    /// consumes the JSON, and the doctor tests assert against real flag names by necessity.
    /// </summary>
    private static readonly Dictionary<string, string> LiteralExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["feature-flags.json"] = "The single declaration itself.",
    };

    [Fact]
    public void FeatureFlagsJson_ExistsAndDeclaresGatewayDevOriginEnforcement()
    {
        File.Exists(FlagsFile).ShouldBeTrue($"feature-flags.json not found at {FlagsFile}");

        var flags = ReadDeclaredFlagNames();

        flags.ShouldContain("GatewayDevOriginEnforcement");
    }

    [Fact]
    public void EveryDeclaredFlag_CarriesAnOwnerADescriptionAndADateAdded()
    {
        // The generator enforces this at build time (BNFF001), so this test is the readable
        // statement of the same rule: a flag with no owner is one nobody will ever retire.
        using var document = JsonDocument.Parse(File.ReadAllText(FlagsFile));

        var missing = new List<string>();

        foreach (var flag in document.RootElement.GetProperty("flags").EnumerateArray())
        {
            var name = flag.TryGetProperty("featureName", out var nameProperty)
                ? nameProperty.GetString()
                : "(unnamed)";

            foreach (var required in new[] { "featureName", "description", "owner", "dateAdded" })
            {
                if (!flag.TryGetProperty(required, out var value) ||
                    string.IsNullOrWhiteSpace(value.GetString()))
                {
                    missing.Add($"  flag '{name}' is missing '{required}'.");
                }
            }

            if (!flag.TryGetProperty("defaultState", out var defaultState) ||
                (defaultState.ValueKind != JsonValueKind.True && defaultState.ValueKind != JsonValueKind.False))
            {
                missing.Add($"  flag '{name}' is missing a boolean 'defaultState'.");
            }
        }

        missing.ShouldBeEmpty("feature-flags.json entries are incomplete:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoSourceFile_RespellsADeclaredFlagNameAsAStringLiteral()
    {
        // AC3. A call site must reference the generated member, so the name has exactly one
        // spelling and the compiler enforces agreement. A literal re-introduces the very drift
        // this issue exists to close - and a misspelled literal evaluates as absent, silently.
        var declared = ReadDeclaredFlagNames();
        declared.ShouldNotBeEmpty("no flags declared - this fence would be vacuous");

        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles(Path.Combine(Repository.Root, "src")))
        {
            var relative = ToRepoRelative(file);
            if (LiteralExemptions.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var flag in declared)
            {
                // Only a QUOTED occurrence is a respelling. A bare identifier is the generated
                // member being referenced, which is exactly what should happen.
                var match = Regex.Match(text, "\"" + Regex.Escape(flag) + "\"");
                if (match.Success)
                {
                    var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                    violations.Add(
                        $"  {relative}:{line} spells flag '{flag}' as a string literal.\n"
                        + $"    Fix: reference FeatureFlags.{flag} instead. The name is declared once, in "
                        + "feature-flags.json, and generated into a compile-time symbol (#2769).");
                }
            }
        }

        violations.ShouldBeEmpty(
            "feature flag names must not be re-spelled as literals in src (#2769 AC3):\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void NoSourceFile_DeclaresItsOwnFeatureFlagsInventoryType()
    {
        // The hand-written FeatureFlags class this generator replaced lived in
        // src/gateway/BotNexus.Gateway.Configuration. If it (or a rival inventory) comes back, the
        // generated type and the hand-written one silently disagree - two sources of truth is the
        // exact defect, whether the duplicate is a constant or a whole class.
        var violations = EnumerateSourceFiles(Path.Combine(Repository.Root, "src"))
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file),
                @"\b(class|record|enum)\s+FeatureFlags\b"))
            .Select(ToRepoRelative)
            .ToList();

        violations.ShouldBeEmpty(
            "FeatureFlags is generated from feature-flags.json; a hand-written declaration is a "
            + "second, disagreeing source of truth (#2769):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void TheGeneratorProject_IsNotPartOfTheDeploymentClosure()
    {
        // It runs inside the compiler and ships nothing. Living under src/ would add it to every
        // `botnexus build` (src/dirs.proj is the deployment closure) for no deployed benefit.
        Directory.Exists(Path.Combine(Repository.Root, "tools", "BotNexus.SourceGenerators"))
            .ShouldBeTrue("the generator must live under tools/, not src/");

        Directory.Exists(Path.Combine(Repository.Root, "src", "BotNexus.SourceGenerators"))
            .ShouldBeFalse("the generator must not be in the src deployment closure");
    }

    private IReadOnlyList<string> ReadDeclaredFlagNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FlagsFile));

        return document.RootElement.GetProperty("flags")
            .EnumerateArray()
            .Select(flag => flag.GetProperty("featureName").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private string ToRepoRelative(string absolutePath)
        => absolutePath.Substring(Repository.Root.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');

}
