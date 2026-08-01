using System.Diagnostics;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using NSubstitute;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Pins the observable decisions of the #2493 no-op-update fast path.
///
/// The dangerous failure mode here is SKIPPING a build that was needed - that produces a stale
/// gateway binary after a "successful" update. So every test below asserts the DECISION
/// (skip vs build), never wall-clock time, and the bulk of them pin the CONSERVATIVE direction:
/// a changed build input must still cause a build.
/// </summary>
[Collection("AnsiConsole")]
public sealed class UpdateNoOpRebuildSkipTests
{
    /// <summary>
    /// Exposes the protected skip decision and lets a test declare whether the pull moved HEAD.
    /// <c>GetWorkingTreeCleanlinessAsync</c> is deliberately NOT overridden - it runs the real
    /// <c>git status</c> against a real temporary repository, so these tests exercise the actual
    /// porcelain parsing rather than a stub of it.
    /// </summary>
    private sealed class SkipProbeCommand(IGatewayProcessManager processManager, bool pullWasNoOp)
        : UpdateCommand(processManager)
    {
        public Task<bool> CanSkipForTestAsync(string repoRoot, CancellationToken cancellationToken)
        {
            LastPullWasNoOp = pullWasNoOp;
            return CanSkipRebuildAsync(repoRoot, cancellationToken);
        }
    }

    /// <summary>
    /// Drives the full <c>ExecuteAsync</c> pipeline with a scripted pull result so a test can
    /// observe whether the gateway was bounced and the build ran at all.
    /// </summary>
    private sealed class ScriptedPipelineCommand(
        IGatewayProcessManager processManager,
        bool pullWasNoOp,
        UpdateCommand.WorkingTreeCleanliness cleanliness)
        : UpdateCommand(processManager)
    {
        public int BuildAndDeployCalls { get; private set; }

        protected override Task<int> RunGitPullStepAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
        {
            LastPullWasNoOp = pullWasNoOp;
            return Task.FromResult(0);
        }

        protected override Task<WorkingTreeCleanliness> GetWorkingTreeCleanlinessAsync(
            string repoRoot, CancellationToken cancellationToken)
            => Task.FromResult(cleanliness);

        protected override Task<int> RunBuildAndDeployAsync(
            string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
        {
            BuildAndDeployCalls++;
            return Task.FromResult(0);
        }
    }

    private static IGatewayProcessManager NewProcessManager()
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, null));
        pm.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(true, 4242, null));
        return pm;
    }

    /// <summary>
    /// Every build input the issue requires the skip decision to be conservative about. These are
    /// tracked files, so a modification to any of them shows up in <c>git status --porcelain</c>
    /// and must force a build.
    /// </summary>
    public static readonly string[] TrackedBuildInputs =
    [
        "src/gateway/BotNexus.Gateway.Api/Program.cs",
        "src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "packages.lock.json",
        "src/gateway/BotNexus.Gateway.Api/TargetFramework.props"
    ];

    public static TheoryData<string> BuildInputCases()
    {
        var data = new TheoryData<string>();
        foreach (var input in TrackedBuildInputs)
            data.Add(input);
        return data;
    }

    /// <summary>
    /// Guard (#2632): a repo-creating harness must never stage or commit outside its sandbox root.
    /// When this suite runs under the pre-commit hook, git exports GIT_DIR / GIT_WORK_TREE for the
    /// caller's live worktree. A harness that locates its repo only via <c>WorkingDirectory</c>
    /// therefore had its `add -A` / `commit` retargeted at the developer's branch, producing the
    /// tree-deleting "initial" commit reported in #2632. Every git call that can author a commit
    /// asserts its target path is under <see cref="Path.GetTempPath"/> first.
    /// </summary>
    internal static string AssertSandboxRepoPath(string repoRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException(
                $"Sandbox guard: refusing git write against '{full}' because it is not under the temp sandbox root '{root}'.");
        return full;
    }

    private static string RunGit(string repoRoot, string arguments)
    {
        AssertSandboxRepoPath(repoRoot);
        // `-C` is the only repo locator, and the inherited GIT_DIR / GIT_WORK_TREE / identity
        // vars are stripped, so an ambient hook environment cannot redirect this at a real repo.
        var psi = new ProcessStartInfo("git", $"-C \"{repoRoot}\" {arguments}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var leak in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
            psi.Environment.Remove(leak);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {arguments} failed: {stderr}");
        return stdout;
    }

    /// <summary>
    /// Creates a committed, clean git repository containing one file of every build-input kind
    /// the skip decision has to be conservative about, plus the gateway binary that
    /// <c>ResolveGatewayBinaryPath</c> points at.
    /// </summary>
    private static string CreateCleanRepoWithGatewayBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bn-2493-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        RunGit(root, "init --initial-branch=main");
        // Sandbox identity is intentionally generic and NON-conflicting with the developer's
        // real identity. It must never resemble the #1602 pollution signature
        // (user.email=test@example.com / user.name=test), so a leaked write cannot be mistaken
        // for - or graft onto - the host repo. Same convention as UpdateCommandGitRunnerTests.
        RunGit(root, "config user.email botnexus-test@invalid.local");
        RunGit(root, "config user.name botnexus-test");
        RunGit(root, "config commit.gpgsign false");

        foreach (var relative in TrackedBuildInputs)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "original\n");
        }

        AssertSandboxRepoPath(root);
        RunGit(root, "add -A");
        RunGit(root, "-c user.name=botnexus-test -c user.email=botnexus-test@invalid.local commit -m initial");

        var gatewayDll = UpdateCommand.ResolveGatewayBinaryPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(gatewayDll)!);
        File.WriteAllText(gatewayDll, "fake assembly");

        return root;
    }

    private static void DeleteRepo(string root)
    {
        try
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Windows can still hold a handle on .git pack files; the temp dir is disposable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---------------------------------------------------------------------------------------
    // CONSERVATIVE DIRECTION: a changed input must still build.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BuildInputCases))]
    public async Task CanSkipRebuild_IsFalse_WhenATrackedBuildInputIsModified(string relativePath)
    {
        var root = CreateCleanRepoWithGatewayBinary();
        try
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(full, "modified by test\n");

            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: true);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeFalse(
                $"a modification to '{relativePath}' can change build output, so the update must rebuild");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Theory]
    [MemberData(nameof(BuildInputCases))]
    public async Task CanSkipRebuild_IsFalse_WhenATrackedBuildInputIsDeleted(string relativePath)
    {
        var root = CreateCleanRepoWithGatewayBinary();
        try
        {
            File.Delete(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: true);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeFalse($"deleting '{relativePath}' changes build output, so the update must rebuild");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Fact]
    public async Task CanSkipRebuild_IsFalse_WhenPullMovedHead()
    {
        var root = CreateCleanRepoWithGatewayBinary();
        try
        {
            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: false);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeFalse("new commits were pulled, so a clean working tree is irrelevant");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Fact]
    public async Task CanSkipRebuild_IsFalse_WhenGatewayBinaryIsMissing()
    {
        var root = CreateCleanRepoWithGatewayBinary();
        try
        {
            File.Delete(UpdateCommand.ResolveGatewayBinaryPath(root));

            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: true);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeFalse("there is no binary to leave running, so it has to be built");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Fact]
    public async Task CanSkipRebuild_IsFalse_WhenGitStatusCannotBeRead()
    {
        // A directory that is not a git repository at all: `git status` exits non-zero, which the
        // implementation maps to Unknown, which must be treated exactly like Dirty.
        var root = Path.Combine(Path.GetTempPath(), $"bn-2493-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var gatewayDll = UpdateCommand.ResolveGatewayBinaryPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(gatewayDll)!);
            File.WriteAllText(gatewayDll, "fake assembly");

            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: true);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeFalse("an unreadable git status must be treated as dirty");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Fact]
    public async Task ExecuteAsync_StopsAndBuilds_WhenWorkingTreeIsDirty()
    {
        var root = CreateCleanRepoWithGatewayBinary();
        var pm = NewProcessManager();
        try
        {
            var cmd = new ScriptedPipelineCommand(pm, pullWasNoOp: true, UpdateCommand.WorkingTreeCleanliness.Dirty);

            await cmd.ExecuteAsync(root, root, port: FreePort(), verbose: false, CancellationToken.None);

            cmd.BuildAndDeployCalls.ShouldBe(1);
            await pm.Received(1).StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The feature must not be inert: a genuine no-op really does skip.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CanSkipRebuild_IsTrue_WhenNothingChangedAndBinaryExists()
    {
        var root = CreateCleanRepoWithGatewayBinary();
        try
        {
            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: true);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeTrue("HEAD did not move, the tree is clean and the binary exists");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Fact]
    public async Task CanSkipRebuild_IsTrue_WhenOnlyUntrackedFilesArePresent()
    {
        var root = CreateCleanRepoWithGatewayBinary();
        try
        {
            File.WriteAllText(Path.Combine(root, "scratch.log"), "not a build input");

            var cmd = new SkipProbeCommand(NewProcessManager(), pullWasNoOp: true);

            var canSkip = await cmd.CanSkipForTestAsync(root, CancellationToken.None);

            canSkip.ShouldBeTrue("untracked files are not compiled into any project");
        }
        finally
        {
            DeleteRepo(root);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsZeroWithoutTouchingGateway_WhenNothingChanged()
    {
        var root = CreateCleanRepoWithGatewayBinary();
        var pm = NewProcessManager();
        try
        {
            var cmd = new ScriptedPipelineCommand(pm, pullWasNoOp: true, UpdateCommand.WorkingTreeCleanliness.Clean);

            var exitCode = await cmd.ExecuteAsync(root, root, port: FreePort(), verbose: false, CancellationToken.None);

            exitCode.ShouldBe(0);
            cmd.BuildAndDeployCalls.ShouldBe(0);
            await pm.DidNotReceive().StopAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await pm.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteRepo(root);
        }
    }
}
