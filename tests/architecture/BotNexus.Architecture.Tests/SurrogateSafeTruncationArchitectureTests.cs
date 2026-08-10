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
/// range-truncation of content text under <c>src/gateway</c> must go through
/// <c>TextTruncation.SafeTruncate</c>.
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
    /// Call sites that legitimately keep raw slicing, each with the reason it cannot produce a
    /// lone surrogate. Every entry here is a deliberate decision, not an unreviewed exemption.
    /// </summary>
    private static readonly Dictionary<string, string> s_allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("gateway", "BotNexus.Cli", "Commands", "ConversationCommands.cs")] =
            "TruncateId: conversation/session ids are generated ASCII (prefix + hex), never user text.",
        [Path.Combine("gateway", "BotNexus.Gateway", "Streaming", "StreamingSessionHelper.cs")] =
            "Already rune-aware: retainedChars is computed by walking Runes within a byte budget.",
    };

    [Fact]
    public void NoProductionSourceFile_TruncatesContentWithRawRangeSlicing()
    {
        var srcRoot = FindSourceRoot();
        var candidates = 0;
        var violations = new List<string>();

        foreach (var path in EnumerateGatewayCsFiles(srcRoot))
        {
            candidates++;
            var text = File.ReadAllText(path);
            if (!s_truncateAndAppend.IsMatch(text) && !s_interpolatedSlice.IsMatch(text))
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
    [Fact]
    public void SafeTruncate_HasExactlyOneImplementation()
    {
        var srcRoot = FindSourceRoot();

        var declaring = EnumerateAllCsFiles(srcRoot)
            .Where(p => File.ReadAllText(p).Contains(
                "public static string? SafeTruncate", StringComparison.Ordinal))
            .Select(p => ToRelative(srcRoot, p))
            .ToList();

        declaring.Count.ShouldBe(
            1,
            "#2883: exactly one SafeTruncate implementation must exist. Found: "
            + string.Join(", ", declaring));
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

    private static IEnumerable<string> EnumerateGatewayCsFiles(string srcRoot) =>
        EnumerateAllCsFiles(Path.Combine(srcRoot, "gateway"));

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
