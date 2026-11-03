using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Contracts.Updates;
using NSubstitute;
using Spectre.Console;
using Xunit;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public sealed class UpdaterCompatibilityTests
{
    private sealed class Probe(IGatewayProcessManager manager, ReleaseTargetKind kind, bool noOp) : UpdateCommand(manager)
    {
        internal List<string> Mutations { get; } = [];
        internal string? ContractJson { get; init; }
        internal UpdaterDescriptor Descriptor { get; init; } = new("0.50.2", "running-sha", [1], ["planned-shutdown-v1"], ["planned-shutdown-v1"]);
        internal bool CancelMetadata { get; init; }
        internal bool SkipRebuild { get; init; } = true;
        internal int DirtyResult { get; init; }
        protected override Task<int> RunGitPullStepAsync(string repoRoot, bool verbose, CancellationToken ct)
        { Mutations.Add("unchecked-pull"); return Task.FromResult(0); }
        protected override UpdaterDescriptor GetUpdaterDescriptor() => Descriptor;
        protected override Task<string?> ReadTargetFileAsync(string repoRoot, ResolvedReleaseTarget target, string path, CancellationToken ct)
        {
            if (CancelMetadata) throw new OperationCanceledException(ct);
            target.CommitSha.ShouldBe(new string('a', 40));
            return Task.FromResult(path == UpdaterCompatibility.ContractPath ? ContractJson : "<Project><PropertyGroup><Version>0.50.2</Version></PropertyGroup></Project>");
        }
        protected override Task<ReleaseUpdateStatus> ResolveReleaseStatusAsync(string repoRoot, ReleaseTargetRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var target = new ResolvedReleaseTarget(kind, "0.50.2", "v0.50.2", "fixture", new string('a', 40));
            return Task.FromResult(new ReleaseUpdateStatus(new ReleaseIdentity("0.50.1", noOp ? target.CommitSha : new string('b', 40)), target));
        }
        protected override Task<int> EnsureWorkingTreeReadyAsync(string repoRoot, CancellationToken ct)
        { Mutations.Add("dirty-tree"); return Task.FromResult(DirtyResult); }
        protected override Task CheckoutReleaseTargetAsync(string repoRoot, ResolvedReleaseTarget target, CancellationToken ct)
        { Mutations.Add("checkout"); return Task.CompletedTask; }
        protected override Task<bool> CanSkipRebuildAsync(string repoRoot, CancellationToken ct) => Task.FromResult(SkipRebuild);
        protected override Task<bool> WaitForPortFreeAsync(int port, CancellationToken ct) => Task.FromResult(true);
        protected override bool IsGatewayRunning(string home, string repoRoot) => true;
        protected override Task<int> RunBuildAndDeployAsync(string repoRoot, string home, bool verbose, CancellationToken ct)
        { Mutations.Add("build-deploy"); return Task.FromResult(0); }
        protected override Task<int> RunRestartAsync(string home, int port, string repoRoot, CancellationToken ct)
        { Mutations.Add("restart"); return Task.FromResult(0); }
    }

    [Fact]
    public async Task Execute_NullReleaseRequest_RejectsWithoutAnyMutation()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, false);
        (await command.ExecuteAsync("missing-fixture-repository", "missing-fixture-home", 5005, false, CancellationToken.None)).ShouldBe(4);
        command.Mutations.ShouldBeEmpty();
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ReleaseTargetKind.Stable, false)]
    [InlineData(ReleaseTargetKind.Exact, false)]
    [InlineData(ReleaseTargetKind.Latest, false)]
    [InlineData(ReleaseTargetKind.Latest, true)]
    public async Task Execute_MissingTargetContract_RefusesBeforeEveryMutation(ReleaseTargetKind kind, bool noOp)
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, kind, noOp) { ReleaseRequest = ReleaseTargetRequest.Latest, DirtyTreeHandling = UpdateCommand.DirtyTreeMode.Force };
        var result = await command.ExecuteAsync("missing-fixture-repository", "missing-fixture-home", 5005, false, CancellationToken.None);
        result.ShouldBe(4);
        command.Mutations.ShouldBeEmpty();
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0.50.2", "0.51.0", "planned-shutdown-v1", "planned-shutdown-v1")]
    [InlineData("0.50.2", "0.50.2", "future-shutdown-v2", "planned-shutdown-v1")]
    [InlineData("9.0.0", "0.50.2", "planned-shutdown-v1", "future-target-v2")]
    public async Task Execute_CrossVersionIncompatibility_DoesNotMutate(string running, string minimum, string required, string requiredTarget)
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Exact, false)
        {
            ReleaseRequest = ReleaseTargetRequest.Exact("0.50.2"),
            ContractJson = UpdaterContractTests.Contract(minimum, required: required),
            Descriptor = new UpdaterDescriptor(running, "fixture-sha", [1], ["planned-shutdown-v1"], [requiredTarget]),
            DirtyTreeHandling = UpdateCommand.DirtyTreeMode.Stash
        };
        (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(4);
        command.Mutations.ShouldBeEmpty();
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CompatibleNoOp_OnlySkipsCheckoutWhenAlreadyAtTarget(bool noOp)
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, noOp)
        { ReleaseRequest = ReleaseTargetRequest.Latest, ContractJson = UpdaterContractTests.Contract() };
        (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(0);
        command.Mutations.ShouldBe(noOp ? new[] { "dirty-tree" } : new[] { "dirty-tree", "checkout" });
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_CompatibleUpdate_UsesExistingStopBuildRestartFlow()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Exact, false)
        { ReleaseRequest = ReleaseTargetRequest.Exact("0.50.2"), ContractJson = UpdaterContractTests.Contract(), SkipRebuild = false };
        (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(0);
        command.Mutations.ShouldBe(new[] { "dirty-tree", "checkout", "build-deploy", "restart" });
        await manager.Received(1).StopAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Execute_CompatibleButDirty_PreservesDirtyExitCode()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, false)
        { ReleaseRequest = ReleaseTargetRequest.Latest, ContractJson = UpdaterContractTests.Contract(), DirtyResult = 3 };
        (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(3);
        command.Mutations.ShouldBe(new[] { "dirty-tree" });
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_CancelledDuringMetadata_DoesNotMutate()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, false)
        { ReleaseRequest = ReleaseTargetRequest.Latest, CancelMetadata = true };
        (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(130);
        command.Mutations.ShouldBeEmpty();
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{}")]
    public async Task Execute_MalformedOrUnknownContract_NoMutationEvenForNoOp(string json)
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, true)
        { ReleaseRequest = ReleaseTargetRequest.Latest, ContractJson = json };
        (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(4);
        command.Mutations.ShouldBeEmpty();
        manager.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_Refusal_ProvidesLiteralCommandsIdentityAndOriginalRetry()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, false)
        { ReleaseRequest = ReleaseTargetRequest.Latest, ContractJson = UpdaterContractTests.Contract("0.51.0") };
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "source with 'quote' $dollar;[x]"));
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            (await command.ExecuteAsync(source, "unused", 5005, false, CancellationToken.None)).ShouldBe(4);
        }
        finally { Console.SetOut(previous); }
        var text = output.ToString();
        text.ShouldContain("Running CLI: 0.50.2+running-sha");
        text.ShouldContain("Selected target: 0.50.2+" + new string('a', 40));
        text.ShouldContain("requires updater >= 0.51.0");
        text.ShouldContain("dotnet tool update -g BotNexus.Cli");
        text.ShouldContain("  Set-Location -LiteralPath " + UpdaterCompatibility.QuoteSource(source, true) + Environment.NewLine);
        text.ShouldContain("  cd -- " + UpdaterCompatibility.QuoteSource(source, false) + Environment.NewLine);
        text.ShouldContain("dotnet pack src/gateway/BotNexus.Cli -c Release -o ./artifacts");
        text.ShouldContain("dotnet tool update -g BotNexus.Cli --source ./artifacts");
        text.ShouldNotContain("--add-source");
        text.ShouldNotContain("tool install");
        text.ShouldContain("not guaranteed");
        text.ShouldContain("rerun the original botnexus update command with the same options");
        text.ShouldNotContain("gateway left running");
        command.Mutations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_Refusal_StripsTargetTerminalControls()
    {
        var command = new Probe(Substitute.For<IGatewayProcessManager>(), ReleaseTargetKind.Latest, false)
        {
            ReleaseRequest = ReleaseTargetRequest.Latest,
            ContractJson = UpdaterContractTests.Contract(required: "\u001b[2J\u001b[Hspoofed\rbehavior")
        };
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            (await command.ExecuteAsync("unused", "unused", 5005, false, CancellationToken.None)).ShouldBe(4);
        }
        finally { Console.SetOut(previous); }
        output.ToString().ShouldNotContain("\u001b");
        output.ToString().ShouldNotContain("spoofed\rbehavior");
        output.ToString().ShouldContain("unsupported updater lifecycle behavior");
        command.Mutations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_CancelledBeforeResolution_DoesNotMutate()
    {
        var manager = Substitute.For<IGatewayProcessManager>();
        var command = new Probe(manager, ReleaseTargetKind.Latest, false) { ReleaseRequest = ReleaseTargetRequest.Latest };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        (await command.ExecuteAsync("unused", "unused", 5005, false, cancellation.Token)).ShouldBe(130);
        command.Mutations.ShouldBeEmpty();
        manager.ReceivedCalls().ShouldBeEmpty();
    }
}
