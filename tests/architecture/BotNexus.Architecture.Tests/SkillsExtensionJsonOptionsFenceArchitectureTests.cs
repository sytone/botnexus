using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Build-failing fence for issue #3495, acceptance criterion 3: every JSON deserialization in the
/// Skills extension must pass an explicit <c>JsonSerializerOptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// The defect was not one missing argument, it was THREE identical hand-rolled
/// <c>JsonSerializer.Deserialize&lt;T&gt;(element.GetRawText())</c> calls, none of which passed
/// options. <c>System.Text.Json</c> is case-sensitive by default, so the documented camelCase key
/// <c>allowSharedSkillManagement</c> never bound and silently defaulted <c>false</c> - the
/// descriptor API reported <c>true</c> while the write gate enforced <c>false</c> across an entire
/// fleet.
/// </para>
/// <para>
/// A behaviour test proves the three CURRENT readers bind correctly. It cannot prove they will
/// keep sharing a seam, and it stays green when a FOURTH reader is added with the bare overload -
/// which is precisely how the defect reached three sites. This fence pins the mechanism instead:
/// inside the Skills extension, an options-less deserialize call is a build failure, and the one
/// case-insensitive options instance lives in <c>SkillsExtensionJson</c>.
/// </para>
/// <para>
/// The scan strips comments first, or the fence fires on the prose in this very repository that
/// legitimately quotes the banned shape (the #2813 / #2955 lesson).
/// </para>
/// </remarks>
public sealed class SkillsExtensionJsonOptionsFenceArchitectureTests : ArchitectureTest
{
    private const string SeamFile =
        "src/extensions/BotNexus.Extensions.Skills/SkillsExtensionJson.cs";

    /// <summary>
    /// No Skills-extension deserialization may use the options-less overload. Adding a fourth
    /// reader without options reopens #3495 for whatever config type it reads.
    /// </summary>
    [Fact]
    public void SkillsExtension_HasNoOptionsLessDeserialization()
    {
        var offenders = SkillsSourceFiles()
            .Where(f => HasOptionsLessDeserialization(StripComments(File.ReadAllText(f))))
            .Select(Rel)
            .Order()
            .ToList();

        offenders.ShouldBeEmpty(
            "Every JSON deserialization in the Skills extension must pass explicit " +
            "JsonSerializerOptions - use SkillsExtensionJson.Options (or a Bind/Resolve helper on " +
            "that type). System.Text.Json is case-sensitive by default, so an options-less call " +
            "silently drops every camelCase key an operator wrote and falls back to the property " +
            "default. That is #3495: three call sites, one silently-false gate, a fleet of agents " +
            "whose correct config was ignored.\nSites: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The options instance is a SINGLE shared static. Three separate case-insensitive literals
    /// would satisfy the test above while leaving the extension exactly as easy to regress.
    /// </summary>
    [Fact]
    public void SkillsExtension_DeclaresExactlyOneJsonSerializerOptionsInstance()
    {
        var declarations = SkillsSourceFiles()
            .Where(f => JsonOptionsDeclarationProbe().IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Rel)
            .Order()
            .ToList();

        declarations.ShouldBe(
            [SeamFile],
            "SkillsExtensionJson.Options must be the only JsonSerializerOptions instance the " +
            "Skills extension constructs. A second instance is a second binding policy, and the " +
            "whole point of #3495 is that duplicated policy drifts silently.\nDeclarations: "
            + string.Join(", ", declarations));
    }

    /// <summary>
    /// Anti-vacuity. A fence whose file scan resolves to an empty set, or whose detectors match
    /// nothing, silently guards nothing.
    /// </summary>
    [Fact]
    public void SkillsJsonFenceDetectors_AreNotVacuous()
    {
        SkillsSourceFiles().Count.ShouldBeGreaterThan(
            10,
            "The scan should read the whole Skills extension; a near-empty file set means path " +
            "resolution broke and the fence stopped guarding anything.");

        HasOptionsLessDeserialization("JsonSerializer.Deserialize<SkillsConfig>(element.GetRawText());")
            .ShouldBeTrue("Detector must match the exact shape #3495 was filed against.");
        HasOptionsLessDeserialization(
                "await JsonSerializer.DeserializeAsync<SkillsWriteRequest>(request.Body, cancellationToken: ct);")
            .ShouldBeTrue(
                "Detector must also match the async request-body shape: a cancellationToken is " +
                "not options, and that call was binding case-sensitively too.");
        HasOptionsLessDeserialization("JsonSerializer.Deserialize<TrustCatalog>(json, JsonOptions);")
            .ShouldBeFalse("Detector must not fire on a call that DOES pass options positionally.");
        HasOptionsLessDeserialization(
                "JsonSerializer.DeserializeAsync<T>(body, options: SkillsExtensionJson.Options, cancellationToken: ct)")
            .ShouldBeFalse("Detector must not fire when options are passed by name.");
        HasOptionsLessDeserialization("SkillsExtensionJson.Bind<SkillsConfig>(element)")
            .ShouldBeFalse("Detector must not fire on the shared seam's own helper calls.");

        JsonOptionsDeclarationProbe()
            .IsMatch("private static readonly JsonSerializerOptions JsonOptions = new()")
            .ShouldBeTrue("Declaration detector must match a constructed options instance.");
        JsonOptionsDeclarationProbe()
            .IsMatch("public static T? Bind<T>(JsonElement element)")
            .ShouldBeFalse("Declaration detector must not fire on merely mentioning the type.");

        var seam = StripComments(File.ReadAllText(Path.Combine(Repository.Root, SeamFile)));
        JsonOptionsDeclarationProbe().IsMatch(seam).ShouldBeTrue(
            "SkillsExtensionJson must itself declare the shared options instance, or the " +
            "single-declaration assertion above is satisfied by a scan that matches nothing.");
        seam.ShouldContain(
            "PropertyNameCaseInsensitive = true",
            Case.Sensitive,
            "The shared options instance must be the case-insensitive one.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="source"/> contains a <c>JsonSerializer.Deserialize[Async]</c>
    /// call that passes no options. An options argument counts if it is supplied positionally as
    /// the second argument, or by the <c>options:</c> name. A <c>cancellationToken:</c> second
    /// argument explicitly does NOT count - that was the async endpoint shape which bound
    /// case-sensitively while looking, at a glance, like it passed something.
    /// </summary>
    private static bool HasOptionsLessDeserialization(string source) =>
        DeserializeCallProbe().Matches(source)
            .Any(m => !PassesOptions(m.Groups["args"].Value));

    private static bool PassesOptions(string args)
    {
        if (args.Contains("options:", StringComparison.Ordinal))
            return true;

        var arguments = SplitTopLevelArguments(args);
        return arguments.Count >= 2
               && !arguments[1].StartsWith("cancellationToken:", StringComparison.Ordinal);
    }

    /// <summary>Splits an argument list on commas that are not nested inside brackets.</summary>
    private static IReadOnlyList<string> SplitTopLevelArguments(string args)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c is '(' or '[' or '<')
                depth++;
            else if (c is ')' or ']' or '>')
                depth--;
            else if (c == ',' && depth == 0)
            {
                arguments.Add(args[start..i].Trim());
                start = i + 1;
            }
        }

        var tail = args[start..].Trim();
        if (tail.Length > 0)
            arguments.Add(tail);

        return arguments;
    }

    private static Regex DeserializeCallProbe() => s_deserializeCallProbe;

    private static readonly Regex s_deserializeCallProbe = new(
        @"JsonSerializer\s*\.\s*Deserialize(?:Async)?\s*<[^>]*>\s*\(" +
        @"(?<args>(?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)",
        RegexOptions.Compiled);

    private static Regex JsonOptionsDeclarationProbe() => s_jsonOptionsDeclarationProbe;

    private static readonly Regex s_jsonOptionsDeclarationProbe = new(
        @"\bJsonSerializerOptions\b[^=;()]*=\s*new\b", RegexOptions.Compiled);

    private IReadOnlyList<string> SkillsSourceFiles()
    {
        var extension = Path.Combine(
            Repository.Root, "src", "extensions", "BotNexus.Extensions.Skills");

        return Directory
            .EnumerateFiles(extension, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private string Rel(string file) =>
        Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }
}
