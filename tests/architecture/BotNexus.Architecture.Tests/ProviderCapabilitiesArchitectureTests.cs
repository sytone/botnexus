using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fitness fence for the provider capability contract (#2432).
/// </summary>
/// <remarks>
/// <para>
/// Two structural properties are pinned here, and neither is expressible as a behaviour test.
/// </para>
/// <para>
/// <b>1. Every real provider declares its capabilities.</b> The behaviour suites can only assert
/// against providers they instantiate. A SIXTH provider added tomorrow that declares nothing would
/// silently inherit the interface default and every existing test would stay green -- which is
/// precisely the "discovered by failure" mode #2432 exists to end.
/// </para>
/// <para>
/// <b>2. No new substring-based model gating.</b> #2374 burned on <c>id.Contains("o1")</c> matching
/// any model with "o1" anywhere in its name. <see cref="ModelFamilyVersion"/> is the single
/// sanctioned parser; a capability contract is exactly the kind of change that tempts an author to
/// write "if the model id contains opus" one more time. This fence makes that a build failure in
/// the provider tree rather than a defect discovered in production two generations later.
/// </para>
/// </remarks>
public class ProviderCapabilitiesArchitectureTests : ArchitectureTest
{
    /// <summary>The provider implementations required by #2432 to declare a capability record.</summary>
    private static readonly string[] RealProviderFiles =
    [
        "src/agent/BotNexus.Agent.Providers.Anthropic/AnthropicProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Messages/CopilotMessagesProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Responses/CopilotResponsesProvider.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Completions/CopilotCompletionsProvider.cs",
        "src/agent/BotNexus.Agent.Providers.OpenAI/OpenAICompletionsProvider.cs",
        "src/agent/BotNexus.Agent.Providers.OpenAI/OpenAIResponsesProvider.cs",
        "src/agent/BotNexus.Agent.Providers.OpenAICompat/OpenAICompatProvider.cs",
    ];

    /// <summary>
    /// The only file permitted to parse a model id into a family and version. Every version gate in
    /// the tree routes through it (#2374).
    /// </summary>
    private const string SanctionedParser =
        "src/agent/BotNexus.Agent.Providers.Core/Registry/ModelFamilyVersion.cs";

    /// <summary>
    /// The substring-gating sites that ALREADY existed when #2432 landed, frozen as a baseline.
    /// <para>
    /// #2432's third acceptance criterion is "no NEW substring-based model gating introduced" -- it
    /// is not a mandate to migrate the pre-existing estate, which spans compat resolution, dynamic
    /// capability discovery and request building, and is its own body of work. Asserting the tree
    /// is already clean would be false; asserting nothing would let the next author add a twelfth
    /// site. A frozen baseline is the only honest fence: these six files are tolerated, a seventh
    /// fails the build, and deleting one from the tree also fails here so the list cannot rot into
    /// a lie.
    /// </para>
    /// </summary>
    private static readonly string[] BaselineSubstringGatingFiles =
    [
        // CopilotTextDeltaNormalizer.cs was removed from this baseline by #3336: its
        // modelId.StartsWith("gpt-5.6") gate is gone, replaced by the transport-quirk flag
        // ProviderCapabilities.FramesStreamedTextDeltasWithCrlf. Migrating a site OUT of the
        // baseline is exactly what the Baseline_ContainsOnlyFilesThatStillCarryTheShape test
        // demands, and is the only sanctioned way this list shrinks.
        "src/agent/BotNexus.Agent.Providers.Copilot/Completions/CopilotCompletionsRequestBuilder.cs",
        "src/agent/BotNexus.Agent.Providers.Core/Compatibility/CompatResolver.cs",
        "src/agent/BotNexus.Agent.Providers.Core/Registry/DynamicModelCapabilities.cs",
        "src/agent/BotNexus.Agent.Providers.Core/Registry/ModelCapabilityHeuristics.cs",
        "src/agent/BotNexus.Agent.Providers.OpenAI/OpenAICompletionsRequestBuilder.cs",
    ];

    /// <summary>A provider declares capabilities when it assigns a <c>ProviderCapabilities</c> record.</summary>
    private static readonly Regex CapabilityDeclaration = new(
        @"ProviderCapabilities\s+Capabilities",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches substring-style gating on a model id: a <c>Contains</c> / <c>StartsWith</c> /
    /// <c>EndsWith</c> / <c>IndexOf</c> probe against something named like a model id, with a
    /// literal argument. This is the #2374 shape -- surface spelling standing in for a declared
    /// family and version.
    /// </summary>
    private static readonly Regex ModelIdSubstringGate = new(
        @"\b\w*[Mm]odel\w*(\.Id)?\s*\.\s*(Contains|StartsWith|EndsWith|IndexOf)\s*\(\s*""",
        RegexOptions.Compiled);


    /// <summary>
    /// AC1: every real provider surfaces a <c>ProviderCapabilities</c>. Reading the source rather
    /// than reflecting over types is deliberate -- a provider that inherits the interface's default
    /// member is indistinguishable at runtime from one that declares an identical record, and it is
    /// the DECLARATION that #2432 requires.
    /// </summary>
    [Fact]
    public void EveryRealProvider_DeclaresProviderCapabilities()
    {
        var missing = new List<string>();

        foreach (var relative in RealProviderFiles)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Expected provider source not found: {relative}. If a provider moved, update this " +
                "fence deliberately rather than deleting the entry.");

            if (!CapabilityDeclaration.IsMatch(StripComments(File.ReadAllText(path))))
                missing.Add(relative);
        }

        missing.ShouldBeEmpty(
            "Every real provider must DECLARE a ProviderCapabilities record (#2432) so the platform " +
            "can answer 'does this provider do X?' without issuing a request and reading what comes " +
            "back. A provider that declares nothing inherits the interface default with every quirk " +
            "workaround OFF, which is safe but silent. Offenders: " + string.Join("; ", missing));
    }

    /// <summary>
    /// AC3: the provider tree introduces no NEW substring-based model gating. Version/family gating
    /// must reuse <c>ModelFamilyVersion</c>, which parses on token boundaries; a raw
    /// <c>id.Contains("...")</c> is the #2374 defect class. The pre-existing estate is frozen by
    /// <see cref="BaselineSubstringGatingFiles"/>; anything outside it is new and fails.
    /// </summary>
    [Fact]
    public void NoNewProviderSource_GatesOnAModelIdSubstring()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateProviderSourceFiles())
        {
            var relative = Relative(file);
            if (string.Equals(relative, SanctionedParser, StringComparison.Ordinal))
                continue;
            if (BaselineSubstringGatingFiles.Contains(relative, StringComparer.Ordinal))
                continue;

            var source = StripComments(File.ReadAllText(file));
            foreach (Match match in ModelIdSubstringGate.Matches(source))
                violations.Add($"{relative}: {match.Value.Trim()}");
        }

        violations.ShouldBeEmpty(
            "Model gating by id substring is the #2374 defect class -- id.Contains(\"o1\") matches " +
            "any model with 'o1' anywhere in its name. Express the gate as 'family X at version >= N' " +
            "via ModelFamilyVersion, the single sanctioned parser, so the next model generation needs " +
            "no code change. Do NOT resolve this by adding the file to the baseline list: that list " +
            "is a frozen record of what predates #2432, not an opt-out. Violations: " +
            string.Join("; ", violations));
    }

    /// <summary>
    /// The baseline must stay honest in BOTH directions. A file listed as pre-existing that no
    /// longer contains substring gating has been cleaned up -- good -- and must be removed from the
    /// list, or the list slowly becomes a blanket exemption for files that would then be free to
    /// reintroduce the shape unchecked.
    /// </summary>
    [Fact]
    public void Baseline_ContainsOnlyFilesThatStillCarryTheShape()
    {
        var stale = new List<string>();

        foreach (var relative in BaselineSubstringGatingFiles)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Baseline substring-gating file no longer exists: {relative}. Remove it from the " +
                "baseline list.");

            if (!ModelIdSubstringGate.IsMatch(StripComments(File.ReadAllText(path))))
                stale.Add(relative);
        }

        stale.ShouldBeEmpty(
            "These files are listed as pre-existing substring-gating sites but no longer contain the " +
            "shape. They have been migrated to ModelFamilyVersion -- remove them from the baseline so " +
            "the exemption cannot be silently reused. Stale entries: " + string.Join("; ", stale));
    }

    /// <summary>
    /// The sanctioned parser must exist and must actually parse. If it were deleted or gutted, the
    /// AC3 fence above would still pass while every gate in the tree had nowhere legitimate to live.
    /// </summary>
    [Fact]
    public void SanctionedParser_ExistsAndOffersTheVersionGate()
    {
        var path = ResolvePath(SanctionedParser);

        File.Exists(path).ShouldBeTrue($"The sanctioned model-version parser is missing: {SanctionedParser}");
        var source = File.ReadAllText(path);
        source.Contains("IsAtLeast", StringComparison.Ordinal).ShouldBeTrue(
            "ModelFamilyVersion must still expose the family/version gate that #2374 introduced; " +
            "without it there is no sanctioned alternative to substring gating.");
    }

    /// <summary>
    /// Non-vacuity: the AC3 scan must examine a real, non-trivial candidate set. A broken repo-root
    /// resolution would make it pass by reading nothing.
    /// </summary>
    [Fact]
    public void Fence_ExaminesANonEmptyProviderSourceSet()
    {
        EnumerateProviderSourceFiles().Count.ShouldBeGreaterThan(
            20,
            "The provider source tree must be discovered, otherwise this fence passes by examining " +
            "nothing.");
    }

    /// <summary>
    /// Proven-red: both detectors must fire on a synthetic violation and stay quiet on the
    /// sanctioned shapes. Without this, a regex that matches nothing passes forever.
    /// </summary>
    [Fact]
    public void Detectors_FireOnSyntheticViolationsAndNotOnSanctionedCode()
    {
        // AC3 detector: the #2374 shape must trip.
        ModelIdSubstringGate.IsMatch(@"if (model.Id.Contains(""o1"")) return true;").ShouldBeTrue(
            "Vacuity guard: a raw model-id substring probe must trip the fence.");
        ModelIdSubstringGate.IsMatch(@"if (modelId.StartsWith(""claude-opus-4-6""))").ShouldBeTrue(
            "Vacuity guard: a literal-prefix model-id probe must trip the fence.");

        // The sanctioned gate, and unrelated string work, must NOT trip.
        ModelIdSubstringGate.IsMatch(@"ModelFamilyVersion.IsAtLeast(modelId, ""opus"", 4, 6)").ShouldBeFalse(
            "Positive pin: the sanctioned family/version gate must be accepted.");
        ModelIdSubstringGate.IsMatch(@"if (api.Contains(""copilot""))").ShouldBeFalse(
            "Positive pin: probing a non-model value must not trip; this fence is about model ids.");

        // AC1 detector.
        CapabilityDeclaration.IsMatch("public ProviderCapabilities Capabilities { get; } = new(...);")
            .ShouldBeTrue("Vacuity guard: a declared capability record must satisfy the AC1 detector.");
        CapabilityDeclaration.IsMatch("public string Api => \"some-api\";")
            .ShouldBeFalse("Positive pin: an api id alone must not count as a capability declaration.");
    }

    /// <summary>
    /// Comments legitimately discuss the banned shape -- the XML docs on the capability record and
    /// on <c>ModelCapabilityHeuristics</c> quote <c>id.Contains("o1")</c> to explain what is being
    /// defended against. Scanning them would make the fence fire on its own explanations.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", "");
    }

    private List<string> EnumerateProviderSourceFiles() =>
        Directory.EnumerateDirectories(Path.Combine(Repository.Root, "src", "agent"), "BotNexus.Agent.Providers.*")
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    private string Relative(string absolute) =>
        Path.GetRelativePath(Repository.Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));

}
