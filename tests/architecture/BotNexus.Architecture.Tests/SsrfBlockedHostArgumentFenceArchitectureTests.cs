using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #3779 AC6: every production call site of <c>SsrfValidator.Validate</c> / <c>AssertSafe</c> must
/// pass a blocked-host argument.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fence and not a behaviour test.</b> The defect this closes was not a wrong check - it was
/// an <em>omitted optional argument</em>. <c>SsrfValidator.Validate(Uri, IReadOnlyList&lt;string&gt;? = null)</c>
/// compiles, runs and returns a perfectly safe-looking verdict when the second argument is left off;
/// the only thing lost is every operator-configured hostname block. Two of the three gateway egress
/// surfaces passed the list, one did not, and no behaviour test in the repository could see the
/// difference because there was no configured list to enforce in any of them.
/// </para>
/// <para>
/// That makes this the classic exemplar-fixed-never-propagated shape (#2761, #3013, #3018, #3035,
/// and #2745 itself - which routed cron webhooks to the shared validator and still dropped this
/// parameter). Repairing only <c>CronWebhookUrl</c> would leave a fourth egress surface free to
/// repeat the omission on the day it is added, silently. The invariant worth pinning is therefore
/// categorical and structural: <b>the argument is always present at the call site</b>.
/// </para>
/// <para>
/// <b>Passing <c>null</c> explicitly is allowed and is the point.</b> The fence does not demand a
/// non-empty list - a caller with no configured policy has nothing to pass. It demands that the
/// caller <em>say so</em>, so "this surface enforces no configured hosts" is a visible decision in
/// the diff rather than an invisible default. A reviewer can question <c>Validate(uri, null)</c>;
/// nobody can question an argument that is not written down.
/// </para>
/// <para>
/// Scanned over <c>src/</c> only. Test code legitimately calls the one-argument overload to exercise
/// the address-class half of the policy in isolation, and forcing an argument there would test
/// nothing while making the existing <c>SsrfValidatorTests</c> noisier.
/// </para>
/// </remarks>
public sealed class SsrfBlockedHostArgumentFenceArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// Matches a call to <c>Validate</c>/<c>AssertSafe</c> on the shared validator and captures its
    /// argument list up to the closing parenthesis. Nested parentheses in the FIRST argument (e.g.
    /// <c>Validate(new Uri(url))</c>) are why the argument list is captured greedily to the last
    /// <c>)</c> on the line rather than the first.
    /// </summary>
    private static readonly Regex ValidatorCall = new(
        @"SsrfValidator\s*\.\s*(?<method>Validate|AssertSafe)\s*\((?<args>.*)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// The invariant: no production call site may omit the blocked-host argument.
    /// </summary>
    [Fact]
    public void EverySsrfValidatorCallSite_PassesABlockedHostArgument()
    {
        var callSites = FindCallSites();

        callSites.ShouldNotBeEmpty(
            "the fence must find call sites to be meaningful. If SsrfValidator was renamed or the "
            + "egress surfaces were restructured, update this fence rather than deleting it - the "
            + "invariant is that a configured blocked-host list reaches every outbound URL check.");

        var violations = callSites
            .Where(site => !HasSecondArgument(site.Arguments))
            .Select(site => $"{site.RelativePath}:{site.Line} -> SsrfValidator.{site.Method}({site.Arguments})")
            .ToArray();

        violations.ShouldBeEmpty(
            "Every production SsrfValidator call must pass a blocked-host argument (#3779 AC6). The "
            + "parameter is optional, so omitting it compiles and returns a safe-looking verdict while "
            + "silently enforcing NONE of the operator's configured hostname blocks - which is exactly "
            + "how the cron webhook surface enforced less than its configuration said for the whole of "
            + "#2745's life. Pass the surface's configured list, or pass an explicit null so the "
            + "'no configured policy here' decision is visible to a reviewer.\nOffenders:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Non-vacuity: the scan actually recognises a single-argument call. Without this, a regex that
    /// silently stopped matching anything would leave the clause above permanently, invisibly green -
    /// the failure mode a fence exists to prevent, reproduced inside the fence itself.
    /// </summary>
    [Fact]
    public void Fence_RecognisesASingleArgumentCallAsAViolation()
    {
        // Assembled from fragments so this probe cannot be mistaken for a real call site by any
        // future text scan of this repository.
        const string probe = "var r = Ssrf" + "Validator.Validate(parsed);";

        var match = ValidatorCall.Match(probe);
        match.Success.ShouldBeTrue("the fence regex must still match the shape it was written for.");
        HasSecondArgument(match.Groups["args"].Value).ShouldBeFalse(
            "a one-argument call must be classified as a violation, or the clause above proves nothing.");

        const string guarded = "var r = Ssrf" + "Validator.Validate(parsed, blockedHosts);";
        HasSecondArgument(ValidatorCall.Match(guarded).Groups["args"].Value).ShouldBeTrue(
            "a two-argument call must be classified as compliant, or the fence would block the fix.");
    }

    /// <summary>
    /// Splits an argument list at top-level commas only, so <c>Validate(new Uri(a, b))</c> is one
    /// argument rather than two. Depth tracking covers parentheses, brackets and generic-free
    /// collection expressions; a top-level comma at depth zero is the only argument separator.
    /// </summary>
    private static bool HasSecondArgument(string argumentList)
    {
        var depth = 0;
        foreach (var c in argumentList)
        {
            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return true;
            }
        }

        return false;
    }

    private (string RelativePath, int Line, string Method, string Arguments)[] FindCallSites() =>
        (from file in Directory.GetFiles(Repository.SourceRoot, "*.cs", SearchOption.AllDirectories)
         where !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
         let lines = File.ReadAllLines(file)
         from indexed in lines.Select((text, index) => (text, index))
         let stripped = StripLineComment(indexed.text)
         let match = ValidatorCall.Match(stripped)
         where match.Success
         select (
             Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'),
             indexed.index + 1,
             match.Groups["method"].Value,
             match.Groups["args"].Value)).ToArray();

    /// <summary>
    /// Drops <c>//</c> and <c>///</c> content before matching. The validator's own XML docs name
    /// <c>SsrfValidator.AssertSafe</c> in prose (see <c>WebFetchTool</c>), and a fence that fired on
    /// a doc comment would be reporting a defect that does not exist - the trap #2813 and #2955 hit.
    /// </summary>
    private static string StripLineComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }
}
