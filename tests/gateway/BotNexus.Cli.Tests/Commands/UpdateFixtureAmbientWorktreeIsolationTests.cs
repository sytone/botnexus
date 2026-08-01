using System.Diagnostics;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Regression guard for issue #2651.
///
/// Two distinct hazards are pinned here, and they are pinned in a file that deliberately
/// contains NO mutating git command of its own:
///
/// <list type="number">
///   <item><description>
///   <b>Ambient-worktree isolation (AC2).</b> Building the <c>UpdateNoOpRebuildSkipTests</c>
///   fixture must leave the developer's / agent's live worktree completely untouched -
///   same <c>HEAD</c>, same <c>status --porcelain</c>, same <c>user.email</c>. This already
///   holds on <c>main</c>; the test exists to lock it in, because the failure mode
///   (a test fixture running <c>git init</c>/<c>git commit</c> against the real worktree)
///   is silent, destructive and only visible after the damage is done.
///   </description></item>
///   <item><description>
///   <b>Sentinel identity (AC4).</b> The fixture's throwaway repository must commit as
///   <c>botnexus-test &lt;botnexus-test@invalid.local&gt;</c>. A generic-looking
///   <c>Test &lt;test@example.com&gt;</c> identity is indistinguishable from a legitimate
///   author, so a leaked write cannot be recognised as fixture spill. <c>@invalid.local</c>
///   is unroutable and obviously synthetic. This mirrors the convention already established
///   in <c>UpdateCommandGitRunnerTests</c>.
///   </description></item>
/// </list>
///
/// Every git invocation in this file is strictly READ-ONLY (<c>rev-parse</c>, <c>status</c>,
/// <c>config --get</c>). The mutating fixture lives in <c>UpdateNoOpRebuildSkipTests</c>, which
/// derives its path only from <see cref="Path.GetTempPath"/>.
/// </summary>
public sealed class UpdateFixtureAmbientWorktreeIsolationTests
{
    /// <summary>
    /// Runs a read-only git query and returns trimmed stdout, or <c>null</c> when git exits
    /// non-zero (e.g. an unset config key, which is a legitimate observation, not a failure).
    /// </summary>
    private static string? ReadGit(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout.TrimEnd('\r', '\n') : null;
    }

    /// <summary>
    /// Walks up from the test binary to the directory containing <c>BotNexus.slnx</c>, i.e. the
    /// live worktree the test run itself is executing inside. Used only for read-only observation.
    /// </summary>
    private static string? FindAmbientRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName;
    }

    [Fact]
    public void BuildingTheFixture_LeavesTheAmbientWorktreeCompletelyUnchanged()
    {
        var repoRoot = FindAmbientRepoRoot();
        if (repoRoot is null || ReadGit(repoRoot, "rev-parse --is-inside-work-tree") is not "true")
        {
            // Not running from inside a git worktree (e.g. a packaged CI layout). There is no
            // ambient state to protect, so there is nothing meaningful to assert.
            return;
        }

        var headBefore = ReadGit(repoRoot, "rev-parse HEAD");
        var statusBefore = ReadGit(repoRoot, "status --porcelain");
        var emailBefore = ReadGit(repoRoot, "config --get user.email");
        var nameBefore = ReadGit(repoRoot, "config --get user.name");

        headBefore.ShouldNotBeNull("the ambient worktree must have a resolvable HEAD to pin against");

        var fixtureRoot = UpdateNoOpRebuildSkipTests.CreateFixtureRepositoryForIsolationPin();
        try
        {
            // The fixture must live under the temp directory, never under the live worktree.
            var normalisedFixture = Path.GetFullPath(fixtureRoot);
            var normalisedRepo = Path.GetFullPath(repoRoot);
            normalisedFixture.StartsWith(normalisedRepo, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                $"the fixture repository was created INSIDE the live worktree ('{normalisedFixture}' is under " +
                $"'{normalisedRepo}'). A test fixture must never run git against the real repository - " +
                "that is the destructive failure mode issue #2651 guards against.");

            normalisedFixture.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                .ShouldBeTrue($"the fixture repository must be created under the temp directory, but was at '{normalisedFixture}'");
        }
        finally
        {
            UpdateNoOpRebuildSkipTests.DeleteFixtureRepositoryForIsolationPin(fixtureRoot);
        }

        ReadGit(repoRoot, "rev-parse HEAD").ShouldBe(
            headBefore, "building the test fixture moved the ambient worktree's HEAD");
        ReadGit(repoRoot, "status --porcelain").ShouldBe(
            statusBefore, "building the test fixture changed the ambient worktree's working-tree state");
        ReadGit(repoRoot, "config --get user.email").ShouldBe(
            emailBefore, "building the test fixture overwrote the ambient worktree's git user.email");
        ReadGit(repoRoot, "config --get user.name").ShouldBe(
            nameBefore, "building the test fixture overwrote the ambient worktree's git user.name");
    }

    [Fact]
    public void FixtureRepository_CommitsUnderTheSyntheticSentinelIdentity()
    {
        var fixtureRoot = UpdateNoOpRebuildSkipTests.CreateFixtureRepositoryForIsolationPin();
        try
        {
            var author = ReadGit(fixtureRoot, "log -1 --format=%an <%ae>");

            author.ShouldBe(
                "botnexus-test <botnexus-test@invalid.local>",
                "the fixture must commit under an obviously synthetic, unroutable sentinel identity. " +
                "A generic 'Test <test@example.com>' author is indistinguishable from a real one, so a " +
                "leaked commit cannot be traced back to this fixture (issue #2651). Use the same sentinel " +
                "as UpdateCommandGitRunnerTests: botnexus-test <botnexus-test@invalid.local>.");
        }
        finally
        {
            UpdateNoOpRebuildSkipTests.DeleteFixtureRepositoryForIsolationPin(fixtureRoot);
        }
    }

    [Fact]
    public void FixtureRepository_ResolvesTheSentinelIdentityEvenWithoutRepoLocalConfig()
    {
        var fixtureRoot = UpdateNoOpRebuildSkipTests.CreateFixtureRepositoryForIsolationPin();
        try
        {
            // The identity must be pinned per-invocation (-c flags), not merely written into the
            // repo-local config, so it can never depend on the ordering of the config calls nor
            // fall back to ambient/global identity.
            ReadGit(fixtureRoot, "config --local --get user.email")
                .ShouldBe("botnexus-test@invalid.local");
            ReadGit(fixtureRoot, "config --local --get user.name")
                .ShouldBe("botnexus-test");

            // And the gateway binary the skip decision points at really exists.
            File.Exists(UpdateCommand.ResolveGatewayBinaryPath(fixtureRoot)).ShouldBeTrue();
        }
        finally
        {
            UpdateNoOpRebuildSkipTests.DeleteFixtureRepositoryForIsolationPin(fixtureRoot);
        }
    }
}
