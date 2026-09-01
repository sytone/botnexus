using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for #2888 (#2765 AC5): a config path named by a type OUTSIDE
/// <c>BotNexus.Gateway.Configuration</c> must be one that <c>IConfigPathResolver</c> can resolve.
///
/// <para>
/// <b>Why this exists.</b> #2764 records two <c>doctor config</c> checks reading
/// <c>root["compaction"]</c> while the setting binds at <c>gateway.compaction</c>. A wrong traversal
/// returns null, which is indistinguishable from "not configured", so the check reported a healthy
/// platform as broken on every run and its sibling guard was silently inert. The three fences in
/// <see cref="ConfigurationProjectBoundaryArchitectureTests"/> assert the assembly reference graph
/// only - none of them looks at <em>which path</em> a consumer reads, so the exact #2764 shape
/// passes all of them and also passes at runtime. Reading the code was the only thing that found it.
/// </para>
///
/// <para>
/// <b>Resolution is delegated, never restated.</b> Each extracted literal is probed against a
/// throwaway <c>PlatformConfig</c> graph through the production <c>ConfigPathResolver</c>, the same
/// technique <c>BoundConfigPath.IsBound</c> uses. A second, test-local notion of "which paths exist"
/// would be one more copy of the duplicated knowledge that caused #2764 in the first place.
/// </para>
///
/// <para>
/// <b>Baseline posture, mirroring <see cref="ConfigFieldCoverageFenceArchitectureTests"/>.</b>
/// Pre-existing unresolvable literals live in a checked-in baseline enforced as a MAXIMUM. It fails
/// in both directions: a NEW unresolvable literal fails
/// <see cref="EveryConfigPathUsedOutsideTheConfigurationProject_Resolves"/>, and a STALE entry whose
/// file no longer carries the shape fails <see cref="Baseline_ContainsNoStaleEntries"/>. A baseline
/// that only fails one way rots into a permanent suppression list.
/// </para>
///
/// <para>
/// <b>Non-vacuity (AC4).</b> <see cref="Fence_Reddens_OnThe2764Shape"/> drives the real extractor and
/// the real resolver over the literal <c>root["compaction"]</c> shape and requires it to be reported;
/// <see cref="Fence_DoesNotFlag_TheBoundPath"/> requires the corrected <c>gateway.compaction</c> path
/// NOT to be reported, so the fence is discriminating rather than merely loud. Mutation-verified out
/// of suite as well - see the PR. A fence that cannot fire is the #2700 failure mode and reads as a
/// clean pass.
/// </para>
/// </summary>
public sealed class ConfigPathResolutionFenceArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// Pre-existing unresolvable literals, one entry per <c>file|literal</c>.
    ///
    /// <para><b>This list may only shrink.</b> Route the consumer through a resolvable path and
    /// delete its line. Never add an entry to silence a new violation.</para>
    /// </summary>
    private static readonly HashSet<string> Baseline = LoadBaseline();

    /// <summary>
    /// Exact expected baseline size, asserted separately from the contents so a bulk suppression is a
    /// visible one-line numeric change in review rather than something buried in a large diff.
    /// </summary>
    private const int ExpectedBaselineCount = 0;

    /// <summary>AC1 + AC2: every config-path literal outside the configuration project resolves.</summary>
    [Fact]
    public void EveryConfigPathUsedOutsideTheConfigurationProject_Resolves()
    {
        var usages = ConfigPathFence.ExtractUsages(Repository.Root);

        // Non-vacuity guard: an empty candidate set means the extraction silently found nothing and
        // the assertion below is over air. The repository demonstrably contains raw-document reads
        // (doctor checks, the CLI init and satellite commands), so a low count is a broken scan.
        usages.Count.ShouldBeGreaterThanOrEqualTo(
            5,
            "the extraction must discover the repository's config-path literals; found " +
            usages.Count + ". A near-empty set means the scan broke, not that the codebase is clean.");

        var violations = ConfigPathFence.FindViolations(usages)
            .Where(v => !Baseline.Contains(v.Key))
            .ToList();

        violations.ShouldBeEmpty(
            "A type outside BotNexus.Gateway.Configuration names a config path that " +
            "IConfigPathResolver cannot resolve. A wrong traversal returns null, which is " +
            "indistinguishable from 'not configured', so the defect is invisible at runtime (#2764). " +
            "Use the path the binder actually reads.\nOffenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    /// <summary>AC3: the baseline is a maximum, not a growing suppression list.</summary>
    [Fact]
    public void Baseline_DoesNotGrow()
    {
        Baseline.Count.ShouldBe(
            ExpectedBaselineCount,
            $"The config-path baseline must only shrink. Expected {ExpectedBaselineCount} entries, " +
            $"found {Baseline.Count}. If you fixed a consumer, lower ExpectedBaselineCount. If this " +
            "number went UP, a new unresolvable path was silenced instead of fixed.");
    }

    /// <summary>
    /// AC3, the other direction: an entry whose file no longer carries the shape must be deleted.
    /// Without this the baseline decays into a permanent allow-list that nobody prunes.
    /// </summary>
    [Fact]
    public void Baseline_ContainsNoStaleEntries()
    {
        var live = ConfigPathFence
            .FindViolations(ConfigPathFence.ExtractUsages(Repository.Root))
            .Select(v => v.Key)
            .ToHashSet(StringComparer.Ordinal);

        var stale = Baseline
            .Where(b => !live.Contains(b))
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "These baseline entries no longer correspond to an unresolvable config path - the " +
            "consumer was fixed, renamed or removed. Delete them (and lower ExpectedBaselineCount) " +
            "so the baseline keeps measuring real remaining work.\nStale:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>
    /// AC4 in-suite: the real extractor plus the real resolver must report the literal #2764 shape.
    /// If this stops failing, the fence above proves nothing however green it looks.
    /// </summary>
    [Fact]
    public void Fence_Reddens_OnThe2764Shape()
    {
        const string source = """
                              var compaction = root["compaction"] as JsonObject;
                              """;

        var paths = ConfigPathFence.ExtractFromText(source);
        paths.ShouldContain("compaction", "the extractor must recognise a raw document root indexer");

        var violations = ConfigPathFence.FindViolations(
            [new ConfigPathFence.Usage("src/gateway/BotNexus.Cli/Commands/Doctor/ScratchCheck.cs", "compaction")]);

        violations.Count.ShouldBe(1, "the #2764 shape must be reported as unresolvable");
        violations[0].Describe().ShouldContain("ScratchCheck.cs", Case.Sensitive,
            "AC2: the failure message must name the offending file");
        violations[0].Describe().ShouldContain("\"compaction\"", Case.Sensitive,
            "AC2: the failure message must name the offending literal");
        violations[0].Suggestion.ShouldBe("gateway.compaction",
            "AC2: the failure message must name the closest resolvable path - the whole point is to " +
            "tell the author where the setting really binds.");
    }

    /// <summary>
    /// Positive pin: the corrected path must NOT be reported. A fence that flags everything is
    /// equivalent to one that flags nothing, and generates the pressure to weaken it that the issue
    /// names as the main risk.
    /// </summary>
    [Fact]
    public void Fence_DoesNotFlag_TheBoundPath()
    {
        var violations = ConfigPathFence.FindViolations(
        [
            new ConfigPathFence.Usage("Scratch.cs", "gateway.compaction"),
            new ConfigPathFence.Usage("Scratch.cs", "gateway.compaction.summarizationModel"),
            new ConfigPathFence.Usage("Scratch.cs", "agents"),
            new ConfigPathFence.Usage("Scratch.cs", "cron"),
        ]);

        violations.ShouldBeEmpty(
            "Positive pin: paths the binder really reads must be accepted. Offenders:\n  " +
            string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    /// <summary>
    /// Negative pin for the extraction predicate: the issue calls out over-matching as the main risk,
    /// so strings that merely look path-ish must not enter the candidate set at all.
    /// </summary>
    [Fact]
    public void Extraction_DoesNotOverMatch()
    {
        const string source = """
                              private const string ConfigFilePath = "config.json";
                              private const string TemplatePath = "templates/pr-body.md";
                              var name = someDictionary["compaction"];
                              logger.LogInformation("gateway.compaction is unset");
                              """;

        ConfigPathFence.ExtractFromText(source).ShouldBeEmpty(
            "Only arguments of the config access surface, root document indexers and dotted *Path " +
            "constants are config paths. Filenames, relative file paths, unrelated dictionary " +
            "lookups and log message text are not, and flagging them would create the friction that " +
            "gets fences weakened.");
    }

    /// <summary>
    /// #3765 AC1: a <c>JsonObject</c> local that merely happens to be NAMED like a document root,
    /// but was parsed from an arbitrary payload, is not a configuration read.
    ///
    /// <para>
    /// This is the live shape from <c>OpenAICompatErrorText.cs</c>: an OpenAI HTTP error body. The
    /// old name-only predicate reported <c>error</c> as an unresolvable path on
    /// <c>PlatformConfig</c>, blocking the base-freshness gate on any PR that parsed JSON into a
    /// conventionally-named local.
    /// </para>
    /// </summary>
    [Fact]
    public void Extraction_DoesNotFlag_ARootNamedLocalParsedFromAnArbitraryPayload()
    {
        const string source = """
                              if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
                              {
                                  message = error["message"]?.GetValue<string>();
                              }
                              var document = JsonSerializer.Deserialize<JsonObject>(webhookPayload);
                              var envelope = document["headers"];
                              """;

        ConfigPathFence.ExtractFromText(source).ShouldBeEmpty(
            "#3765: an identifier name is not provenance. A JsonObject parsed from an HTTP response " +
            "body or a webhook envelope is not the platform configuration document, and reporting " +
            "its keys as config paths misdescribes correct code as a configuration defect.");
    }

    /// <summary>
    /// Non-vacuity guard for the case above. Both literals sit in indexers over root-named locals,
    /// so they ARE candidates for the root-indexer pattern - the exclusion is doing real work rather
    /// than the source failing to match anything in the first place. Rename the locals away from the
    /// payload parse and the identical indexers are extracted again.
    /// </summary>
    [Fact]
    public void Extraction_StillFlags_TheSameIndexersWhenProvenanceIsAbsent()
    {
        const string source = """
                              var value = root["error"];
                              var header = document["headers"];
                              """;

        var paths = ConfigPathFence.ExtractFromText(source);

        paths.ShouldContain("error",
            "without a visible arbitrary-payload parse, a bare document-root indexer is still the " +
            "#2764 shape and must be extracted - otherwise the #3765 narrowing gutted the fence.");
        paths.ShouldContain("headers",
            "same for the second identifier; the exclusion must key on provenance, not on the key.");
    }

    /// <summary>
    /// The other side of the discriminator: a root parsed from the configuration document itself is
    /// still a configuration read. Without this, the #3765 narrowing could be satisfied by dropping
    /// every parsed root, which would be a fence that no longer sees the defect it exists for.
    /// </summary>
    [Fact]
    public void Extraction_StillFlags_ARootParsedFromTheConfigurationDocument()
    {
        const string source = """
                              var root = JsonNode.Parse(configJson) as JsonObject;
                              var compaction = root["compaction"] as JsonObject;
                              """;

        ConfigPathFence.ExtractFromText(source).ShouldContain("compaction",
            "a root parsed from the configuration document is exactly the #2764 shape and must " +
            "still be reported.");
    }

    private static HashSet<string> LoadBaseline()
    {
        var assemblyDir = Path.GetDirectoryName(
            typeof(ConfigPathResolutionFenceArchitectureTests).Assembly.Location)!;
        var path = Path.Combine(assemblyDir, "ConfigPathResolutionBaseline.baseline");

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Config-path baseline not found at '{path}'. It must be copied to the output " +
                "directory (CopyToOutputDirectory) or the fence cannot distinguish pre-existing " +
                "violations from new ones.", path);

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
    }
}
