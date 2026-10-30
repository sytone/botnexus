using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Contracts.Updates;
using NSubstitute;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public sealed class UpdateDeploymentIdentityTests : IDisposable
{
    private const string OldCommit = "1111111111111111111111111111111111111111";
    private const string NewCommit = "2222222222222222222222222222222222222222";
    private readonly string _root = UpdateNoOpRebuildSkipTests.CreateFixtureRepositoryForIsolationPin();
    private string Binary => UpdateCommand.ResolveGatewayBinaryPath(_root);

    public UpdateDeploymentIdentityTests()
    {
        UpdateDeploymentIdentity.Invalidate(Binary);
        File.WriteAllText(Binary, "old gateway output");
    }

    private sealed class Pipeline(IGatewayProcessManager manager) : UpdateCommand(manager)
    {
        internal string SourceCommit { get; set; } = NewCommit;
        internal int BuildResult { get; set; }
        internal int RestartResult { get; set; }
        internal int BuildCalls { get; private set; }
        internal int RestartCalls { get; private set; }
        internal bool CancelBuild { get; set; }
        internal bool MoveSource { get; set; }
        internal Func<string, CancellationToken, Task>? DuringBuild { get; set; }
        internal TaskCompletionSource BuildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override string GetCommitSha(string repoRoot) => SourceCommit;
        protected override Task<int> RunGitPullStepAsync(string repoRoot, bool verbose, CancellationToken cancellationToken)
        {
            LastPullWasNoOp = !MoveSource;
            SourceCommit = NewCommit;
            MoveSource = false;
            return Task.FromResult(0);
        }
        protected override Task<bool> WaitForPortFreeAsync(int port, CancellationToken cancellationToken) => Task.FromResult(true);
        protected override async Task<int> RunBuildAndDeployAsync(string repoRoot, string home, bool verbose, CancellationToken cancellationToken)
        {
            BuildCalls++;
            BuildEntered.TrySetResult();
            if (DuringBuild is not null)
                await DuringBuild(repoRoot, cancellationToken);
            if (CancelBuild)
                return 130;
            if (BuildResult == 0)
            {
                var binary = ResolveGatewayBinaryPath(repoRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(binary) ?? throw new InvalidOperationException());
                File.WriteAllText(binary, "new gateway output");
            }
            return BuildResult;
        }
        protected override Task<int> RunRestartAsync(string home, int port, string repoRoot, CancellationToken cancellationToken)
        {
            RestartCalls++;
            return Task.FromResult(RestartResult);
        }
        protected override Task<ReleaseUpdateStatus> ResolveReleaseStatusAsync(string repoRoot, ReleaseTargetRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ReleaseUpdateStatus(new ReleaseIdentity(null, SourceCommit),
                ReleaseTargetResolver.Resolve(ReleaseTargetRequest.Latest, [], new DevelopmentTipReference("origin/main", NewCommit))));
    }

    private static Pipeline NewPipeline()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        manager.StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new GatewayStopResult(true, "not running", GatewayStopOutcome.NotRunning));
        manager.IsRunning(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
        return new Pipeline(manager);
    }

    [Fact]
    public async Task ExecuteAsync_FailedBuildThenUnchangedRetry_ReconcilesOldBinary()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, OldCommit, CancellationToken.None);
        var first = NewPipeline();
        first.SourceCommit = OldCommit;
        first.MoveSource = true;
        first.BuildResult = 1;
        (await first.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(1);
        File.ReadAllText(Binary).ShouldBe("old gateway output");

        var retry = NewPipeline();
        (await retry.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        retry.BuildCalls.ShouldBe(1);
        retry.RestartCalls.ShouldBe(1);
        (await UpdateDeploymentIdentity.MatchesAsync(Binary, NewCommit, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulUnchangedDeployment_SkipsBuildAndRestart()
    {
        var first = NewPipeline();
        (await first.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        first.BuildCalls.ShouldBe(1);
        var retry = NewPipeline();
        (await retry.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        retry.BuildCalls.ShouldBe(0);
        retry.RestartCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("stale-source")]
    [InlineData("changed-binary")]
    public async Task ExecuteAsync_UnverifiedArtifact_Rebuilds(string scenario)
    {
        if (scenario != "missing")
            await UpdateDeploymentIdentity.RecordAsync(Binary, scenario == "stale-source" ? OldCommit : NewCommit, CancellationToken.None);
        if (scenario == "malformed")
            File.WriteAllText(UpdateDeploymentIdentity.ReceiptPath(Binary), "{invalid");
        if (scenario == "changed-binary")
            File.WriteAllText(Binary, "externally replaced output");
        var command = NewPipeline();
        (await command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        command.BuildCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 0, true)]
    public async Task ExecuteAsync_FailureOrCancellation_DoesNotCertifyDeployment(int buildResult, int restartResult, bool cancelBuild)
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        var command = NewPipeline();
        command.MoveSource = true;
        command.BuildResult = buildResult;
        command.RestartResult = restartResult;
        command.CancelBuild = cancelBuild;
        (await command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldNotBe(0);
        (await UpdateDeploymentIdentity.MatchesAsync(Binary, NewCommit, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeNoOp_DoesNotBuildOrRestart()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var command = NewPipeline();
        (await command.ExecuteAsync(_root, _root, 5005, false, cancellation.Token)).ShouldBe(130);
        command.BuildCalls.ShouldBe(0);
        command.RestartCalls.ShouldBe(0);
    }

    [Fact]
    public async Task MatchesAsync_Cancelled_DoesNotReturnCurrent()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => UpdateDeploymentIdentity.MatchesAsync(Binary, NewCommit, cancellation.Token));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task CheckAsync_SourceCurrent_ReportsDeploymentSeparately(bool verified, int expectedExit)
    {
        if (verified)
            await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        var command = NewPipeline();
        command.ReleaseRequest = ReleaseTargetRequest.Latest;
        var original = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Interactive = InteractionSupport.No });
        AnsiConsole.Profile.Width = 200;
        try
        {
            (await command.CheckAsync(_root, false, CancellationToken.None)).ShouldBe(expectedExit);
            var output = writer.ToString();
            output.ShouldContain("Source");
            output.ShouldContain(NewCommit);
            output.ShouldContain(verified ? "Verified deployment" : "unverified");
            output.ShouldContain("Running revision not verified");
            output.ShouldNotContain("Already at");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingOutputDirectory_CanBuildFreshDeployment()
    {
        Directory.Delete(Path.GetDirectoryName(Binary) ?? throw new InvalidOperationException(), recursive: true);
        var command = NewPipeline();
        (await command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        command.BuildCalls.ShouldBe(1);
    }

    [Fact]
    public async Task MatchesAsync_ReceiptButMissingBinary_ForcesReconciliation()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        File.Delete(Binary);
        (await UpdateDeploymentIdentity.MatchesAsync(Binary, NewCommit, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task MatchesAsync_UnknownSource_NeverCertifiesDeployment()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, "unknown", CancellationToken.None);
        (await UpdateDeploymentIdentity.MatchesAsync(Binary, "unknown", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_UntrackedSourceAfterReceipt_RebuildsAndCannotReviveReceiptAfterRemoval()
    {
        var first = NewPipeline();
        (await first.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        (await UpdateDeploymentIdentity.MatchesAsync(Binary, NewCommit, CancellationToken.None)).ShouldBeTrue();
        var untracked = Path.Combine(_root, "src/gateway/BotNexus.Gateway.Api/Untracked.cs");
        File.WriteAllText(untracked, "// SDK default compile input");

        var command = NewPipeline();
        command.ReleaseRequest = ReleaseTargetRequest.Latest;
        (await command.CheckAsync(_root, false, CancellationToken.None)).ShouldBe(1);
        (await command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        command.BuildCalls.ShouldBe(1);
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
        File.Delete(untracked);
        (await command.CheckAsync(_root, false, CancellationToken.None)).ShouldBe(1);
        var retry = NewPipeline();
        (await retry.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        retry.BuildCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_UntrackedInputRemovedDuringBuild_DoesNotCertifyContaminatedOutput()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        var untracked = Path.Combine(_root, "src/gateway/BotNexus.Gateway.Api/Untracked.cs");
        File.WriteAllText(untracked, "// included when build starts");
        var command = NewPipeline();
        command.DuringBuild = (_, _) =>
        {
            File.Delete(untracked);
            return Task.CompletedTask;
        };
        (await command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        command.BuildCalls.ShouldBe(1);
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
        (await UpdateDeploymentIdentity.MatchesAsync(Binary, NewCommit, CancellationToken.None)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("tracked")]
    [InlineData("untracked")]
    [InlineData("commit")]
    public async Task ExecuteAsync_SourceChangesDuringBuild_DoesNotPublishReceipt(string change)
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        var command = NewPipeline();
        command.MoveSource = true;
        command.DuringBuild = (root, _) =>
        {
            if (change == "commit")
                command.SourceCommit = OldCommit;
            else
                File.WriteAllText(Path.Combine(root, "src/gateway/BotNexus.Gateway.Api/" +
                    (change == "tracked" ? "Program.cs" : "Untracked.cs")), "// changed during build");
            return Task.CompletedTask;
        };
        (await command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None)).ShouldBe(0);
        command.BuildCalls.ShouldBe(1);
        command.RestartCalls.ShouldBe(1);
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_TokenCancelledInsideBuild_DoesNotRestartOrPublishReceipt()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var command = NewPipeline();
        command.MoveSource = true;
        command.DuringBuild = async (_, token) => await Task.Delay(Timeout.InfiniteTimeSpan, token);
        var execution = command.ExecuteAsync(_root, _root, 5005, false, cancellation.Token);
        await command.BuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        (await execution.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(130);
        command.RestartCalls.ShouldBe(0);
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionInsideBuild_DoesNotPublishReceipt()
    {
        await UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, CancellationToken.None);
        var command = NewPipeline();
        command.MoveSource = true;
        command.DuringBuild = (_, _) => throw new IOException("build boundary failure");
        await Should.ThrowAsync<IOException>(() => command.ExecuteAsync(_root, _root, 5005, false, CancellationToken.None));
        command.BuildCalls.ShouldBe(1);
        command.RestartCalls.ShouldBe(0);
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
    }

    // A real asynchronous read boundary in receipt hashing, not a pre-cancelled token.
    private sealed class ReceiptReadStream(bool failRead) : MemoryStream
    {
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            if (failRead)
                throw new IOException("receipt hash read failed");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task RecordAsync_CancelledDuringHash_DoesNotPublishReceiptOrLeaveTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new ReceiptReadStream(failRead: false);
        var recording = UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, stream, cancellation.Token);
        await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => recording.WaitAsync(TimeSpan.FromSeconds(10)));
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(Binary) ?? throw new InvalidOperationException(), "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordAsync_HashReadFails_DoesNotPublishReceiptOrLeaveTemporaryFile()
    {
        await using var stream = new ReceiptReadStream(failRead: true);
        await Should.ThrowAsync<IOException>(() => UpdateDeploymentIdentity.RecordAsync(Binary, NewCommit, stream, CancellationToken.None));
        File.Exists(UpdateDeploymentIdentity.ReceiptPath(Binary)).ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(Binary) ?? throw new InvalidOperationException(), "*.tmp").ShouldBeEmpty();
    }

    public void Dispose() => UpdateNoOpRebuildSkipTests.DeleteFixtureRepositoryForIsolationPin(_root);
}
