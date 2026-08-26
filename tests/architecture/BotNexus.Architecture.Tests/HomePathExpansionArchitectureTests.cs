using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for issue #3013: exactly one <c>~</c> home-path expansion
/// implementation may exist under <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The defect this fence prevents is not a bug in any one copy - it is the copying itself. Five call
/// sites across four files each carried their own twelve-line expansion helper, and they had drifted:
/// two fell back to the <c>HOME</c> environment variable when
/// <see cref="Environment.SpecialFolder.UserProfile"/> was empty and two did not, so the same configured
/// string resolved differently depending only on which code path read it. Deleting the copies fixes
/// today; this fence is what stops the sixth copy being pasted in tomorrow.
/// </para>
/// <para>
/// The fence scans <c>src/</c> for the recognisable shape of a hand-rolled expansion - a test for a
/// leading <c>~</c> - and allows it only in <c>HomePathExpander.cs</c>. Comments and XML doc are
/// stripped before scanning so that prose describing the behaviour does not trip the fence; the point
/// is to forbid a second implementation, not a second mention.
/// </para>
/// </remarks>
public sealed class HomePathExpansionArchitectureTests : ArchitectureTest
{
    /// <summary>The single file permitted to implement <c>~</c> expansion.</summary>
    private const string CanonicalImplementation = "HomePathExpander.cs";

    /// <summary>
    /// Matches the hand-rolled expansion shape: a leading-<c>~</c> test, in any of the forms the four
    /// deleted copies used, plus the <c>Substring</c>/range variants a new copy would likely use.
    /// </summary>
    private static readonly Regex TildeExpansionShape = new(
        @"StartsWith\(\s*'~'\s*\)"
        + @"|\[\s*0\s*\]\s*==\s*'~'"
        + @"|==\s*'~'",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches a home-directory lookup. On its own this is legitimate everywhere (many components
    /// build <c>~/.botnexus</c> paths); it only indicates a duplicate expander when it appears in the
    /// same file as a leading-<c>~</c> test.
    /// </summary>
    private static readonly Regex HomeLookupShape = new(
        @"SpecialFolder\.UserProfile|GetEnvironmentVariable\(\s*""HOME""\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void OnlyOneTildeExpansionImplementation_ExistsUnderSrc()
    {
        var repoRoot = Repository.Root;
        var sourceRoot = Path.Combine(repoRoot, "src");
        Directory.Exists(sourceRoot).ShouldBeTrue($"Expected a src directory at '{sourceRoot}'.");

        var candidates = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Non-vacuity: if the glob matched nothing the fence would pass for the wrong reason.
        candidates.Count.ShouldBeGreaterThan(100,
            "The fence found almost no source files, so a pass would prove nothing. Check the repo-root resolution.");

        var offenders = new List<string>();
        var canonicalSeen = false;

        foreach (var path in candidates)
        {
            var code = StripCommentsAndStrings(File.ReadAllText(path));
            if (!TildeExpansionShape.IsMatch(code))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(path), CanonicalImplementation, StringComparison.Ordinal))
            {
                canonicalSeen = true;
                continue;
            }

            // A leading-'~' test with no home lookup is a rejection/validation guard, not an expander
            // (GrepTool refuses '~' paths outright, for example). Only the pair is a duplicate.
            if (HomeLookupShape.IsMatch(code))
            {
                offenders.Add(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
            }
        }

        // Non-vacuity: the fence must actually be looking at the canonical implementation. If the file
        // were renamed or its shape changed, an empty offender list would be meaningless.
        canonicalSeen.ShouldBeTrue(
            $"The canonical expander '{CanonicalImplementation}' was not found by the fence's own pattern. "
            + "Either it moved or its implementation shape changed - update this fence deliberately.");

        offenders.ShouldBeEmpty(
            "A second '~' home-path expansion implementation exists. BotNexus had five such copies "
            + "across four files and they had already drifted on the HOME fallback (issue #3013). "
            + "Call BotNexus.Domain.Paths.HomePathExpander.Expand instead of writing another.\n"
            + "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Removes line comments, block comments and string/char literals so the scan sees code only.
    /// </summary>
    /// <remarks>
    /// Without this the fence fires on its own citation of the pattern in nearby XML doc, and on the
    /// canonical expander's remarks - the recurring "the fence trips on the comment explaining the
    /// fence" failure.
    /// </remarks>
    private static string StripCommentsAndStrings(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//[^\r\n]*", " ");
        // Keep '~' char literals intact - they are the thing being detected - but drop string literals,
        // which is where documentation-ish text and error messages naming '~' live.
        return Regex.Replace(withoutLineComments, "\"(?:[^\"\\\\\r\n]|\\\\.)*\"", "\"\"");
    }

}
