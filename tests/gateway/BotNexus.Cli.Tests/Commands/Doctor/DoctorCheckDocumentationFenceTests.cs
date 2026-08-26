using System.Text.RegularExpressions;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Fence: every registered doctor check and advisory id must be documented in
/// <c>docs/cli-reference.md</c> (issue #3319 AC3).
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/cli-reference.md</c> is the operator's only index of what <c>doctor</c> reports, and it
/// was a third hand-maintained copy of the registry. Two ids had already reached the registries
/// without reaching it - <c>feature-flags-explicit</c> and <c>feature-flags-unknown-key</c> - so the
/// documentation had quietly stopped describing the tool. Nothing failed, because nothing was
/// checking.
/// </para>
/// <para>
/// This fence deliberately does NOT generate the prose. A generated sentence would satisfy the fence
/// while telling an operator nothing; the point is that adding a check FAILS the build until a human
/// writes what it reports. The fence therefore asserts presence of the id string, and the prose
/// around it is a human's job.
/// </para>
/// <para>
/// Non-vacuity (AC4): this fence was demonstrated RED on the pre-fix content of
/// <c>docs/cli-reference.md</c>, naming both undocumented ids, before the documentation was updated.
/// A fence that passes on a known-bad input is the #2700 failure mode.
/// </para>
/// </remarks>
public class DoctorCheckDocumentationFenceTests
{
    [Fact]
    public void EveryRegisteredDoctorId_IsDocumentedInTheCliReference()
    {
        var documentation = ReadCliReference();

        var undocumented = DoctorCheckIds.All
            .Where(id => !MentionsId(documentation, id))
            .ToList();

        undocumented.ShouldBeEmpty(
            "these doctor ids are registered but absent from docs/cli-reference.md, so the "
            + "operator's index of what `doctor` reports no longer describes the tool: "
            + string.Join(", ", undocumented)
            + ". Document each one in the relevant table or sentence - the fence deliberately does "
            + "not write the prose for you.");
    }

    [Fact]
    public void TheFence_GoesRedOnADocsetThatOmitsARegisteredId()
    {
        // Non-vacuity, asserted rather than argued: the matcher must actually fail to find an id
        // that is not present. Without this, a matcher that silently matched everything would let
        // the fence above pass on any docset at all.
        MentionsId("nothing relevant here", "feature-flags-unknown-key").ShouldBeFalse();
        MentionsId("| `feature-flags-unknown-key` | ... |", "feature-flags-unknown-key").ShouldBeTrue();
    }

    [Fact]
    public void TheInventory_CoversAllThreeSuites()
    {
        // A fence over an empty or single-suite inventory would pass while checking almost nothing.
        DoctorCheckIds.Aggregate.ShouldNotBeEmpty();
        DoctorCheckIds.Config.ShouldNotBeEmpty();
        DoctorCheckIds.Advisories.ShouldNotBeEmpty();
        DoctorCheckIds.All.Count.ShouldBe(
            DoctorCheckIds.Aggregate.Count + DoctorCheckIds.Config.Count + DoctorCheckIds.Advisories.Count);
    }

    /// <summary>
    /// Matches the id as a whole token, so <c>compaction-model</c> is not considered documented by
    /// an occurrence of <c>compaction-model-missing</c>. Substring matching would make the fence
    /// pass on a docset that omits the shorter id entirely.
    /// </summary>
    private static bool MentionsId(string documentation, string id)
        => Regex.IsMatch(documentation, @"(?<![A-Za-z0-9\-])" + Regex.Escape(id) + @"(?![A-Za-z0-9\-])");

    private static string ReadCliReference()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "cli-reference.md");
        File.Exists(path).ShouldBeTrue($"expected the CLI reference at {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
