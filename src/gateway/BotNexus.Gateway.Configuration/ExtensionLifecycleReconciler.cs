using System.Collections.Concurrent;

namespace BotNexus.Gateway.Configuration;

/// <summary>Result of materializing one registered repository.</summary>
internal sealed record ExtensionCloneOutcome(string Id, bool Succeeded, string? ResolvedCommit, string? RepositoryPath, string? FailureName, string? Diagnostic)
{
    /// <summary>Creates a successful clone result.</summary>
    public static ExtensionCloneOutcome Success(string id, string commit, string repositoryPath) => new(id, true, commit, repositoryPath, null, null);
    /// <summary>Creates a named clone failure.</summary>
    public static ExtensionCloneOutcome Failed(string id, string failureName, string diagnostic) => new(id, false, null, null, failureName, diagnostic);
}

/// <summary>Result of building one discovered extension project.</summary>
internal sealed record ExtensionBuildOutcome(bool Succeeded, string? FailureName, string? Diagnostic)
{
    /// <summary>Creates a successful build result.</summary>
    public static ExtensionBuildOutcome Success() => new(true, null, null);
    /// <summary>Creates a named build failure.</summary>
    public static ExtensionBuildOutcome Failed(string failureName, string diagnostic) => new(false, failureName, diagnostic);
}

/// <summary>Reports one repository's isolated lifecycle result.</summary>
public sealed record ExtensionRepositoryLifecycleResult(string Id, bool Succeeded, string? ResolvedCommit, string? FailureName, string? Diagnostic);

/// <summary>Reports the complete non-fatal extension reconciliation pass.</summary>
public sealed record ExtensionLifecycleResult(IReadOnlyList<ExtensionRepositoryLifecycleResult> Repositories)
{
    /// <summary>True when every enabled registration reconciled successfully.</summary>
    public bool Succeeded => Repositories.All(item => item.Succeeded);
}

/// <summary>
/// Serializes lifecycle work across processes, prepares complete repository generations, and
/// performs one global deployment pass from current registry state.
/// </summary>
public enum ExtensionLifecyclePurpose
{
    /// <summary>Reconciles repositories and always refreshes the complete live extension tree.</summary>
    HostStartup,
    /// <summary>Reconciles repositories, but preserves the live tree when no repositories are registered.</summary>
    RepositoryMaintenance
}

/// <summary>Coordinates repository builds and one global extension deployment transaction.</summary>
public sealed class ExtensionLifecycleReconciler
{
    private readonly string _home;
    private readonly string _repoRoot;
    private readonly ExtensionRepositoryRegistryService _registry;
    private readonly Func<ExtensionRepositoryRegistrationInfo, CancellationToken, Task<ExtensionCloneOutcome>> _clone;
    private readonly Func<string, IEnumerable<string>> _discoverProjects;
    private readonly Func<string, string, string, CancellationToken, Task<ExtensionBuildOutcome>> _build;
    private readonly Func<IReadOnlyCollection<ExtensionDeploymentSource>, ExtensionDeploymentResult> _deploy;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>> _acquireLock;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a reconciler from explicit lifecycle seams, primarily for hosting and deterministic tests.</summary>
    internal ExtensionLifecycleReconciler(
        string home,
        string repoRoot,
        ExtensionRepositoryRegistryService registry,
        Func<ExtensionRepositoryRegistrationInfo, CancellationToken, Task<ExtensionCloneOutcome>> clone,
        Func<string, IEnumerable<string>> discoverProjects,
        Func<string, string, string, CancellationToken, Task<ExtensionBuildOutcome>> build,
        Func<IReadOnlyCollection<ExtensionDeploymentSource>, ExtensionDeploymentResult> deploy,
        ExtensionReconciliationFileLock fileLock,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? acquireLock = null)
    {
        _home = Path.GetFullPath(home);
        _repoRoot = Path.GetFullPath(repoRoot);
        _registry = registry;
        _clone = clone;
        _discoverProjects = discoverProjects;
        _build = build;
        _deploy = deploy;
        _acquireLock = acquireLock ?? fileLock.AcquireAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates the production pipeline shared by CLI and gateway API hosts.</summary>
    public static ExtensionLifecycleReconciler CreateDefault(string home, string repoRoot, bool verbose = false, Action<string>? report = null)
    {
        home = Path.GetFullPath(home);
        repoRoot = ResolveRepositoryRoot(repoRoot);
        var registry = new ExtensionRepositoryRegistryService(Path.Combine(home, "config.json"), new System.IO.Abstractions.FileSystem());
        var cloneReconciler = new ExtensionRepositoryCloneReconciler(home, registry);
        var buildService = new ExtensionProjectBuildService(
            new DotNetExtensionProjectBuildRunner(verbose),
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            report ?? (_ => { }));

        return new ExtensionLifecycleReconciler(
            home,
            repoRoot,
            registry,
            async (registration, ct) =>
            {
                var result = await cloneReconciler.ReconcileAsync(registration, ct).ConfigureAwait(false);
                var repositoryPath = Path.Combine(home, "extension-repositories", registration.Id, "repository");
                return result.Succeeded
                    ? ExtensionCloneOutcome.Success(result.Id, result.ResolvedCommit!, repositoryPath)
                    : ExtensionCloneOutcome.Failed(result.Id, result.FailureName!, result.Diagnostic!);
            },
            repository => Directory.GetFiles(repository, "*.csproj", SearchOption.AllDirectories)
                .Where(project => File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "botnexus-extension.json"))),
            async (_, project, staging, ct) =>
            {
                var result = await buildService.BuildAsync(project, staging, repoRoot, ct).ConfigureAwait(false);
                return result.ExitCode == 0
                    ? ExtensionBuildOutcome.Success()
                    : ExtensionBuildOutcome.Failed("build-failed", $"dotnet build exited with code {result.ExitCode} for '{project}'.");
            },
            sources => ExtensionDeploymentReconciler.Reconcile(Path.Combine(home, "extensions"), sources),
            new ExtensionReconciliationFileLock(home));
    }

    private static string ResolveRepositoryRoot(string startPath)
    {
        foreach (var candidate in new[] { startPath, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
                directory = directory.Parent;
            if (directory is not null) return directory.FullName;
        }
        return Path.GetFullPath(startPath);
    }

    /// <summary>Runs a complete pass; repository failures are returned rather than thrown.</summary>
    public async Task<ExtensionLifecycleResult> ReconcileAsync(
        string? onlyRepositoryId = null,
        CancellationToken cancellationToken = default,
        ExtensionLifecyclePurpose purpose = ExtensionLifecyclePurpose.HostStartup)
    {
        await using var lease = await _acquireLock(cancellationToken).ConfigureAwait(false);
        var allRegistrations = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        var targets = allRegistrations
            .Where(item => item.Enabled && (onlyRepositoryId is null || item.Id == onlyRepositoryId))
            .ToArray();
        var results = new Dictionary<string, ExtensionRepositoryLifecycleResult>(StringComparer.Ordinal);
        var resolvedCommits = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var registration in targets)
        {
            try
            {
                var clone = await _clone(registration, cancellationToken).ConfigureAwait(false);
                if (!clone.Succeeded)
                {
                    results[registration.Id] = Failed(registration.Id, clone.FailureName!, clone.Diagnostic!);
                    continue;
                }

                var projects = _discoverProjects(clone.RepositoryPath!).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (projects.Length == 0)
                {
                    const string message = "No extension projects were found in the repository.";
                    await RecordFailureAsync(registration.Id, "no-extension-projects", message, cancellationToken).ConfigureAwait(false);
                    results[registration.Id] = Failed(registration.Id, "no-extension-projects", message);
                    continue;
                }

                var destination = Path.Combine(_home, "extension-repositories", registration.Id, "staged");
                var candidate = destination + $".candidate-{Guid.NewGuid():N}";
                try
                {
                    Directory.CreateDirectory(candidate);
                    ExtensionBuildOutcome? failedBuild = null;
                    for (var index = 0; index < projects.Length; index++)
                    {
                        var output = Path.Combine(candidate, $"project-{index:D3}");
                        Directory.CreateDirectory(output);
                        var build = await _build(registration.Id, projects[index], output, cancellationToken).ConfigureAwait(false);
                        if (!build.Succeeded) { failedBuild = build; break; }
                    }

                    if (failedBuild is not null)
                    {
                        await RecordFailureAsync(registration.Id, failedBuild.FailureName!, failedBuild.Diagnostic!, cancellationToken).ConfigureAwait(false);
                        results[registration.Id] = Failed(registration.Id, failedBuild.FailureName!, failedBuild.Diagnostic!);
                        continue;
                    }

                    candidates[registration.Id] = candidate;
                }
                finally
                {
                    if (!candidates.ContainsKey(registration.Id) && Directory.Exists(candidate))
                        Directory.Delete(candidate, recursive: true);
                }

                resolvedCommits[registration.Id] = clone.ResolvedCommit!;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await RecordFailureAsync(registration.Id, "reconciliation-failed", ex.Message, cancellationToken).ConfigureAwait(false);
                results[registration.Id] = Failed(registration.Id, "reconciliation-failed", ex.Message);
            }
        }

        ExtensionDeploymentResult deployment;
        try
        {
            if (purpose == ExtensionLifecyclePurpose.RepositoryMaintenance && allRegistrations.Length == 0)
            {
                deployment = new ExtensionDeploymentResult(0, [], []);
            }
            else
            {
                var sources = DiscoverInTreeSources(_repoRoot)
                    .Concat(DiscoverRegisteredSources(_home, allRegistrations, candidates))
                    .ToArray();
                deployment = _deploy(sources);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deployment = new ExtensionDeploymentResult(0, [], []) { Accepted = false, TransactionFailure = ex.Message };
        }

        try
        {
            foreach (var (id, commit) in resolvedCommits)
            {
                var failures = deployment.Failures.Where(item => IsSourceForRepository(item.Source, id)).ToArray();
                if (!deployment.Accepted || failures.Length > 0)
                {
                    var diagnostic = failures.Length > 0
                        ? string.Join(" ", failures.Select(item => item.Message))
                        : deployment.TransactionFailure ?? "The global extension deployment transaction was rejected.";
                    await RecordFailureAsync(id, "deployment-failed", diagnostic, cancellationToken).ConfigureAwait(false);
                    results[id] = Failed(id, "deployment-failed", diagnostic);
                    continue;
                }

                ReplaceDirectory(candidates[id], Path.Combine(_home, "extension-repositories", id, "staged"));
                await _registry.RecordReconciliationSuccessAsync(id, commit, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                results[id] = new ExtensionRepositoryLifecycleResult(id, true, commit, null, null);
            }
        }
        finally
        {
            foreach (var candidate in candidates.Values)
                if (Directory.Exists(candidate)) Directory.Delete(candidate, recursive: true);
        }

        return new ExtensionLifecycleResult(targets.Select(item => results[item.Id]).ToArray());
    }

    private static bool IsSourceForRepository(string source, string id)
        => source.Equals($"repository:{id}", StringComparison.Ordinal)
            || source.StartsWith($"repository:{id}:", StringComparison.Ordinal);

    private Task RecordFailureAsync(string id, string name, string diagnostic, CancellationToken ct)
        => _registry.RecordReconciliationFailureAsync(id, name, diagnostic, ct);

    private static ExtensionRepositoryLifecycleResult Failed(string id, string name, string diagnostic)
        => new(id, false, null, name, diagnostic);

    private static void ReplaceDirectory(string candidate, string destination)
    {
        var backup = destination + $".backup-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination)) Directory.Move(destination, backup);
        try { Directory.Move(candidate, destination); }
        catch
        {
            if (Directory.Exists(backup)) Directory.Move(backup, destination);
            throw;
        }
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
    }

    private static IEnumerable<ExtensionDeploymentSource> DiscoverInTreeSources(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "src", "extensions");
        if (!Directory.Exists(root)) yield break;
        foreach (var project in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var projectDirectory = Path.GetDirectoryName(project)!;
            var manifest = Path.Combine(projectDirectory, "botnexus-extension.json");
            if (!File.Exists(manifest)) continue;
            var bin = Path.Combine(projectDirectory, "bin", "Release");
            if (!Directory.Exists(bin)) continue;
            var output = Directory.GetDirectories(bin)
                .Where(path => Path.GetFileName(path).StartsWith("net", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            if (output is not null)
                yield return new ExtensionDeploymentSource($"in-tree:{Path.GetFileNameWithoutExtension(project)}", output, true, false, manifest);
        }
    }

    private static IEnumerable<ExtensionDeploymentSource> DiscoverRegisteredSources(
        string home,
        IEnumerable<ExtensionRepositoryRegistrationInfo> registrations,
        IReadOnlyDictionary<string, string> candidates)
    {
        foreach (var registration in registrations.Where(item => item.Enabled))
        {
            var sourceName = $"repository:{registration.Id}";
            var staged = candidates.TryGetValue(registration.Id, out var candidate)
                ? candidate
                : Path.Combine(home, "extension-repositories", registration.Id, "staged");
            if (!Directory.Exists(staged))
            {
                yield return new ExtensionDeploymentSource(sourceName, staged, true, true);
                continue;
            }

            var outputs = File.Exists(Path.Combine(staged, "botnexus-extension.json"))
                ? [staged]
                : Directory.GetDirectories(staged).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (outputs.Length == 0)
            {
                yield return new ExtensionDeploymentSource(sourceName, staged, true, true);
                continue;
            }
            foreach (var output in outputs)
                yield return new ExtensionDeploymentSource($"{sourceName}:{Path.GetFileName(output)}", output, true, true);
        }
    }
}

/// <summary>Portable cross-process exclusive lock scoped to one BotNexus home.</summary>
internal sealed class ExtensionReconciliationFileLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;

    /// <summary>Creates the lock in the installation's state directory.</summary>
    public ExtensionReconciliationFileLock(string home) => _path = Path.Combine(Path.GetFullPath(home), "extension-reconciliation.lock");

    /// <summary>Waits until both the process-local and operating-system file locks are held.</summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var processLock = ProcessLocks.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Lease(stream, processLock);
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch { processLock.Release(); throw; }
    }

    private sealed class Lease(FileStream stream, SemaphoreSlim processLock) : IAsyncDisposable
    {
        private FileStream? _stream = stream;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            processLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
