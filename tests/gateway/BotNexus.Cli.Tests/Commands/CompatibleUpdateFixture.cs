using System.IO.Abstractions;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Services;
using BotNexus.Gateway.Contracts.Updates;

namespace BotNexus.Cli.Tests.Commands;

// Explicit immutable release/metadata fixture for tests of the later lifecycle stages.
internal abstract class CompatibleUpdateFixture : UpdateCommand
{
    protected CompatibleUpdateFixture(IGatewayProcessManager manager, bool noOp = false) : this(manager, null, noOp)
    {
    }

    protected CompatibleUpdateFixture(IGatewayProcessManager manager, IFileSystem? fileSystem, bool noOp = false) : base(manager, fileSystem)
    {
        ReleaseRequest = ReleaseTargetRequest.Latest;
        TargetWasNoOp = noOp;
    }

    private bool TargetWasNoOp { get; }

    protected override UpdaterDescriptor GetUpdaterDescriptor()
        => new("0.50.2", "fixture", [1], ["planned-shutdown-v1"], ["planned-shutdown-v1"]);

    protected override Task<string?> ReadTargetFileAsync(string repoRoot, ResolvedReleaseTarget target, string path, CancellationToken cancellationToken)
        => Task.FromResult<string?>(path == UpdaterCompatibility.ContractPath ? UpdaterContractTests.Contract() : "<Project><Version>0.50.2</Version></Project>");

    protected override Task<ReleaseUpdateStatus> ResolveReleaseStatusAsync(string repoRoot, ReleaseTargetRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = new ResolvedReleaseTarget(ReleaseTargetKind.Latest, "0.50.2", "origin/main", "fixture", new string('a', 40));
        return Task.FromResult(new ReleaseUpdateStatus(new ReleaseIdentity("0.50.2", TargetWasNoOp ? target.CommitSha : new string('b', 40)), target));
    }

    protected override Task<int> EnsureWorkingTreeReadyAsync(string repoRoot, CancellationToken cancellationToken)
        => Task.FromResult(0);

    protected override Task CheckoutReleaseTargetAsync(string repoRoot, ResolvedReleaseTarget target, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
