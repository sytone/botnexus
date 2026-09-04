using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fence for #2682: the platform has exactly ONE content-hash trust implementation, and the three
/// trust vocabularies that describe it stay identical.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour tests prove that a tampered plugin is refused under <c>Enforce</c> today. They
/// cannot prove that a FUTURE consumer reuses the same hasher, and a second hasher is not a
/// hypothetical: #2682 was filed precisely because plugins were about to grow one. Two
/// implementations drift, and the way they drift is that the set the platform enforces stops being
/// the set it reports - which is invisible to every behaviour test until someone relies on it.
/// </para>
/// <para>
/// Rule 1 - <c>SHA256</c> may only be invoked for trust cataloguing from the single shared
/// implementation.
/// </para>
/// <para>
/// Rule 2 - the catalog file name is declared once. A second <c>"trust.json"</c> literal is a
/// second opinion about where the catalog lives.
/// </para>
/// <para>
/// Rule 3 - <c>ContentTrustMode</c>, <c>SkillTrustMode</c> and <c>PluginTrustMode</c> declare the
/// same members in the same order. (<c>PluginMcpRegistrationFenceArchitectureTests</c> pins the
/// latter two against each other; this adds the shared implementation's own vocabulary, which is
/// the one the other two now forward to.)
/// </para>
/// </remarks>
public sealed class ContentTrustCatalogFenceArchitectureTests : ArchitectureTest
{
    /// <summary>The single permitted trust-hash implementation.</summary>
    private const string SharedImplementation =
        "src/extensions/BotNexus.Extensions.Plugins/Security/ContentTrustCatalog.cs";

    /// <summary>
    /// Files permitted to compute a SHA-256 hash <i>in a trust-catalogue context</i>. Each entry is
    /// a deliberate decision with a written reason, not an unreviewed exemption.
    /// </summary>
    private static readonly Dictionary<string, string> s_allowedSha256Uses = new(StringComparer.OrdinalIgnoreCase)
    {
        [SharedImplementation] = "The single content-trust hasher itself (#2682).",
    };

    /// <summary>
    /// The scan is deliberately scoped to files that hash AND speak the trust-catalogue vocabulary.
    /// A blanket "no SHA-256 outside one file" rule would be wrong: the platform legitimately hashes
    /// for around twenty unrelated purposes (descriptor fingerprints, actor pseudonyms, download
    /// verification, cache keys), and an allow-list of twenty irrelevant files is an allow-list
    /// nobody reads. What must not exist twice is a hasher that decides whether CONTENT IS TRUSTED.
    /// </summary>
    [Fact]
    public void ContentTrustHashing_HasExactlyOneImplementation()
    {
        var offenders = SourceFiles()
            .Select(f => (File: f, Code: StripComments(File.ReadAllText(f))))
            .Where(x => s_sha256Probe.IsMatch(x.Code) && s_trustVocabularyProbe.IsMatch(x.Code))
            .Select(x => Rel(x.File))
            .Where(rel => !s_allowedSha256Uses.ContainsKey(rel))
            .Order()
            .ToList();

        offenders.ShouldBeEmpty(
            "#2682: content-hash trust must have ONE implementation. A second SHA-256 catalog " +
            "hasher is how the set the platform ENFORCES stops matching the set it REPORTS - the " +
            "exact drift this issue was filed to prevent. Reuse ContentTrustCatalog, or add an " +
            "entry to s_allowedSha256Uses WITH a written reason.\nOffenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void CatalogFileName_IsDeclaredOnce()
    {
        var declaring = SourceFiles()
            .Where(f => s_catalogLiteralProbe.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Rel)
            .Order()
            .ToList();

        declaring.ShouldBe(
            [SharedImplementation],
            "#2682: the trust catalog file name must be declared once, on ContentTrustCatalog. A " +
            "second \"trust.json\" literal is a second opinion about where a catalog lives, and the " +
            "consumer holding the stale one verifies a file nothing writes.\nDeclarations: " +
            string.Join(", ", declaring));
    }

    [Fact]
    public void TrustVocabularies_AreIdentical()
    {
        var content = EnumMembers(SharedImplementation, "ContentTrustMode");
        var skills = EnumMembers(
            "src/extensions/BotNexus.Extensions.Skills/Security/SkillTrustVerifier.cs",
            "SkillTrustMode");
        var plugins = EnumMembers(
            "src/extensions/BotNexus.Extensions.Mcp/Plugins/PluginTrust.cs",
            "PluginTrustMode");

        content.ShouldNotBeEmpty("the fence is vacuous if the shared enum could not be parsed");

        skills.ShouldBe(content, ignoreOrder: false,
            $"SkillTrustMode must match ContentTrustMode. Skills: [{string.Join(", ", skills)}] " +
            $"Shared: [{string.Join(", ", content)}]");

        plugins.ShouldBe(content, ignoreOrder: false,
            $"PluginTrustMode must match ContentTrustMode. Plugins: [{string.Join(", ", plugins)}] " +
            $"Shared: [{string.Join(", ", content)}]");
    }

    /// <summary>
    /// Anti-vacuity. A fence whose scan resolves to an empty set, or whose detectors match nothing,
    /// silently guards nothing - the #2910 lesson.
    /// </summary>
    [Fact]
    public void Detectors_AreNotVacuous()
    {
        SourceFiles().Count.ShouldBeGreaterThan(
            200,
            "the scan should read the whole src tree; a near-empty file set means path resolution " +
            "broke and the fence stopped guarding anything");

        s_sha256Probe.IsMatch("var hash = SHA256.HashData(bytes);")
            .ShouldBeTrue("detector must match the shape a second hasher would take");
        s_sha256Probe.IsMatch("using System.Security.Cryptography;")
            .ShouldBeFalse("detector must not fire on the namespace import alone");

        s_trustVocabularyProbe.IsMatch("var catalog = new TrustCatalog();")
            .ShouldBeTrue("vocabulary detector must match trust-catalogue code");
        s_trustVocabularyProbe.IsMatch("var fingerprint = SHA256.HashData(descriptorBytes);")
            .ShouldBeFalse("vocabulary detector must not fire on an unrelated hash");

        // The scoped scan must actually SELECT the shared implementation, or the two detectors
        // pair to match nothing and the allow-list above guards an empty set.
        SourceFiles()
            .Select(f => (Rel: Rel(f), Code: StripComments(File.ReadAllText(f))))
            .Where(x => s_sha256Probe.IsMatch(x.Code) && s_trustVocabularyProbe.IsMatch(x.Code))
            .Select(x => x.Rel)
            .ShouldContain(
                SharedImplementation,
                "the scoped scan must select the shared implementation, or the fence guards nothing");

        s_catalogLiteralProbe.IsMatch("""const string CatalogFileName = "trust.json";""")
            .ShouldBeTrue("detector must match a catalog file-name declaration");
        s_catalogLiteralProbe.IsMatch("// the catalog lives at trust.json")
            .ShouldBeFalse("detector must not fire on prose mentioning the file");

        // The allow-listed file must actually contain what it is allow-listed for, or the
        // exemption is laundering a file that no longer hashes anything.
        var shared = StripComments(File.ReadAllText(Path.Combine(Repository.Root, SharedImplementation)));
        s_sha256Probe.IsMatch(shared).ShouldBeTrue(
            "ContentTrustCatalog must itself hash, or the single-implementation assertion above is " +
            "satisfied by a scan that matches nothing");
    }

    [Fact]
    public void AllowList_Entries_AllExist()
    {
        var missing = s_allowedSha256Uses.Keys
            .Where(rel => !File.Exists(Path.Combine(Repository.Root, rel)))
            .ToList();

        missing.ShouldBeEmpty(
            "#2682: allow-listed file(s) no longer exist - remove or update the entry:\n  "
            + string.Join("\n  ", missing));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Matches an actual SHA-256 computation, not a mere namespace import.</summary>
    private static readonly Regex s_sha256Probe = new(
        @"\bSHA256\s*\.\s*(?:HashData|Create)\s*\(|\bIncrementalHash\.CreateHash\s*\(\s*HashAlgorithmName\.SHA256",
        RegexOptions.Compiled);

    /// <summary>Matches the trust-catalogue vocabulary, scoping the hash rule to trust decisions.</summary>
    private static readonly Regex s_trustVocabularyProbe = new(
        @"\bTrustCatalog\b|\bTrustVerificationResult\b|\bCatalogFileName\b",
        RegexOptions.Compiled);

    /// <summary>Matches a declaration whose value is the catalog file name.</summary>
    private static readonly Regex s_catalogLiteralProbe = new(
        @"=\s*""trust\.json""", RegexOptions.Compiled);

    private string[] EnumMembers(string relativePath, string enumName)
    {
        var file = Path.Combine(Repository.Root, relativePath);
        File.Exists(file).ShouldBeTrue($"expected {relativePath} to exist");

        var match = Regex.Match(
            File.ReadAllText(file),
            @"enum\s+" + Regex.Escape(enumName) + @"\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        match.Success.ShouldBeTrue($"could not locate 'enum {enumName}' in {relativePath}");

        var body = StripComments(match.Groups["body"].Value);

        return body
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.Split('=')[0].Trim())
            .Where(m => m.Length > 0 && !m.StartsWith('['))
            .ToArray();
    }

    private IReadOnlyList<string> SourceFiles() =>
        Directory
            .EnumerateFiles(Repository.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    private string Rel(string file) =>
        Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }
}
