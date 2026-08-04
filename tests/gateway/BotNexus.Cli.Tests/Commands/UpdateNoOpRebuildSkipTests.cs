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

    /// <summary>
    /// A process manager for the ordinary case: a gateway IS running. Stating liveness explicitly
    /// matters since #2772 - the skip path now asks whether a gateway is alive instead of asserting
    /// it from control flow, and an unstubbed <c>IsRunning</c> would silently default to false and
    /// turn every skip case into a start case.
    /// </summary>
    private static IGatewayProcessManager NewProcessManager(bool gatewayRunning = true)
    {
        var pm = Substitute.For<IGatewayProcessManager>();
        pm.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStopResult(true, null));
        pm.StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayStartResult(true, 4242, null));
        pm.IsRunning(Arg.Any<string?>(), Arg.Any<string?>()).Returns(gatewayRunning);
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
    /// Sandbox identity for the throwaway fixture repository. It is deliberately synthetic and
    /// unroutable, so a commit that ever escapes this fixture is immediately traceable to it
    /// rather than masquerading as a legitimate author. A generic <c>Test &lt;test@example.com&gt;</c>
    /// identity is indistinguishable from a real one, which is exactly the hazard issue #2651
    /// describes. This matches the sentinel already used by <c>UpdateCommandGitRunnerTests</c> -
    /// one convention, not two.
    /// </summary>
    private const string SentinelName = "botnexus-test";

    /// <summary>Unroutable sentinel address paired with <see cref="SentinelName"/>.</summary>
    private const string SentinelEmail = "botnexus-test@invalid.local";

    /// <summary>
    /// Per-invocation identity flags. Passing <c>-c</c> on the command line - rather than relying
    /// on a repo-local <c>git config</c> having already run - means no git call this fixture makes
    /// can inherit the ambient user identity, regardless of statement ordering or of a
    /// <c>config</c> step failing.
    /// </summary>
    private static readonly string IdentityFlags =
        $"-c user.name={SentinelName} -c user.email={SentinelEmail} -c commit.gpgsign=false";

    private static string RunGit(string repoRoot, string arguments)
    {
        // Guard + ambient-redirect stripping both live in GitSandboxGuard so the invariant has one
        // definition. See #2632: the hook environment exports GIT_DIR / GIT_WORK_TREE for the
        // caller's live worktree, which silently retargeted this harness at the developer's branch.
        var psi = GitSandboxGuard.CreateSandboxedGit(repoRoot, arguments);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {IdentityFlags} {arguments} failed: {stderr}");
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
        RunGit(root, $"config user.email {SentinelEmail}");
        RunGit(root, $"config user.name {SentinelName}");
        RunGit(root, "config commit.gpgsign false");

        foreach (var relative in TrackedBuildInputs)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "original\n");
        }

        GitSandboxGuard.AssertSandboxRepoPath(root);
        RunGit(root, "add -A");
        RunGit(root, "-c user.name=botnexus-test -c user.email=botnexus-test@invalid.local commit -m initial");

        var gatewayDll = UpdateCommand.ResolveGatewayBinaryPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(gatewayDll)!);
        File.WriteAllText(gatewayDll, "fake assembly");

        return root;
    }

    /// <summary>
    /// Exposes the fixture builder to <c>UpdateFixtureAmbientWorktreeIsolationTests</c>, which pins
    /// (issue #2651) that constructing this repository leaves the ambient worktree's HEAD, status
    /// and git identity untouched, and that it commits under the synthetic sentinel identity.
    /// </summary>
    internal static string CreateFixtureRepositoryForIsolationPin()
        => CreateCleanRepoWithGatewayBinary();

    /// <summary>
    /// Companion teardown for <see cref="CreateFixtureRepositoryForIsolationPin"/>.
    /// </summary>
    internal static void DeleteFixtureRepositoryForIsolationPin(string root)
        => DeleteRepo(root);

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
            await pm.Received(1).StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
            await pm.DidNotReceive().StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await pm.DidNotReceive().StartAsync(Arg.Any<GatewayStartOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteRepo(root);
        }
    }
}
