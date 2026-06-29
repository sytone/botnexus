using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;
using Shouldly;
using System.Diagnostics;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Integration tests for the consolidated git subprocess runner introduced in #1391.
/// These exercise the real <c>protected virtual</c> wrappers (which now all delegate to a
/// single shared <c>RunGitAsync</c>) against a real temporary git repository so the
/// process-spawn / output-capture / parse plumbing is verified end-to-end rather than mocked.
/// They are the behavioural safety net for collapsing five near-identical helpers into one.
/// </summary>
[Collection("AnsiConsole")]
public sealed class UpdateCommandGitRunnerTests : IDisposable
{
    private readonly string _repo;
    private readonly string _isolatedGlobalConfig;
    private readonly bool _gitAvailable;

    public UpdateCommandGitRunnerTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"botnexus-git-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        // Empty per-fixture global config so no developer/CI ~/.gitconfig can leak in.
        _isolatedGlobalConfig = Path.Combine(_repo, ".isolated-gitconfig");
        File.WriteAllText(_isolatedGlobalConfig, string.Empty);
        _gitAvailable = TryInitRepoWithTwoCommits(_repo);
    }

    /// <summary>Exposes the protected git wrappers so a test can drive the real runner.</summary>
    private sealed class GitRunnerProbe(IGatewayProcessManager pm) : UpdateCommand(pm)
    {
        public Task<int> CountCommitsBetweenForTestAsync(string repoRoot, string from, string to, CancellationToken ct)
            => CountCommitsBetweenAsync(repoRoot, from, to, ct);

        public Task<IReadOnlyList<string>> GetCommitSubjectsBetweenForTestAsync(string repoRoot, string from, string to, CancellationToken ct)
            => GetCommitSubjectsBetweenAsync(repoRoot, from, to, ct);
    }

    private static GitRunnerProbe NewProbe()
        => new(Substitute.For<IGatewayProcessManager>());

    [Fact]
    public async Task CountCommitsBetween_ReturnsExpectedCount_OverRealRepo()
    {
        if (!_gitAvailable)
            return; // git not on PATH in this environment; covered by other CI runners.

        var count = await NewProbe().CountCommitsBetweenForTestAsync(_repo, "HEAD~1", "HEAD", CancellationToken.None);

        count.ShouldBe(1);
    }

    [Fact]
    public void SandboxRepo_IsFullyIsolated_AndNotBare()
    {
        if (!_gitAvailable)
            return;

        // Self-defense for #1602: the throwaway repo must be a normal working repo, never bare,
        // and must use the generic sandbox identity - guaranteeing it can never be the donor for
        // a host-repo bare flip or a test@example.com co-author trailer.
        RunGit(_repo, "rev-parse --is-bare-repository").ShouldBe(0);
        File.ReadAllText(Path.Combine(_repo, ".git", "config"))
            .ShouldNotContain("test@example.com");
    }

    [Fact]
    public async Task GetCommitSubjectsBetween_ReturnsSubjectsInOrder_OverRealRepo()
    {
        if (!_gitAvailable)
            return;

        var subjects = await NewProbe().GetCommitSubjectsBetweenForTestAsync(_repo, "HEAD~1", "HEAD", CancellationToken.None);

        subjects.Count.ShouldBe(1);
        subjects[0].ShouldBe("second commit");
    }

    [Fact]
    public async Task CountCommitsBetween_ReturnsZero_OnBadRevisionRange()
    {
        if (!_gitAvailable)
            return;

        // git rev-list exits non-zero / prints nothing for an unknown ref ->
        // the consolidated runner's parse must degrade to 0, not throw.
        var count = await NewProbe().CountCommitsBetweenForTestAsync(_repo, "does-not-exist", "HEAD", CancellationToken.None);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task GetCommitSubjectsBetween_ReturnsEmpty_OnBadRevisionRange()
    {
        if (!_gitAvailable)
            return;

        var subjects = await NewProbe().GetCommitSubjectsBetweenForTestAsync(_repo, "does-not-exist", "HEAD", CancellationToken.None);

        subjects.ShouldBeEmpty();
    }

    [Fact]
    public async Task CountCommitsBetween_ReturnsZero_WhenCancelled()
    {
        if (!_gitAvailable)
            return;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pre-cancelled token -> the runner's cancel path returns a canceled result,
        // and the count helper degrades to 0 rather than throwing.
        var count = await NewProbe().CountCommitsBetweenForTestAsync(_repo, "HEAD~1", "HEAD", cts.Token);

        count.ShouldBe(0);
    }

    private bool TryInitRepoWithTwoCommits(string repo)
    {
        try
        {
            if (RunGit(repo, "init -q") != 0) return false;
            // Sandbox identity is intentionally generic and NON-conflicting with the developer's
            // real identity. It must never resemble the #1602 pollution signature
            // (user.email=test@example.com / user.name=test), so a leaked write cannot be mistaken
            // for - or graft onto - the host repo.
            RunGit(repo, "config user.email botnexus-test@invalid.local");
            RunGit(repo, "config user.name botnexus-test");
            RunGit(repo, "config commit.gpgsign false");

            // Distinct seed filenames (not a.txt/b.txt) so any stray that DOES escape is
            // immediately traceable to this fixture rather than masquerading as host content.
            File.WriteAllText(Path.Combine(repo, "seed1.txt"), "1");
            RunGit(repo, "add seed1.txt");
            if (RunGit(repo, "commit -q -m \"first commit\"") != 0) return false;

            File.WriteAllText(Path.Combine(repo, "seed2.txt"), "2");
            RunGit(repo, "add seed2.txt");
            if (RunGit(repo, "commit -q -m \"second commit\"") != 0) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private int RunGit(string repo, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{repo}\" {args}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Total isolation: a leaked GIT_DIR / GIT_WORK_TREE / GIT_CONFIG_* from a parent process
        // could otherwise redirect these ops at the real repo even though every command uses -C.
        // Strip the dir/identity env entirely (so -C is the only locator) and pin config into the
        // sandbox so the only config that applies is the repo-local config written above.
        psi.Environment["GIT_CONFIG_GLOBAL"] = _isolatedGlobalConfig;
        psi.Environment["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["HOME"] = repo;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var leak in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
            psi.Environment.Remove(leak);

        using var proc = Process.Start(psi);
        if (proc is null) return -1;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repo))
                Directory.Delete(_repo, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
