using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ExtensionLifecycleReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"botnexus-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReconcileAsync_NoRegistrations_DoesNoWorkAndSucceeds()
    {
        var fixture = await Fixture.CreateAsync(_root);

        var result = await fixture.Reconciler.ReconcileAsync();

        result.Repositories.ShouldBeEmpty();
        fixture.Events.ShouldBe(["deploy"]);
    }

    [Fact]
    public async Task ReconcileAsync_RepositoryMaintenanceWithoutRegistrations_HoldsLockButDoesNotDeploy()
    {
        var fixture = await Fixture.CreateAsync(_root);
        var held = await new ExtensionReconciliationFileLock(Path.Combine(_root, "home")).AcquireAsync(CancellationToken.None);

        var reconciliation = fixture.Reconciler.ReconcileAsync(purpose: ExtensionLifecyclePurpose.RepositoryMaintenance);
        await fixture.LockAcquisitionStarted.Task;

        reconciliation.IsCompleted.ShouldBeFalse();
        fixture.Events.ShouldBeEmpty();
        await held.DisposeAsync();
        await reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_FailureIsIsolatedByRepositoryAndPreservesLaterWork()
    {
        var fixture = await Fixture.CreateAsync(_root, "broken", "healthy");
        fixture.Clone = id => id == "broken"
            ? ExtensionCloneOutcome.Failed(id, "clone-failed", "network unavailable")
            : ExtensionCloneOutcome.Success(id, "abc123", fixture.RepositoryPath(id));
        fixture.Rebuild();

        var result = await fixture.Reconciler.ReconcileAsync();

        result.Repositories.Count.ShouldBe(2);
        result.Repositories.Single(item => item.Id == "broken").FailureName.ShouldBe("clone-failed");
        result.Repositories.Single(item => item.Id == "healthy").Succeeded.ShouldBeTrue();
        fixture.Events.Count(item => item == "deploy").ShouldBe(1);
        fixture.Events.ShouldNotContain("build:broken");
    }

    [Fact]
    public async Task ReconcileAsync_BuildFailureDoesNotDeployCandidateOrDeleteLastKnownGood()
    {
        var fixture = await Fixture.CreateAsync(_root, "tools");
        var live = Path.Combine(_root, "home", "extensions", "tools-extension");
        Directory.CreateDirectory(live);
        var sentinel = Path.Combine(live, "extension.dll");
        await File.WriteAllTextAsync(sentinel, "last-known-good");
        var staged = Path.Combine(_root, "home", "extension-repositories", "tools", "staged", "project-000");
        Directory.CreateDirectory(staged);
        var stagedSentinel = Path.Combine(staged, "extension.dll");
        await File.WriteAllTextAsync(stagedSentinel, "last-known-good-staged");
        fixture.Build = _ => ExtensionBuildOutcome.Failed("compile-failed", "compiler error");
        fixture.Rebuild();

        var result = await fixture.Reconciler.ReconcileAsync();

        result.Repositories.ShouldHaveSingleItem().FailureName.ShouldBe("compile-failed");
        var persisted = (await fixture.Registry.ListAsync()).ShouldHaveSingleItem();
        persisted.ReconciliationStatus.ShouldBe("failed");
        persisted.LastSuccessUtc.ShouldBeNull();
        fixture.Events.Count(item => item == "deploy").ShouldBe(1);
        (await File.ReadAllTextAsync(sentinel)).ShouldBe("last-known-good");
        (await File.ReadAllTextAsync(stagedSentinel)).ShouldBe("last-known-good-staged");
    }

    [Fact]
    public async Task ReconcileAsync_PromotesWholeGenerationAndRemovesObsoleteProjectOutput()
    {
        var fixture = await Fixture.CreateAsync(_root, "tools");
        var staged = Path.Combine(_root, "home", "extension-repositories", "tools", "staged");
        Directory.CreateDirectory(Path.Combine(staged, "project-001"));
        await File.WriteAllTextAsync(Path.Combine(staged, "project-001", "obsolete.txt"), "old");
        fixture.Build = id =>
        {
            var output = fixture.CurrentBuildOutput.ShouldNotBeNull();
            File.WriteAllText(Path.Combine(output, "current.txt"), id);
            return ExtensionBuildOutcome.Success();
        };
        fixture.Rebuild();

        var result = await fixture.Reconciler.ReconcileAsync();

        result.Succeeded.ShouldBeTrue();
        Directory.Exists(Path.Combine(staged, "project-001")).ShouldBeFalse();
        File.Exists(Path.Combine(staged, "project-000", "current.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task ReconcileAsync_BuildFailurePreservesWholePreviousGeneration()
    {
        var fixture = await Fixture.CreateAsync(_root, "tools");
        var staged = Path.Combine(_root, "home", "extension-repositories", "tools", "staged");
        Directory.CreateDirectory(Path.Combine(staged, "project-000"));
        var sentinel = Path.Combine(staged, "project-000", "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "last-known-good");
        fixture.Build = _ => ExtensionBuildOutcome.Failed("build-failed", "compile error");
        fixture.Rebuild();

        await fixture.Reconciler.ReconcileAsync();

        (await File.ReadAllTextAsync(sentinel)).ShouldBe("last-known-good");
        Directory.GetDirectories(Path.GetDirectoryName(staged)!, "staged.candidate-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_DeploymentSourcesComeOnlyFromEnabledRegistryEntries()
    {
        var fixture = await Fixture.CreateAsync(_root, "enabled", "disabled");
        await fixture.Registry.SetEnabledAsync("disabled", false);
        var stale = Path.Combine(_root, "home", "extension-repositories", "removed", "staged", "project-000");
        Directory.CreateDirectory(stale);
        fixture.Deploy = sources =>
        {
            sources.ShouldContain(source => source.Source.StartsWith("repository:enabled:", StringComparison.Ordinal));
            sources.ShouldNotContain(source => source.Source.Contains("disabled", StringComparison.Ordinal));
            sources.ShouldNotContain(source => source.Source.Contains("removed", StringComparison.Ordinal));
            return new ExtensionDeploymentResult(0, [], []);
        };
        fixture.Rebuild();

        await fixture.Reconciler.ReconcileAsync();
    }

    [Fact]
    public async Task ReconcileAsync_RecordsSuccessOnlyAfterGlobalDeployment()
    {
        var fixture = await Fixture.CreateAsync(_root, "tools");
        fixture.Deploy = sources =>
        {
            var duringDeployment = fixture.Registry.ListAsync().GetAwaiter().GetResult().Single();
            duringDeployment.ReconciliationStatus.ShouldBe("reconciling");
            duringDeployment.LastSuccessUtc.ShouldBeNull();
            return new ExtensionDeploymentResult(0, [], []);
        };
        fixture.Rebuild();

        await fixture.Reconciler.ReconcileAsync();

        var completed = (await fixture.Registry.ListAsync()).Single();
        completed.ReconciliationStatus.ShouldBe("succeeded");
        completed.LastSuccessUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReconcileAsync_ZeroRegistrationsWaitsForLockBeforeDeployment()
    {
        var fixture = await Fixture.CreateAsync(_root);
        var held = await new ExtensionReconciliationFileLock(Path.Combine(_root, "home")).AcquireAsync(CancellationToken.None);

        var reconciliation = fixture.Reconciler.ReconcileAsync();
        await fixture.LockAcquisitionStarted.Task;

        reconciliation.IsCompleted.ShouldBeFalse();
        fixture.Events.ShouldBeEmpty();
        await held.DisposeAsync();
        await reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Events.ShouldBe(["deploy"]);
    }

    [Fact]
    public async Task FileLock_SerializesIndependentInstancesForOneHome()
    {
        Directory.CreateDirectory(_root);
        var first = new ExtensionReconciliationFileLock(_root);
        var second = new ExtensionReconciliationFileLock(_root);
        var held = await first.AcquireAsync(CancellationToken.None);
        var waiting = second.AcquireAsync(CancellationToken.None).AsTask();

        waiting.IsCompleted.ShouldBeFalse();
        await held.DisposeAsync();
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        acquired.ShouldNotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Fixture
    {
        private readonly string _home;
        private readonly string _repoRoot;
        private readonly ExtensionRepositoryRegistryService _registry;

        private Fixture(string root, ExtensionRepositoryRegistryService registry)
        {
            _home = Path.Combine(root, "home");
            _repoRoot = Path.Combine(root, "source");
            _registry = registry;
            Directory.CreateDirectory(_repoRoot);
            Rebuild();
        }

        public List<string> Events { get; } = [];
        public TaskCompletionSource LockAcquisitionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExtensionRepositoryRegistryService Registry => _registry;
        public string? CurrentBuildOutput { get; private set; }
        public Func<string, ExtensionCloneOutcome> Clone { get; set; } = _ => throw new InvalidOperationException();
        public Func<string, ExtensionBuildOutcome> Build { get; set; } = _ => ExtensionBuildOutcome.Success();
        public Func<IReadOnlyCollection<ExtensionDeploymentSource>, ExtensionDeploymentResult> Deploy { get; set; }
            = _ => new ExtensionDeploymentResult(0, [], []);
        public ExtensionLifecycleReconciler Reconciler { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(string root, params string[] ids)
        {
            var home = Path.Combine(root, "home");
            Directory.CreateDirectory(home);
            var registry = new ExtensionRepositoryRegistryService(Path.Combine(home, "config.json"), new FileSystem());
            foreach (var id in ids)
                await registry.AddAsync(id, $"https://example.test/{id}.git", "main");
            var fixture = new Fixture(root, registry);
            fixture.Clone = id => ExtensionCloneOutcome.Success(id, "abc123", fixture.RepositoryPath(id));
            fixture.Rebuild();
            return fixture;
        }

        public string RepositoryPath(string id)
        {
            var path = Path.Combine(_home, "extension-repositories", id, "repository");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, $"{id}.csproj"), "<Project />");
            return path;
        }

        public void Rebuild()
        {
            var fileLock = new ExtensionReconciliationFileLock(_home);
            Reconciler = new ExtensionLifecycleReconciler(
                _home,
                _repoRoot,
                _registry,
                async (registration, ct) =>
                {
                    Events.Add($"clone:{registration.Id}");
                    await _registry.RecordReconciliationAttemptAsync(
                        registration.Id,
                        Path.Combine(_home, "extension-repositories", registration.Id, "repository"),
                        TimeProvider.System.GetUtcNow(),
                        ct);
                    var outcome = Clone(registration.Id);
                    if (!outcome.Succeeded)
                        await _registry.RecordReconciliationFailureAsync(registration.Id, outcome.FailureName!, outcome.Diagnostic!, ct);
                    return outcome;
                },
                repository => Directory.GetFiles(repository, "*.csproj", SearchOption.AllDirectories),
                async (id, _, output, _) => { Events.Add($"build:{id}"); CurrentBuildOutput = output; return await Task.FromResult(Build(id)); },
                sources => { Events.Add("deploy"); return Deploy(sources); },
                fileLock,
                acquireLock: async ct =>
                {
                    LockAcquisitionStarted.TrySetResult();
                    return await fileLock.AcquireAsync(ct);
                });
        }
    }
}
