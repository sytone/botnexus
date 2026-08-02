using System.Diagnostics;
using System.Text.Json;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins the observable project set of the Microsoft.Build.Traversal <c>dirs.proj</c> files (#2575).
///
/// These tests ask MSBuild itself what the traversal evaluated to (<c>-getItem:ProjectReference</c>)
/// rather than re-implementing the glob in C#. Re-globbing in the test would make the assertion
/// vacuous: it would pass by construction no matter what the traversal file actually said.
///
/// They assert the SET, never wall-clock duration. A timing assertion is inherently flaky and
/// proves nothing about which projects were built.
/// </summary>
public sealed class TraversalProjectSetTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, BuildCommand.SolutionFileName)))
            dir = dir.Parent;

        dir.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return dir!.FullName;
    }

    /// <summary>
    /// Evaluates a traversal project with MSBuild and returns the absolute paths it will build.
    /// Deliberately has no try/catch and no early return: if MSBuild fails, the test fails.
    /// </summary>
    private static IReadOnlyList<string> EvaluateProjectReferences(
        string repoRoot, string relativeProject, params string[] properties)
    {
        var args = $"msbuild \"{relativeProject}\" -getItem:ProjectReference --nologo"
                   + string.Concat(properties.Select(p => $" /p:{p}"));

        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        proc.ShouldNotBeNull($"failed to start dotnet {args}");
        var stdout = proc!.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        proc.ExitCode.ShouldBe(0, $"dotnet {args} failed:\n{stdout}\n{stderr}");

        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement
            .GetProperty("Items")
            .GetProperty("ProjectReference")
            .EnumerateArray()
            .Select(e => Path.GetFullPath(e.GetProperty("FullPath").GetString()!))
            .ToList();
    }

    private static IReadOnlyList<string> ProjectsOnDisk(string dir) =>
        Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// AC1/AC3/AC8. The deployment traversal must cover EVERY project on disk under <c>src/</c>.
    ///
    /// This is the non-vacuity fence: drop a new .csproj under src/ and, if the traversal ever
    /// stopped being a wildcard (say someone replaced it with a hand-maintained list), the new
    /// project would appear in <c>ProjectsOnDisk</c> but not in MSBuild's evaluated set and this
    /// test would fail. #2376 shipped a container with zero extensions from exactly that shape
    /// of drift.
    /// </summary>
    [Fact]
    public void SrcTraversal_BuildsEveryProjectUnderSrc_AndNothingElse()
    {
        var repoRoot = FindRepoRoot();

        var evaluated = EvaluateProjectReferences(repoRoot, "src/dirs.proj");
        var onDisk = ProjectsOnDisk(Path.Combine(repoRoot, "src"));

        onDisk.Count.ShouldBeGreaterThan(20);

        var missing = onDisk.Except(evaluated, StringComparer.OrdinalIgnoreCase).ToList();
        missing.ShouldBeEmpty(
            "src/dirs.proj must pick up every project under src/ by wildcard; these were not "
            + "discovered: " + string.Join(", ", missing));

        // No leakage the other way: nothing outside src/ may enter the deployment graph.
        var leaked = evaluated
            .Where(p => !p.StartsWith(Path.Combine(repoRoot, "src") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        leaked.ShouldBeEmpty("the deployment traversal leaked projects outside src/: "
            + string.Join(", ", leaked));

        // The closure that #2376 and #2398 are about.
        evaluated.ShouldContain(p => p.EndsWith("BotNexus.Gateway.Api.csproj", StringComparison.Ordinal));
        evaluated.ShouldContain(p => p.EndsWith(
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.csproj", StringComparison.Ordinal));
        evaluated.Count(p => p.Contains($"{Path.DirectorySeparatorChar}extensions{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)).ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// AC7. The test traversal builds the test graph and no more - no <c>tests/</c> project may
    /// appear in the deployment traversal, and no <c>src/</c> project may appear directly in the
    /// test traversal's own set.
    /// </summary>
    [Fact]
    public void TestsTraversal_BuildsEveryProjectUnderTests_AndNoSrcProjects()
    {
        var repoRoot = FindRepoRoot();

        var evaluated = EvaluateProjectReferences(repoRoot, "tests/dirs.proj");
        var onDisk = ProjectsOnDisk(Path.Combine(repoRoot, "tests"));

        onDisk.Count.ShouldBeGreaterThan(20);

        var missing = onDisk.Except(evaluated, StringComparer.OrdinalIgnoreCase).ToList();
        missing.ShouldBeEmpty("tests/dirs.proj must pick up every project under tests/: "
            + string.Join(", ", missing));

        var leaked = evaluated
            .Where(p => !p.StartsWith(Path.Combine(repoRoot, "tests") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        leaked.ShouldBeEmpty("the test traversal leaked projects outside tests/: "
            + string.Join(", ", leaked));
    }

    /// <summary>
    /// AC7 the other direction: the deployment traversal must never evaluate a test project.
    /// Building the 55 projects under tests/ is precisely what #2493 removed.
    /// </summary>
    [Fact]
    public void SrcTraversal_ContainsNoTestProjects()
    {
        var repoRoot = FindRepoRoot();

        var evaluated = EvaluateProjectReferences(repoRoot, "src/dirs.proj");

        evaluated.ShouldNotBeEmpty();
        evaluated.ShouldAllBe(p => !p.Contains(
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AC4. <c>SkipCli=true</c> must still remove BotNexus.Cli from the evaluated set: it is the
    /// process running <c>botnexus update</c> and its own output assembly is locked on Windows.
    /// Asserts both directions so the test cannot pass by the project simply not existing.
    /// </summary>
    [Fact]
    public void SrcTraversal_SkipCli_RemovesOnlyTheCliProject()
    {
        var repoRoot = FindRepoRoot();

        var withCli = EvaluateProjectReferences(repoRoot, "src/dirs.proj");
        var withoutCli = EvaluateProjectReferences(repoRoot, "src/dirs.proj", "SkipCli=true");

        withCli.ShouldContain(p => p.EndsWith("BotNexus.Cli.csproj", StringComparison.Ordinal));
        withoutCli.ShouldNotContain(p => p.EndsWith("BotNexus.Cli.csproj", StringComparison.Ordinal));

        // Exactly one project removed - SkipCli must not narrow the deployment closure further.
        // Skipping wrongly is the dangerous direction: it leaves a stale deployed binary.
        withCli.Except(withoutCli, StringComparer.OrdinalIgnoreCase).Count().ShouldBe(1);
        withoutCli.Count.ShouldBe(withCli.Count - 1);
    }

    /// <summary>
    /// AC1/AC2. The root traversal composes the two directory traversals, so "build everything"
    /// can never drift from src + tests.
    /// </summary>
    [Fact]
    public void RootTraversal_ComposesSrcAndTests_AndDeployProjIsGone()
    {
        var repoRoot = FindRepoRoot();

        var evaluated = EvaluateProjectReferences(repoRoot, "dirs.proj");

        evaluated.ShouldContain(Path.GetFullPath(Path.Combine(repoRoot, "src", "dirs.proj")));
        evaluated.ShouldContain(Path.GetFullPath(Path.Combine(repoRoot, "tests", "dirs.proj")));

        File.Exists(Path.Combine(repoRoot, "BotNexus.Deploy.proj"))
            .ShouldBeFalse("BotNexus.Deploy.proj must be deleted, not left orphaned (#2575 AC2)");
    }

    /// <summary>
    /// Writes a control traversal next to the real one, identical except that it has NO Exclude,
    /// evaluates both, and returns (withExclude, withoutExclude).
    ///
    /// The control file must live in the SAME directory as the real one, because the glob is
    /// anchored on <c>$(MSBuildThisFileDirectory)</c>. It is a <c>.proj</c>, not a <c>.csproj</c>,
    /// so it can never enter the set it is measuring.
    /// </summary>
    private static (IReadOnlyList<string> WithExclude, IReadOnlyList<string> WithoutExclude)
        EvaluateWithAndWithoutBinObjExclude(string repoRoot, string dir)
    {
        var control = Path.Combine(repoRoot, dir, "dirs.binobj-control.tmp.proj");
        File.WriteAllText(control,
            "<Project Sdk=\"Microsoft.Build.Traversal\">\n"
            + "  <ItemGroup>\n"
            + "    <ProjectReference Include=\"$(MSBuildThisFileDirectory)**\\*.csproj\" />\n"
            + "  </ItemGroup>\n"
            + "</Project>\n");
        try
        {
            return (EvaluateProjectReferences(repoRoot, $"{dir}/dirs.proj"),
                    EvaluateProjectReferences(repoRoot, $"{dir}/dirs.binobj-control.tmp.proj"));
        }
        finally
        {
            File.Delete(control);
        }
    }

    /// <summary>
    /// #2666. The bin/obj Exclude added to <c>tests/dirs.proj</c> is a pure walk-narrowing: it
    /// removes thousands of concurrently-churning <c>bin/</c> and <c>obj/</c> directories from
    /// MSBuild's recursive directory walk, and it must NOT remove a single project.
    ///
    /// Asserted by differential evaluation against a control traversal that has no Exclude, so
    /// the test cannot pass by re-implementing the glob. Both directions are asserted: an
    /// Exclude that dropped a real project, or one that somehow ADDED one, fails here.
    /// </summary>
    [Fact]
    public void TestsTraversal_BinObjExclude_DoesNotChangeTheEvaluatedSet()
    {
        var repoRoot = FindRepoRoot();

        var (withExclude, withoutExclude) = EvaluateWithAndWithoutBinObjExclude(repoRoot, "tests");

        withoutExclude.ShouldNotBeEmpty("the control traversal must find projects at all");
        withExclude.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(withoutExclude.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                "the bin/obj exclude must be a strict no-op on the evaluated set: no .csproj is "
                + "ever emitted into bin/ or obj/, so excluding those directories may only remove "
                + "racing directories from the walk, never a project");

        // Anchors the set to what is actually on disk, so the equality above cannot be satisfied
        // by both sides being empty (the exact #2666 failure mode).
        withExclude.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(ProjectsOnDisk(Path.Combine(repoRoot, "tests")));
    }

    /// <summary>
    /// #2666, the <c>src/</c> half. Same invariant, and additionally proves the Exclude composes
    /// with the existing <c>SkipCli</c> Remove rather than colliding with it.
    /// </summary>
    [Fact]
    public void SrcTraversal_BinObjExclude_DoesNotChangeTheEvaluatedSet()
    {
        var repoRoot = FindRepoRoot();

        var (withExclude, withoutExclude) = EvaluateWithAndWithoutBinObjExclude(repoRoot, "src");

        withoutExclude.ShouldNotBeEmpty("the control traversal must find projects at all");
        withExclude.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(withoutExclude.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                "the bin/obj exclude must be a strict no-op on the deployment closure");

        withExclude.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(ProjectsOnDisk(Path.Combine(repoRoot, "src")));
    }

    /// <summary>
    /// #2666 regression fence on the FILES, not just the evaluated set. <c>ProjectReference</c> is
    /// an explicit item, so <c>DefaultItemExcludes</c> does not apply to it: if someone deletes
    /// the Exclude the glob silently goes back to walking every bin/obj directory and the
    /// intermittent empty-set failure returns. That regression is invisible to a set assertion on
    /// a quiescent dev machine, so it is pinned textually here.
    /// </summary>
    [Fact]
    public void BothDirectoryTraversals_ExcludeBinAndObjFromTheProjectGlob()
    {
        var repoRoot = FindRepoRoot();

        foreach (var rel in new[] { "src/dirs.proj", "tests/dirs.proj" })
        {
            var text = File.ReadAllText(Path.Combine(repoRoot, rel));

            text.Contains("Exclude=", StringComparison.Ordinal)
                .ShouldBeTrue($"{rel} must exclude bin/obj from the ProjectReference glob (#2666)");
            text.Contains(@"**\bin\**\*.csproj", StringComparison.Ordinal)
                .ShouldBeTrue($"{rel} must exclude bin/ from the ProjectReference glob (#2666)");
            text.Contains(@"**\obj\**\*.csproj", StringComparison.Ordinal)
                .ShouldBeTrue($"{rel} must exclude obj/ from the ProjectReference glob (#2666)");
        }
    }

    /// <summary>
    /// AC1. All three traversal files must actually use the Traversal SDK - not a hand-rolled
    /// Project that happens to be named dirs.proj.
    /// </summary>
    [Fact]
    public void AllDirsProjFiles_UseTheTraversalSdk()
    {
        var repoRoot = FindRepoRoot();

        foreach (var rel in new[] { "dirs.proj", "src/dirs.proj", "tests/dirs.proj" })
        {
            var path = Path.Combine(repoRoot, rel);
            File.Exists(path).ShouldBeTrue($"{rel} must exist");
            File.ReadAllText(path).ShouldContain("Sdk=\"Microsoft.Build.Traversal\"");
        }

        File.ReadAllText(Path.Combine(repoRoot, "global.json"))
            .ShouldContain("Microsoft.Build.Traversal");
    }
}
