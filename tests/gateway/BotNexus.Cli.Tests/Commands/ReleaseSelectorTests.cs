using System.Diagnostics;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Contracts.Updates;
using NSubstitute;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public sealed class ReleaseSelectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _remote;
    private readonly string _local;
    private readonly string _isolatedGlobalConfig;
    private readonly bool _gitAvailable;
    private string _firstStableCommit = string.Empty;
    private string _highestStableCommit = string.Empty;
    private string _mainCommit = string.Empty;

    public ReleaseSelectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"botnexus-release-selector-{Guid.NewGuid():N}");
        _source = Path.Combine(_root, "source");
        _remote = Path.Combine(_root, "origin.git");
        _local = Path.Combine(_root, "local");
        Directory.CreateDirectory(_source);
        _isolatedGlobalConfig = Path.Combine(_root, ".isolated-gitconfig");
        File.WriteAllText(_isolatedGlobalConfig, string.Empty);
        _gitAvailable = TryCreateRepositoryGraph();
    }

    private sealed class ResolutionFailureCommand(IGatewayProcessManager processManager)
        : UpdateCommand(processManager)
    {
        public bool CheckoutCalled { get; private set; }

        protected override Task<ReleaseUpdateStatus> ResolveReleaseStatusAsync(
            string repoRoot,
            ReleaseTargetRequest request,
            CancellationToken cancellationToken)
            => Task.FromException<ReleaseUpdateStatus>(
                new ReleaseTargetResolutionException("requested release was not found"));

        protected override Task CheckoutReleaseTargetAsync(
            string repoRoot,
            ResolvedReleaseTarget target,
            CancellationToken cancellationToken)
        {
            CheckoutCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void ToRequest_WithNoSelector_RequestsStableRelease()
    {
        ReleaseSelector.ToRequest(latest: false, version: null).ShouldBe(ReleaseTargetRequest.Stable);
    }

    [Fact]
    public void ToRequest_WithLatest_RequestsDevelopmentTip()
    {
        ReleaseSelector.ToRequest(latest: true, version: null).ShouldBe(ReleaseTargetRequest.Latest);
    }

    [Fact]
    public void ToRequest_WithVersion_RequestsExactRelease()
    {
        var request = ReleaseSelector.ToRequest(latest: false, version: "1.2.3");

        request.Kind.ShouldBe(ReleaseTargetKind.Exact);
        request.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public void ToRequest_WithLatestAndVersion_RejectsMutuallyExclusiveSelectors()
    {
        Action action = () => ReleaseSelector.ToRequest(latest: true, version: "1.2.3");

        Should.Throw<ArgumentException>(action).Message.ShouldContain("cannot be combined");
    }

    [Fact]
    public void InstallAndUpdateCommands_ExposeTheSameReleaseSelectors()
    {
        var verbose = new System.CommandLine.Option<bool>("--verbose");
        var target = new System.CommandLine.Option<string?>("--target");
        var install = new InstallCommand().Build(verbose, target);
        var update = new UpdateCommand(Substitute.For<IGatewayProcessManager>()).Build(verbose, target);
        var check = update.Subcommands.Single(command => command.Name == "check");

        install.Options.Select(option => option.Name).ShouldContain("latest");
        install.Options.Select(option => option.Name).ShouldContain("version");
        update.Options.Select(option => option.Name).ShouldContain("latest");
        update.Options.Select(option => option.Name).ShouldContain("version");
        check.Options.Select(option => option.Name).ShouldContain("latest");
        check.Options.Select(option => option.Name).ShouldContain("version");
    }

    [Fact]
    public async Task Install_WithDefaultSelector_ClonesAndChecksOutHighestStableReleaseThroughRealGit()
    {
        if (!_gitAvailable)
            return;

        var installPath = Path.Combine(_root, "fresh-install");

        var exitCode = await InstallCommand.ExecuteAsync(
            installPath,
            _remote,
            build: false,
            verbose: false,
            ReleaseTargetRequest.Stable,
            CancellationToken.None);

        exitCode.ShouldBe(0);
        ReadCommit(installPath, "HEAD").ShouldBe(_highestStableCommit);
        ReadCommit(installPath, "HEAD").ShouldNotBe(_mainCommit);
    }

    [Fact]
    public async Task ResolveLocal_WithLatest_ResolvesFetchedOriginMainThroughRealGit()
    {
        if (!_gitAvailable)
            return;

        var status = await ReleaseTargetGitResolver.ResolveLocalAsync(
            _local,
            ReleaseTargetRequest.Latest,
            CancellationToken.None);

        status.Target.CommitSha.ShouldBe(_mainCommit);
        status.Target.SourceName.ShouldBe("origin/main");
    }

    [Fact]
    public async Task ResolveLocal_WithExactVersion_ResolvesRequestedTagThroughRealGit()
    {
        if (!_gitAvailable)
            return;

        var status = await ReleaseTargetGitResolver.ResolveLocalAsync(
            _local,
            ReleaseTargetRequest.Exact("1.0.0"),
            CancellationToken.None);

        status.Target.CommitSha.ShouldBe(_firstStableCommit);
        status.Target.SourceName.ShouldBe("v1.0.0");
    }

    [Fact]
    public async Task ResolveLocal_WithAnnotatedStableTag_PeelsTagToCommitThroughRealGit()
    {
        if (!_gitAvailable)
            return;

        var status = await ReleaseTargetGitResolver.ResolveLocalAsync(
            _local,
            ReleaseTargetRequest.Stable,
            CancellationToken.None);

        status.Target.CommitSha.ShouldBe(_highestStableCommit);
        status.Target.SourceName.ShouldBe("v2.0.0");
        status.Target.CommitSha.ShouldNotBe(ReadObjectId(_local, "refs/tags/v2.0.0"));
    }

    [Fact]
    public async Task ResolveLocal_WithMissingExactVersion_FailsThroughRealGit()
    {
        if (!_gitAvailable)
            return;

        Func<Task> action = () => ReleaseTargetGitResolver.ResolveLocalAsync(
            _local,
            ReleaseTargetRequest.Exact("9.9.9"),
            CancellationToken.None);

        var exception = await Should.ThrowAsync<ReleaseTargetResolutionException>(action);
        exception.Message.ShouldContain("v9.9.9");
    }

    [Fact]
    public async Task CheckAndApply_WithInvalidExactVersion_ReturnSameFailureWithoutCheckoutOrGatewayStop()
    {
        if (!_gitAvailable)
            return;

        var processManager = Substitute.For<IGatewayProcessManager>();
        var command = new UpdateCommand(processManager)
        {
            ReleaseRequest = ReleaseTargetRequest.Exact("9.9.9")
        };
        var before = ReadCommit(_local, "HEAD");

        var checkExitCode = await command.CheckAsync(_local, verbose: false, CancellationToken.None);
        var applyExitCode = await command.ExecuteAsync(
            _local,
            Path.Combine(_root, "home"),
            5005,
            verbose: false,
            CancellationToken.None);

        checkExitCode.ShouldBe(2);
        applyExitCode.ShouldBe(checkExitCode);
        ReadCommit(_local, "HEAD").ShouldBe(before);
        await processManager.DidNotReceive().StopAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_WhenRepositoryAlreadyExists_RefusesInsteadOfSilentlyIgnoringSelection()
    {
        if (!_gitAvailable)
            return;

        var before = ReadCommit(_local, "HEAD");

        var exitCode = await InstallCommand.ExecuteAsync(
            _local,
            _remote,
            build: false,
            verbose: false,
            ReleaseTargetRequest.Exact("1.0.0"),
            CancellationToken.None);

        exitCode.ShouldBe(2);
        ReadCommit(_local, "HEAD").ShouldBe(before);
    }

    [Fact]
    public async Task Apply_WhenResolutionFails_DoesNotStopGatewayOrMutateCheckout()
    {
        var processManager = Substitute.For<IGatewayProcessManager>();
        var command = new ResolutionFailureCommand(processManager)
        {
            ReleaseRequest = ReleaseTargetRequest.Exact("9.9.9")
        };

        var exitCode = await command.ExecuteAsync(
            "unused",
            "unused",
            5005,
            verbose: false,
            CancellationToken.None);

        exitCode.ShouldBe(2);
        command.CheckoutCalled.ShouldBeFalse();
        await processManager.DidNotReceive().StopAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private bool TryCreateRepositoryGraph()
    {
        GitSandboxGuard.AssertSandboxRepoPath(_root);
        try
        {
            if (RunGit(_source, "init -q -b main") != 0)
                return false;

            RunGit(_source, $"config user.email {GitSandboxGuard.SentinelEmail}");
            RunGit(_source, $"config user.name {GitSandboxGuard.SentinelName}");
            RunGit(_source, "config commit.gpgsign false");
            RunGit(_source, "config tag.gpgsign false");

            Commit(_source, "stable-one.txt", "stable one", "stable one");
            _firstStableCommit = ReadCommit(_source, "HEAD");
            if (RunGit(_source, "tag v1.0.0") != 0)
                return false;

            Commit(_source, "stable-two.txt", "stable two", "stable two");
            _highestStableCommit = ReadCommit(_source, "HEAD");
            if (RunGit(_source, "tag -a v2.0.0 -m v2.0.0") != 0)
                return false;

            Commit(_source, "main-only.txt", "main only", "main only");
            _mainCommit = ReadCommit(_source, "HEAD");

            if (RunGit(_root, $"clone -q --bare -- \"{_source}\" \"{_remote}\"") != 0)
                return false;
            if (RunGit(_root, $"clone -q -- \"{_remote}\" \"{_local}\"") != 0)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Commit(string repo, string fileName, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, fileName), content);
        GitSandboxGuard.AssertSandboxRepoPath(repo);
        if (RunGit(repo, $"add -- {fileName}") != 0 || RunGit(repo, $"commit -q -m \"{message}\"") != 0)
            throw new InvalidOperationException($"Could not create test commit '{message}'.");
    }

    private string ReadCommit(string repo, string revision)
        => ReadGit(repo, $"rev-parse {revision}");

    private string ReadObjectId(string repo, string revision)
        => ReadGit(repo, $"rev-parse {revision}");

    private string ReadGit(string repo, string arguments)
    {
        var (exitCode, output) = RunGitWithOutput(repo, arguments);
        if (exitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed in the test repository.");
        return output.Trim();
    }

    private int RunGit(string repo, string arguments)
        => RunGitWithOutput(repo, arguments).ExitCode;

    private (int ExitCode, string Output) RunGitWithOutput(string repo, string arguments)
    {
        GitSandboxGuard.AssertSandboxRepoPath(repo);
        var startInfo = GitSandboxGuard.CreateSandboxedGit(repo, arguments);
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = _isolatedGlobalConfig;
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["HOME"] = _root;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, string.Empty);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for Windows git file handles.
        }
    }
}
