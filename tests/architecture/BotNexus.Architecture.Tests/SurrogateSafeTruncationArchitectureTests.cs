using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #2883: .NET strings are UTF-16, so slicing arbitrary text with <c>value[..max]</c> can cut
/// between a high and a low surrogate and emit a lone surrogate. That renders as U+FFFD and, on
/// the persisting paths (conversation titles, memory previews), is unrepairable afterwards.
/// </summary>
/// <remarks>
/// The defect's real shape was that the same one-liner had been copied to fourteen call sites, so
/// there was no single place to fix. This fence exists to stop the fifteenth: any new
/// range-truncation of content text under <c>src/gateway</c> OR <c>src/extensions</c> must go
/// through the shared boundary policy.
/// <para>
/// #2924 widened the scan from <c>src/gateway</c> to <c>src/extensions</c> as well. The gateway-only
/// scope was the reason two weaker copies (the Blazor portal's <c>SurrogateSafeText</c> and
/// Telegram's <c>SliceSurrogateSafe</c>) could sit outside the fence indefinitely, and a fourth
/// copy could have been added without failing anything.
/// </para>
/// </remarks>
public sealed class SurrogateSafeTruncationArchitectureTests
{
    /// <summary>
    /// Matches a range slice whose result is immediately concatenated with a string literal - the
    /// truncate-and-append-ellipsis shape. Slices used for parsing (<c>raw[..idx]</c> feeding a
    /// <c>Trim()</c>, a hash prefix, a scheme split) are not this shape and are not the defect.
    /// </summary>
    private static readonly Regex s_truncateAndAppend = new(
        @"\[\.\.[A-Za-z0-9_]+\]\s*\+\s*[$@]?""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches interpolation of a range slice directly into a string, e.g. <c>$"{line[..Max]}..."</c>.
    /// </summary>
    private static readonly Regex s_interpolatedSlice = new(
        @"\{[A-Za-z0-9_.]+\[\.\.[A-Za-z0-9_]+\]\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// #3171: matches a bare range slice whose bound is NAMED like a length budget - <c>MaxFoo</c>,
    /// <c>FooLimit</c>, <c>PreviewLength</c>, <c>_options.TombstonePreviewChars</c>. This is the
    /// third shape of the same defect and the one the original two patterns missed: neither
    /// <c>content = content[..MaxContentChars];</c> (KnowledgeGetTool) nor
    /// <c>text[..PreviewLength]</c> (ToolCallValidator) concatenates or interpolates at the slice
    /// itself, so both sat inside the fenced tree for two issues without failing anything.
    /// </summary>
    /// <remarks>
    /// Keying on the BOUND'S NAME rather than on the surrounding syntax is what makes this precise.
    /// A parsing slice cuts at a discovered index (<c>raw[..idx]</c>, <c>value[..separator]</c>) and
    /// so is not matched; a truncation slice cuts at a configured ceiling, and naming that ceiling
    /// <c>Max*</c>/<c>*Limit*</c>/<c>*Preview*</c> is the established convention in this codebase.
    /// </remarks>
    private static readonly Regex s_boundedSlice = new(
        @"\[\.\.\s*[A-Za-z0-9_.]*(?:Max|Limit|Preview|Budget|Cap)[A-Za-z0-9_]*\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Call sites that legitimately keep raw slicing, each with the reason it cannot produce a
    /// lone surrogate. Every entry here is a deliberate decision, not an unreviewed exemption.
    /// </summary>
    private static readonly Dictionary<string, string> s_allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("gateway", "BotNexus.Cli", "Commands", "ConversationCommands.cs")] =
            "TruncateId: conversation/session ids are generated ASCII (prefix + hex), never user text.",
        [Path.Combine("gateway", "BotNexus.Gateway", "Streaming", "StreamingSessionHelper.cs")] =
            "Already rune-aware: retainedChars is computed by walking Runes within a byte budget.",
        [Path.Combine("extensions", "BotNexus.Extensions.Skills", "Security", "SkillTrustVerifier.cs")] =
            "Hash prefixes: entry.Sha256[..12] slices generated lowercase hex, never user text (#2924).",
        [Path.Combine("domain", "BotNexus.Domain.Wire", "TextualMimeType.cs")] =
            "DecodeBounded slices a ReadOnlySpan<byte> before UTF-8 decoding, not a UTF-16 string, so no surrogate exists to split (#3171).",
    };

    /// <summary>
    /// The single permitted implementation of the boundary walk, and the delegating seams allowed to
    /// name <c>char.IsHighSurrogate</c> in a truncation context (#2924 acceptance criterion 1).
    /// </summary>
    private static readonly string SharedImplementation =
        Path.Combine("domain", "BotNexus.Domain.Wire", "GraphemeSafeTruncation.cs");

    /// <summary>
    /// Files under <c>src</c> permitted to call <c>char.IsHighSurrogate</c> at all. Each is a
    /// non-truncation use: they classify or copy surrogates rather than choosing a cut point.
    /// </summary>
    private static readonly Dictionary<string, string> s_allowedSurrogateInspection = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("agent", "BotNexus.Agent.Providers.Core", "Utilities", "UnicodeStringExtensions.cs")] =
            "Sanitisation, not truncation: walks pairs to copy or replace them, never picks a length.",
        [Path.Combine("gateway", "BotNexus.Tools", "EditTool.cs")] =
            "Normalisation index map: emits both halves of a pair together; no length limit involved.",
    };

    [Fact]
    public void NoProductionSourceFile_TruncatesContentWithRawRangeSlicing()
    {
        var srcRoot = FindSourceRoot();
        var candidates = 0;
        var violations = new List<string>();

        foreach (var path in EnumerateFencedCsFiles(srcRoot))
        {
            candidates++;
            var text = File.ReadAllText(path);
            if (!s_truncateAndAppend.IsMatch(text)
                && !s_interpolatedSlice.IsMatch(text)
                && !s_boundedSlice.IsMatch(text))
                continue;

            var relative = ToRelative(srcRoot, path);
            if (s_allowed.ContainsKey(relative))
                continue;

            violations.Add(relative);
        }

        // Non-vacuity: a fence that scanned nothing would pass silently. #2910 shipped one that
        // resolved its own root to the wrong directory and passed for exactly that reason.
        candidates.ShouldBeGreaterThan(
            200,
            $"fence scanned only {candidates} files under {srcRoot} - the source root resolved wrongly");

        violations.ShouldBeEmpty(
            "#2883: content truncation must use TextTruncation.SafeTruncate so a cut cannot split a " +
            "surrogate pair. Raw value[..max] slicing of user-, model- or command-supplied text is " +
            "the defect this fence prevents.\nViolations:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The helper must remain the single implementation; a second private copy would recreate the
    /// drift that #2883 was filed to remove.
    /// </summary>
    /// <remarks>
    /// #2925 moved the body onto a <c>this string</c> extension and left
    /// <c>TextTruncation.SafeTruncate</c> as a thin forwarder, so the declaration text now appears
    /// twice while there is still exactly ONE implementation. A declaration carrying the documented
    /// forwarding-shim marker is therefore not counted. This does not weaken the fence: a second
    /// genuine body - which by definition carries no shim marker - still fails it, and a shim that
    /// grows a real body loses its right to the marker.
    /// </remarks>
    [Fact]
    public void SafeTruncate_HasExactlyOneImplementation()
    {
        var srcRoot = FindSourceRoot();

        var declaring = EnumerateAllCsFiles(srcRoot)
            .Where(p => DeclaresNonShimSafeTruncate(File.ReadAllText(p)))
            .Select(p => ToRelative(srcRoot, p))
            .ToList();

        declaring.Count.ShouldBe(
            1,
            "#2883: exactly one SafeTruncate implementation must exist. Found: "
            + string.Join(", ", declaring));
    }

    /// <summary>
    /// True when the file declares a <c>SafeTruncate</c> whose doc comment does NOT mark it as a
    /// #2925 forwarding shim - i.e. a real implementation.
    /// </summary>
    private static bool DeclaresNonShimSafeTruncate(string text)
    {
        const string Declaration = "public static string? SafeTruncate";
        const string ShimMarker = "Documented forwarding shim (#2925)";

        var index = text.IndexOf(Declaration, StringComparison.Ordinal);
        var previousDeclarationEnd = 0;
        while (index >= 0)
        {
            // The lookback is bounded by the PREVIOUS declaration as well as by a fixed window, so
            // one marked shim cannot launder an unmarked real body that follows it.
            var windowStart = Math.Max(previousDeclarationEnd, Math.Max(0, index - 900));
            if (!text.AsSpan(windowStart, index - windowStart).Contains(ShimMarker, StringComparison.Ordinal))
                return true;

            previousDeclarationEnd = index + Declaration.Length;
            index = text.IndexOf(Declaration, previousDeclarationEnd, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// #2925, pinned: the shim exclusion above must recognise a marked forwarder AND must still
    /// count a real body, so the exclusion cannot be used to hide a second implementation.
    /// </summary>
    [Fact]
    public void ShimExclusion_CountsRealBodies_AndSkipsMarkedForwarders()
    {
        const string shim = """
            /// <remarks>
            /// Documented forwarding shim (#2925). Implementation moved to the extension.
            /// </remarks>
            public static string? SafeTruncate(string? v, int max) => v.SafeTruncate(max);
            """;
        const string real = """
            /// <summary>Truncates.</summary>
            public static string? SafeTruncate(string? v, int max) { return v?[..max]; }
            """;

        DeclaresNonShimSafeTruncate(shim).ShouldBeFalse();
        DeclaresNonShimSafeTruncate(real).ShouldBeTrue();
        DeclaresNonShimSafeTruncate(shim + "\n" + real).ShouldBeTrue();
    }

    /// <summary>
    /// #2924 acceptance criterion 1: exactly ONE grapheme-safe boundary implementation exists under
    /// <c>src</c>, and no truncation-boundary surrogate back-off lives anywhere else. Before this
    /// issue there were three: the domain's grapheme-correct walk, the portal's one-code-unit
    /// back-off, and Telegram's. The two weaker ones split ZWJ sequences and flag pairs.
    /// </summary>
    [Fact]
    public void GraphemeSafeBoundary_HasExactlyOneImplementation()
    {
        var srcRoot = FindSourceRoot();

        var declaring = EnumerateAllCsFiles(srcRoot)
            .Where(p => File.ReadAllText(p).Contains(
                "char.IsHighSurrogate", StringComparison.Ordinal))
            .Select(p => ToRelative(srcRoot, p))
            .ToList();

        // Non-vacuity: if the scan found nothing at all, either the source root resolved wrongly or
        // the shared implementation was renamed away - both make this test pass while guarding
        // nothing. The shared file MUST be among the hits.
        declaring.ShouldContain(
            SharedImplementation,
            "#2924: the shared boundary implementation must be found by this scan. If it moved, " +
            "update SharedImplementation - do not let the fence pass on an empty candidate set.");

        var unexpected = declaring
            .Where(rel =>
                !string.Equals(rel, SharedImplementation, StringComparison.OrdinalIgnoreCase) &&
                !s_allowedSurrogateInspection.ContainsKey(rel))
            .ToList();

        unexpected.ShouldBeEmpty(
            "#2924: grapheme-safe truncation must have exactly ONE implementation, in " +
            SharedImplementation + ". A surrogate back-off elsewhere is a second truncation " +
            "policy and will diverge, exactly as the portal and Telegram copies did. If the use is " +
            "genuinely NOT about choosing a truncation length, add it to " +
            "s_allowedSurrogateInspection WITH a written reason.\nOffenders:\n  " +
            string.Join("\n  ", unexpected));
    }

    /// <summary>
    /// Every surrogate-inspection allow-list entry must name a file that exists and carry a reason,
    /// so a rename cannot silently widen the exemption into a hole.
    /// </summary>
    [Fact]
    public void SurrogateInspectionAllowList_EntriesAllExistAndAreJustified()
    {
        var srcRoot = FindSourceRoot();

        var missing = s_allowedSurrogateInspection.Keys
            .Where(rel => !File.Exists(Path.Combine(srcRoot, rel)))
            .ToList();

        missing.ShouldBeEmpty(
            "#2924: allow-listed file(s) no longer exist - remove or update the entry:\n  "
            + string.Join("\n  ", missing));

        File.Exists(Path.Combine(srcRoot, SharedImplementation)).ShouldBeTrue(
            "#2924: the shared implementation path must exist: " + SharedImplementation);

        foreach (var (file, reason) in s_allowedSurrogateInspection)
        {
            reason.Length.ShouldBeGreaterThan(
                30,
                $"Allow-list entry '{file}' has no meaningful written justification (#2924).");
        }
    }

    /// <summary>
    /// #2924 acceptance criterion 5: the widened scope is real. The fence must actually be walking
    /// files under <c>src/extensions</c>, not just declaring that it does - a scope widening that
    /// enumerates nothing is the #2910 vacuity failure repeated.
    /// </summary>
    [Fact]
    public void Fence_ActuallyScansExtensions_AndWouldFlagARawSliceThere()
    {
        var srcRoot = FindSourceRoot();

        var extensionFiles = EnumerateFencedCsFiles(srcRoot)
            .Where(p => ToRelative(srcRoot, p).StartsWith("extensions", StringComparison.OrdinalIgnoreCase))
            .ToList();

        extensionFiles.Count.ShouldBeGreaterThan(
            100,
            $"#2924: the fence scanned only {extensionFiles.Count} files under src/extensions. The " +
            "widened scope must actually enumerate the extension tree, or criterion 5 is vacuous.");

        // The exact shape a reintroduced raw-slice truncation in BlazorClient.Core would take.
        s_truncateAndAppend.IsMatch(@"return value[..max] + ""..."";").ShouldBeTrue(
            "#2924: a raw-slice truncation reintroduced in BlazorClient.Core must be detected.");
        s_truncateAndAppend.IsMatch(
            @"return GraphemeSafeTruncation.Truncate(value, max) ?? string.Empty;").ShouldBeFalse(
            "#2924: delegating to the shared policy must NOT be flagged.");

        FencedAreas.ShouldContain("extensions");
        FencedAreas.ShouldContain("gateway");
    }

    /// <summary>
    /// Vacuity guard: the patterns must match the shapes they claim to, and must not fire on the
    /// parsing slices that are legitimately left alone.
    /// </summary>
    [Fact]
    public void Fence_Regexes_MatchTheirTargetShapes()
    {
        s_truncateAndAppend.IsMatch(@"value[..maxLength] + ""...""").ShouldBeTrue();
        s_truncateAndAppend.IsMatch(@"content[..limit] + $""... [truncated]""").ShouldBeTrue();
        s_truncateAndAppend.IsMatch(@"TextTruncation.SafeTruncate(value, maxLength, ""..."")").ShouldBeFalse();

        // Parsing slices must NOT be flagged - they are not truncation.
        s_truncateAndAppend.IsMatch(@"var key = raw[..idx].Trim();").ShouldBeFalse();
        s_truncateAndAppend.IsMatch(@"remainder = remainder[..pathIndex];").ShouldBeFalse();

        s_interpolatedSlice.IsMatch(@"$""{line[..MaxLineLength]}... [truncated]""").ShouldBeTrue();
        s_interpolatedSlice.IsMatch(@"$""{TextTruncation.SafeTruncate(line, Max)}...""").ShouldBeFalse();
    }

    /// <summary>
    /// #3171 acceptance criterion 5: the bare bounded-slice pattern must match the two shapes this
    /// issue fixed, and must NOT fire on a parsing slice or on a delegation to the shared policy.
    /// Without these cases the widened pattern could be quietly neutered and still pass.
    /// </summary>
    [Fact]
    public void BoundedSliceRegex_MatchesTheShapesFixedBy3171_AndNotParsingSlices()
    {
        // The two exact call sites #3171 removed.
        s_boundedSlice.IsMatch(@"content = content[..MaxContentChars];").ShouldBeTrue();
        s_boundedSlice.IsMatch(@"return $""string \""{text[..PreviewLength]}"";").ShouldBeTrue();

        // Other budget-named ceilings, including qualified ones.
        s_boundedSlice.IsMatch(@"summary = summary[..options.MaxSummaryChars];").ShouldBeTrue();
        s_boundedSlice.IsMatch(@"var p = c[.._options.TombstonePreviewChars];").ShouldBeTrue();

        // Parsing slices cut at a DISCOVERED index, not a configured ceiling - not the defect.
        s_boundedSlice.IsMatch(@"var key = raw[..idx].Trim();").ShouldBeFalse();
        s_boundedSlice.IsMatch(@"remainder = remainder[..pathIndex];").ShouldBeFalse();
        s_boundedSlice.IsMatch(@"var head = value[..separator];").ShouldBeFalse();

        // Delegating to the shared policy must never be flagged.
        s_boundedSlice.IsMatch(
            @"content = TextTruncation.SafeTruncate(content, MaxContentChars);").ShouldBeFalse();
        s_boundedSlice.IsMatch(
            @"var p = GraphemeSafeTruncation.Truncate(text, PreviewLength);").ShouldBeFalse();
    }

    /// <summary>
    /// #3171 acceptance criterion 5: the two files this issue fixed must now route through the
    /// shared policy. A regex fence proves the absence of a shape; this proves the presence of the
    /// replacement, so deleting the call and the slice together cannot pass both tests.
    /// </summary>
    [Fact]
    public void ToolOutputPreviewSites_RouteThroughTheSharedPolicy()
    {
        var srcRoot = FindSourceRoot();

        var sites = new Dictionary<string, string>
        {
            [Path.Combine("extensions", "BotNexus.Extensions.Qmd", "KnowledgeGetTool.cs")] =
                "TextTruncation.SafeTruncate",
            [Path.Combine("agent", "BotNexus.Agent.Providers.Core", "Validation", "ToolCallValidator.cs")] =
                "GraphemeSafeTruncation.Truncate",
        };

        foreach (var (relative, expectedCall) in sites)
        {
            var full = Path.Combine(srcRoot, relative);
            File.Exists(full).ShouldBeTrue($"#3171: expected source file {relative} to exist.");
            File.ReadAllText(full).ShouldContain(
                expectedCall,
                Case.Sensitive,
                $"#3171: {relative} must bound its tool-output preview via {expectedCall}. " +
                "Reverting it to a raw range slice reintroduces the lone-surrogate defect.");
        }
    }

    /// <summary>
    /// Every allow-list entry must name a file that exists, so a rename cannot silently widen the
    /// exemption into a hole.
    /// </summary>
    [Fact]
    public void AllowList_Entries_AllExist()
    {
        var srcRoot = FindSourceRoot();
        var missing = s_allowed.Keys
            .Where(rel => !File.Exists(Path.Combine(srcRoot, rel)))
            .ToList();

        missing.ShouldBeEmpty(
            "#2883: allow-listed file(s) no longer exist - remove or update the entry:\n  "
            + string.Join("\n  ", missing));
    }

    private static IEnumerable<string> EnumerateFencedCsFiles(string srcRoot) =>
        FencedAreas.SelectMany(area => EnumerateAllCsFiles(Path.Combine(srcRoot, area)));

    /// <summary>
    /// The areas of <c>src</c> this fence scans. #2924 added <c>extensions</c>: the Blazor portal and
    /// the Telegram channel both carried their own weaker truncation precisely because they sat
    /// outside the original gateway-only scope. #3171 added <c>agent</c> and <c>domain</c> for the
    /// same reason a third time: <c>ToolCallValidator</c>'s 40-code-unit argument preview lives under
    /// <c>src/agent</c> and was therefore invisible to a fence that claimed to cover tool output.
    /// </summary>
    private static readonly string[] FencedAreas = ["gateway", "extensions", "agent", "domain"];

    private static IEnumerable<string> EnumerateAllCsFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : full;
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return Path.Combine(current!.FullName, "src");
    }
}
