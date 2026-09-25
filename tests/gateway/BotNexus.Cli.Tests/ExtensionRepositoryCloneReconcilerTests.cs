using System.Diagnostics;
using System.IO.Abstractions;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Tests;

public sealed class ExtensionRepositoryCloneReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"botnexus-reconcile-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReconcileAsync_LocalBranch_ClonesAndPersistsResolvedState()
    {
        var (source, commit) = CreateSource();
        var (registry, reconciler, registration) = await CreateReconciler(source, "main");

        var result = await reconciler.ReconcileAsync(registration);

        result.ResolvedCommit.ShouldBe(commit);
        var persisted = (await registry.ListAsync()).ShouldHaveSingleItem();
        persisted.ReconciliationStatus.ShouldBe("succeeded");
        persisted.ResolvedCommit.ShouldBe(commit);
        persisted.ClonePath.ShouldNotBeNull();
        Directory.Exists(persisted.ClonePath).ShouldBeTrue();
        persisted.LastAttemptUtc.ShouldNotBeNull();
        persisted.LastSuccessUtc.ShouldNotBeNull();
        persisted.LatestFailure.ShouldBeNull();
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("commit")]
    public async Task ReconcileAsync_TagOrCommit_ResolvesExactCommit(string refKind)
    {
        var (source, commit) = CreateSource();
        if (refKind == "tag") Git(source, "tag", "v1");
        var requestedRef = refKind == "tag" ? "v1" : commit;
        var (_, reconciler, registration) = await CreateReconciler(source, requestedRef);

        var result = await reconciler.ReconcileAsync(registration);

        result.ResolvedCommit.ShouldBe(commit);
    }

    [Fact]
    public async Task ReconcileAsync_ConcurrentRequestsForOneClone_AreSerializedAndBothSucceed()
    {
        var (source, commit) = CreateSource();
        var (_, reconciler, registration) = await CreateReconciler(source, "main");

        var results = await Task.WhenAll(
            reconciler.ReconcileAsync(registration),
            reconciler.ReconcileAsync(registration));

        results.ShouldAllBe(result => result.Succeeded && result.ResolvedCommit == commit);
    }

    [Fact]
    public async Task ReconcileAsync_ExistingCleanCloneFetchesFastForward()
    {
        var (source, _) = CreateSource();
        var (registry, reconciler, registration) = await CreateReconciler(source, "main");
        await reconciler.ReconcileAsync(registration);
        var next = Commit(source, "next");

        var result = await reconciler.ReconcileAsync(registration);

        result.Succeeded.ShouldBeTrue();
        result.ResolvedCommit.ShouldBe(next);
        (await registry.ListAsync()).Single().ResolvedCommit.ShouldBe(next);
    }

    [Fact]
    public async Task ReconcileAsync_DirtyCloneReturnsNamedFailureWithoutDiscardingChanges()
    {
        var (source, _) = CreateSource();
        var (registry, reconciler, registration) = await CreateReconciler(source, "main");
        await reconciler.ReconcileAsync(registration);
        var clone = (await registry.ListAsync()).Single().ClonePath!;
        var sentinel = Path.Combine(clone, "operator.txt");
        File.WriteAllText(sentinel, "preserve");

        var result = await reconciler.ReconcileAsync(registration);

        result.FailureName.ShouldBe("dirty");
        File.ReadAllText(sentinel).ShouldBe("preserve");
        (await registry.ListAsync()).Single().LatestFailure.ShouldStartWith("dirty:");
    }

    [Fact]
    public async Task ReconcileAsync_MissingOriginReturnsNamedFailure()
    {
        var (source, _) = CreateSource();
        var (registry, reconciler, registration) = await CreateReconciler(source, "main");
        await reconciler.ReconcileAsync(registration);
        var clone = (await registry.ListAsync()).Single().ClonePath!;
        Git(clone, "remote", "remove", "origin");

        var result = await reconciler.ReconcileAsync(registration);

        result.FailureName.ShouldBe("missing-origin");
    }

    [Fact]
    public async Task ReconcileAsync_InvalidRefReturnsNamedFailure()
    {
        var (source, _) = CreateSource();
        var (_, reconciler, registration) = await CreateReconciler(source, "does-not-exist");

        var result = await reconciler.ReconcileAsync(registration);

        result.FailureName.ShouldBe("invalid-ref");
    }

    [Fact]
    public async Task ReconcileAsync_DivergedTargetReturnsNamedFailureWithoutMovingHead()
    {
        var (source, first) = CreateSource();
        var (registry, reconciler, registration) = await CreateReconciler(source, "main");
        await reconciler.ReconcileAsync(registration);
        Git(source, "checkout", "--orphan", "replacement");
        File.WriteAllText(Path.Combine(source, "content.txt"), "replacement");
        Git(source, "add", "content.txt");
        Git(source, "commit", "-m", "replacement");
        Git(source, "branch", "-f", "main", "HEAD");

        var result = await reconciler.ReconcileAsync(registration);

        result.FailureName.ShouldBe("diverged");
        Git((await registry.ListAsync()).Single().ClonePath!, "rev-parse", "HEAD").ShouldBe(first);
    }

    private async Task<(ExtensionRepositoryRegistryService Registry, ExtensionRepositoryCloneReconciler Reconciler, ExtensionRepositoryRegistrationInfo Registration)> CreateReconciler(string source, string requestedRef)
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var registry = new ExtensionRepositoryRegistryService(Path.Combine(home, "config.json"), new FileSystem());
        await registry.AddAsync("community", "https://fixture.invalid/repository.git", requestedRef);
        var stored = (await registry.ListAsync()).Single();
        var registration = stored with { RepositoryUrl = new Uri(source).AbsoluteUri };
        return (registry, new ExtensionRepositoryCloneReconciler(home, registry), registration);
    }

    private (string Source, string Commit) CreateSource()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Git(source, "init", "--initial-branch=main");
        Git(source, "config", "user.name", "botnexus-test");
        Git(source, "config", "user.email", "botnexus-test@invalid.local");
        return (source, Commit(source, "initial"));
    }

    private static string Commit(string source, string contents)
    {
        File.WriteAllText(Path.Combine(source, "content.txt"), contents);
        Git(source, "add", "content.txt");
        Git(source, "commit", "-m", contents);
        return Git(source, "rev-parse", "HEAD");
    }

    private static string Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment.Remove("GIT_DIR");
        info.Environment.Remove("GIT_WORK_TREE");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
