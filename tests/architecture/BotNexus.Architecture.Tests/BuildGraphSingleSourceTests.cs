using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences build and test invocations against naming the solution file (#2842).
/// </summary>
/// <remarks>
/// <para>
/// The traversal projects (<c>dirs.proj</c>, <c>src/dirs.proj</c>, <c>tests/dirs.proj</c>) are the
/// single definition of the build graph. <c>BotNexus.slnx</c> is a second spelling of the same
/// set, and two spellings of one value is the defect family behind #2793 and #2796: whichever is
/// edited second drifts silently.
/// </para>
/// <para>
/// Measured before the migration: the solution listed 106 projects against 105 in the traversals,
/// and 17 call sites across CI, the Dockerfile, the remote runner and six scripts still drove the
/// solution file. A project added to one graph was not necessarily built or tested by the other.
/// </para>
/// <para>
/// Repo-root MARKER usages are deliberately NOT fenced. A marker only needs a file that reliably
/// exists at the repository root, so <c>File.Exists(Path.Combine(dir, "BotNexus.slnx"))</c> stays
/// correct regardless of which project drives the build.
/// </para>
/// </remarks>
public class BuildGraphSingleSourceTests
{
    [Fact]
    public void BuildAndTestInvocations_DoNotNameTheSolutionFile()
    {
        var repoRoot = FindRepoRoot();
        // .cs is included because the ONE call site this fence originally missed was a C# test
        // fixture, not a script: ExtensionBootFixture shelled out to `dotnet build BotNexus.slnx
        // -c Release` from inside the test phase (#2910). It cost 319.3s of a 443s test phase -
        // 72% - rebuilding 57 test projects in Release that nothing deploys or loads. A fence
        // that scans only scripts cannot see a build invoked from compiled code, and this is
        // exactly where the expensive drift hid.
        var extensions = new[] { ".ps1", ".sh", ".yml", ".yaml", ".proj", ".props", ".cs" };

        // Pattern 1: 'dotnet build|test|... <something> BotNexus.slnx' on ONE line. Covers every
        // script, workflow and Dockerfile call site.
        var invocation = new Regex(
            @"dotnet\s+(build|test|restore|list|publish|pack)\b[^\r\n]*BotNexus\.slnx",
            RegexOptions.IgnoreCase);

        // Pattern 2: the SPLIT-ARGUMENT form, which pattern 1 structurally cannot see and which is
        // how the #2910 defect hid for so long. A C# process launch passes the executable and its
        // arguments as separate string literals on separate lines:
        //
        //     await ProcessRunner.RunAsync(
        //         "dotnet",
        //         "build BotNexus.slnx --configuration Release ...",
        //
        // The verb and the solution name share a line, but the word 'dotnet' does not, so a regex
        // anchored on 'dotnet' matches nothing. This was mutation-proven: reverting the fixture to
        // the solution build left pattern 1 GREEN. A fence that cannot fail on the very defect it
        // was written for is decoration, so match the verb+solution pair without requiring 'dotnet'.
        var splitArgInvocation = new Regex(
            @"^\s*""(build|test|restore|list|publish|pack)\s[^\r\n]*BotNexus\.slnx",
            RegexOptions.IgnoreCase);

        var violations = new List<string>();
        var scanned = 0;
        foreach (var file in EnumerateCandidateFiles(repoRoot, extensions))
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Comments describing the history are fine; only live invocations are fenced.
                if (trimmed.StartsWith('#') || trimmed.StartsWith("//"))
                    continue;

                if (invocation.IsMatch(line) || splitArgInvocation.IsMatch(line))
                    violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}");
            }
        }

        // Non-vacuity: the first version of this fence resolved the root to tests/ and scanned a
        // subtree with no invocations in it, so it passed with a real violation present. A fence
        // that finds nothing because it looked nowhere is worse than no fence at all.
        scanned.ShouldBeGreaterThan(50,
            $"Expected to scan the repository's build scripts, but only found {scanned} candidate " +
            $"files under '{repoRoot}'. The repo-root resolution is probably wrong.");

        violations.ShouldBeEmpty(
            "Build and test invocations must target a traversal project (dirs.proj, src/dirs.proj, " +
            "tests/dirs.proj), not BotNexus.slnx. The traversals are the single definition of the " +
            "build graph; naming the solution file reintroduces a second spelling that drifts." +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string repoRoot, string[] extensions)
    {
        foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}.squad{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            // NOT dirs.proj: tests/dirs.proj and src/dirs.proj exist too, so a walk-up keyed on
            // that name stops at tests/ and silently scans a subtree containing no invocations -
            // which made the first version of this fence pass while a violation was present.
            // Directory.Packages.props exists only at the repository root.
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
