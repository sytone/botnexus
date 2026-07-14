using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the src &lt;-&gt; tests project mirror
/// convention and the "no root-level test projects" rule (#1959).
///
/// <para><b>Rule 1 — mirror.</b> Every production project at
/// <c>src/&lt;category&gt;/&lt;Name&gt;/&lt;Name&gt;.csproj</c> MUST have a mirrored test
/// project at the SAME relative path under <c>tests/</c>, named with a <c>.Tests</c>
/// suffix — i.e. <c>tests/&lt;category&gt;/&lt;Name&gt;.Tests/&lt;Name&gt;.Tests.csproj</c>
/// (example: <c>src/gateway/BotNexus.Gateway</c> -&gt; <c>tests/gateway/BotNexus.Gateway.Tests</c>).
/// A src project is allowed to lack a mirror ONLY if it appears in
/// <see cref="TestMirrorExemptions"/> with a justification.</para>
///
/// <para><b>Rule 2 — no root-level test projects.</b> Test projects MUST live under a
/// known category folder (<see cref="KnownCategoryFolders"/>), never directly at the
/// <c>tests/</c> root. Existing root-level projects are grandfathered in
/// <see cref="RootLevelTestExemptions"/> (their relocation is sibling PBI #1960's scope,
/// deliberately out of scope here so this fence stays standalone and green today).</para>
///
/// <para><b>Standalone &amp; green-today.</b> The exemption allow-lists are seeded from the
/// ACTUAL current tree so this fence passes as-is, WITHOUT requiring the #1960 cleanup
/// first. It then FAILS the moment a NEW src project is added without a mirror or
/// exemption, or a NEW test project is dropped at the <c>tests/</c> root. It does not
/// move, rename, or delete any existing project.</para>
/// </summary>
public sealed class SrcTestsMirrorArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string SrcRoot => Path.Combine(RepoRoot, "src");

    private static string TestsRoot => Path.Combine(RepoRoot, "tests");

    /// <summary>
    /// Folders directly under <c>tests/</c> that are recognised test categories. A test
    /// project must live under one of these; anything at the <c>tests/</c> root is a
    /// violation (unless grandfathered in <see cref="RootLevelTestExemptions"/>).
    /// </summary>
    private static readonly HashSet<string> KnownCategoryFolders = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "architecture",
        "agent",
        "domain",
        "extensions",
        "gateway",
        "persistence",
        "integration",
        "e2e",
        "scenarios",
        "examples",
    };

    /// <summary>
    /// Src projects that are deliberately allowed to have NO mirrored test project.
    /// Each entry is a repo-relative csproj path (forward slashes) mapped to a one-line
    /// justification. These are interface/DTO/marker/composition-only projects whose
    /// behaviour is exercised through their consumers' test suites, so a dedicated
    /// project-level mirror would add ceremony with no coverage value.
    ///
    /// Adding a NEW src project means EITHER creating its mirror test project OR adding
    /// it here with a justification — that conscious choice is exactly what this fence forces.
    /// </summary>
    private static readonly Dictionary<string, string> TestMirrorExemptions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["src/gateway/BotNexus.Gateway.Abstractions/BotNexus.Gateway.Abstractions.csproj"] =
            "Interface/abstraction-only project (no runtime logic); covered via implementors' tests.",
        ["src/gateway/BotNexus.Gateway.Contracts/BotNexus.Gateway.Contracts.csproj"] =
            "DTO/contract-only project (records + shapes, no behaviour); validated through consumer tests.",
        ["src/gateway/BotNexus.Gateway.Telemetry.Abstractions/BotNexus.Gateway.Telemetry.Abstractions.csproj"] =
            "Telemetry interface/marker-only project (no logic); covered by BotNexus.Gateway.Telemetry.Tests.",
        ["src/extensions/BotNexus.Extensions.Channels.SignalR/BotNexus.Extensions.Channels.SignalR.csproj"] =
            "SignalR channel host/composition project; covered by the BlazorClient test projects and integration suites.",
        ["src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core/BotNexus.Extensions.Channels.SignalR.BlazorClient.Core.csproj"] =
            "Shared Blazor UI component/markup project (no standalone logic); covered by BlazorClient.Tests.",
        ["src/extensions/BotNexus.Extensions.Channels.Telegram/BotNexus.Extensions.Channels.Telegram.csproj"] =
            "Telegram channel adapter (thin transport shim over the Telegram SDK); exercised via integration.",
        ["src/extensions/BotNexus.Extensions.Channels.Tui/BotNexus.Extensions.Channels.Tui.csproj"] =
            "Terminal UI channel (interactive console shell, no unit-testable domain logic).",
    };

    /// <summary>
    /// Test projects currently living directly at the <c>tests/</c> root that are
    /// grandfathered until sibling PBI #1960 relocates them under a category folder.
    /// Each entry is a repo-relative csproj path (forward slashes) with a justification.
    /// New root-level test projects are NOT allowed — this list is closed and shrinks
    /// (never grows) as #1960 proceeds.
    /// </summary>
    private static readonly Dictionary<string, string> RootLevelTestExemptions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // All previously grandfathered root-level test projects were relocated under
        // category folders by #1960. This allow-list is intentionally empty and closed:
        // new root-level test projects are not permitted.
    };

    [Fact]
    public void EverySrcProject_HasMirroredTestProject_OrIsExempt()
    {
        Directory.Exists(SrcRoot).ShouldBeTrue($"src root not found at {SrcRoot}");
        Directory.Exists(TestsRoot).ShouldBeTrue($"tests root not found at {TestsRoot}");

        var violations = new List<string>();

        foreach (var srcCsproj in EnumerateCsproj(SrcRoot))
        {
            var relSrc = ToRepoRelative(srcCsproj);

            if (TestMirrorExemptions.ContainsKey(relSrc))
            {
                continue;
            }

            var expectedTestRel = MirrorTestPath(relSrc);
            var expectedTestAbs = Path.Combine(RepoRoot, expectedTestRel.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(expectedTestAbs))
            {
                violations.Add(
                    $"  src project '{relSrc}' has no mirrored test project.\n" +
                    $"    Expected: '{expectedTestRel}'.\n" +
                    $"    Fix: create that test project, OR add '{relSrc}' to " +
                    "TestMirrorExemptions with a one-line justification.");
            }
        }

        violations.ShouldBeEmpty(
            "src <-> tests mirror rule violated (#1959):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void NoTestProject_LivesAtTestsRoot_OrIsExempt()
    {
        Directory.Exists(TestsRoot).ShouldBeTrue($"tests root not found at {TestsRoot}");

        var violations = new List<string>();

        foreach (var testCsproj in EnumerateCsproj(TestsRoot))
        {
            var relTest = ToRepoRelative(testCsproj);

            // Segments relative to tests/: [<folder...>, <ProjectDir>, <file>.csproj].
            // A root-level project has exactly [<ProjectDir>, <file>.csproj] (its project
            // directory sits directly under tests/, with no category folder in between).
            var relToTests = relTest.Substring("tests/".Length);
            var segments = relToTests.Split('/');

            var isRootLevel = segments.Length == 2;
            if (!isRootLevel)
            {
                // Sanity: the first segment must be a known category folder.
                var category = segments[0];
                if (!KnownCategoryFolders.Contains(category))
                {
                    violations.Add(
                        $"  test project '{relTest}' lives under unknown category folder '{category}'.\n" +
                        $"    Fix: move it under a known category folder ({string.Join(", ", KnownCategoryFolders.OrderBy(f => f))}), " +
                        "or add the folder to KnownCategoryFolders.");
                }

                continue;
            }

            if (RootLevelTestExemptions.ContainsKey(relTest))
            {
                continue;
            }

            violations.Add(
                $"  test project '{relTest}' lives directly at the tests/ root.\n" +
                $"    Fix: move it under a known category folder ({string.Join(", ", KnownCategoryFolders.OrderBy(f => f))}). " +
                "New root-level test projects are not allowed (#1959).");
        }

        violations.ShouldBeEmpty(
            "no-root-level-test-projects rule violated (#1959):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void MirrorExemptions_AreNotStale_EveryEntryPointsAtAnExistingSrcProject()
    {
        // Keep the allow-list honest: an exemption for a project that no longer exists (or
        // has since gained a mirror) is dead weight that should be pruned.
        var stale = new List<string>();

        foreach (var (relSrc, _) in TestMirrorExemptions)
        {
            var abs = Path.Combine(RepoRoot, relSrc.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                stale.Add($"  '{relSrc}' — no such src project (remove this exemption).");
                continue;
            }

            var mirrorAbs = Path.Combine(
                RepoRoot,
                MirrorTestPath(relSrc).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(mirrorAbs))
            {
                stale.Add($"  '{relSrc}' — now HAS a mirror test project; drop the exemption.");
            }
        }

        stale.ShouldBeEmpty("TestMirrorExemptions contains stale entries (#1959):\n" + string.Join("\n", stale));
    }

    [Fact]
    public void RootLevelExemptions_AreNotStale_EveryEntryPointsAtAnExistingRootProject()
    {
        var stale = new List<string>();

        foreach (var (relTest, _) in RootLevelTestExemptions)
        {
            var abs = Path.Combine(RepoRoot, relTest.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                stale.Add($"  '{relTest}' — no such test project (remove this exemption; #1960 may have relocated it).");
            }
        }

        stale.ShouldBeEmpty("RootLevelTestExemptions contains stale entries (#1959):\n" + string.Join("\n", stale));
    }

    [Fact]
    public void Fence_IsNotVacuous_MirrorMappingIsExercised()
    {
        // Guard against the fence passing because it enumerated nothing.
        EnumerateCsproj(SrcRoot).Count.ShouldBeGreaterThan(10,
            "Vacuity guard: expected to enumerate many src projects; found too few — enumeration is broken.");

        // The mapping must transform a known src path to its known mirror path.
        MirrorTestPath("src/gateway/BotNexus.Gateway/BotNexus.Gateway.csproj")
            .ShouldBe("tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj");
        MirrorTestPath("src/agent/BotNexus.Agent.Core/BotNexus.Agent.Core.csproj")
            .ShouldBe("tests/agent/BotNexus.Agent.Core.Tests/BotNexus.Agent.Core.Tests.csproj");
    }

    // ---- helpers ----

    /// <summary>
    /// Maps a repo-relative src csproj path to its expected repo-relative mirror test
    /// csproj path: <c>src/&lt;rest&gt;/&lt;Name&gt;/&lt;Name&gt;.csproj</c> becomes
    /// <c>tests/&lt;rest&gt;/&lt;Name&gt;.Tests/&lt;Name&gt;.Tests.csproj</c>.
    /// </summary>
    private static string MirrorTestPath(string relSrcCsproj)
    {
        var withoutPrefix = relSrcCsproj.Substring("src/".Length);
        var segments = withoutPrefix.Split('/');

        // Last two segments are the project directory and the csproj file (equal names).
        var projectDir = segments[^2];
        var testProjectDir = projectDir + ".Tests";
        var testFile = testProjectDir + ".csproj";

        var categorySegments = segments.Take(segments.Length - 2);
        var prefix = string.Join('/', categorySegments);
        var mid = string.IsNullOrEmpty(prefix) ? testProjectDir : prefix + "/" + testProjectDir;

        return "tests/" + mid + "/" + testFile;
    }

    private static List<string> EnumerateCsproj(string root)
    {
        return Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsUnderBinOrObj(p))
            .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsUnderBinOrObj(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/") || normalized.Contains("/obj/");
    }

    private static string ToRepoRelative(string absolutePath)
    {
        var rel = Path.GetRelativePath(RepoRoot, absolutePath);
        return rel.Replace('\\', '/');
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
