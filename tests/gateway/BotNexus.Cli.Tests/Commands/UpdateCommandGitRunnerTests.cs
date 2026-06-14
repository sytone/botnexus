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
    private readonly bool _gitAvailable;

    public UpdateCommandGitRunnerTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"botnexus-git-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
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

    private static bool TryInitRepoWithTwoCommits(string repo)
    {
        try
        {
            if (RunGit(repo, "init -q") != 0) return false;
            RunGit(repo, "config user.email test@example.com");
            RunGit(repo, "config user.name test");
            RunGit(repo, "config commit.gpgsign false");

            File.WriteAllText(Path.Combine(repo, "a.txt"), "1");
            RunGit(repo, "add a.txt");
            if (RunGit(repo, "commit -q -m \"first commit\"") != 0) return false;

            File.WriteAllText(Path.Combine(repo, "b.txt"), "2");
            RunGit(repo, "add b.txt");
            if (RunGit(repo, "commit -q -m \"second commit\"") != 0) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int RunGit(string repo, string args)
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
