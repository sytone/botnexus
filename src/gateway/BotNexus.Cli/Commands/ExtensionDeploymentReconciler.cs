using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Extensions;

namespace BotNexus.Cli.Commands;

/// <summary>Describes one already-built extension output eligible for deployment.</summary>
internal sealed record ExtensionDeploymentSource(
    string Source,
    string OutputDirectory,
    bool Enabled,
    bool Registered,
    string? ManifestPath = null);

internal sealed record ExtensionDeploymentFailure(string Source, string Message);

internal sealed record ExtensionDeploymentResult(
    int DeployedCount,
    IReadOnlyCollection<string> DeployedIds,
    IReadOnlyList<ExtensionDeploymentFailure> Failures);

internal enum ExtensionDeploymentOperation
{
    Backup,
    Activate,
    Rollback,
    Cleanup
}

internal sealed class ExtensionDeploymentHooks
{
    internal Action<ExtensionDeploymentOperation, string, string?>? BeforeOperation { get; init; }
    internal Func<bool>? IsWindows { get; init; }
    internal Action<TimeSpan>? Delay { get; init; }
}

/// <summary>
/// Validates extension outputs before live mutation, then replaces each deployment directory through
/// a same-parent staging rename. Registered validation failures retain source-owned last-known-good
/// directories so stale pruning cannot turn an unavailable build into an undeploy.
/// </summary>
internal static partial class ExtensionDeploymentReconciler
{
    private const int MaximumAttempts = 4;
    private const string ManifestFileName = "botnexus-extension.json";
    private const string SourceMarkerFileName = ".botnexus-deployment-source";
    private const string DeploymentLockFileName = ".deployment.lock";
    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };

    internal static ExtensionDeploymentResult Reconcile(
        string liveRoot,
        IReadOnlyCollection<ExtensionDeploymentSource> sources,
        ExtensionDeploymentHooks? hooks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveRoot);
        ArgumentNullException.ThrowIfNull(sources);

        var active = sources.Where(source => source.Enabled).ToArray();
        var candidates = new List<ValidatedSource>(active.Length);
        var failures = new List<ExtensionDeploymentFailure>();
        var retainedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in active)
        {
            try
            {
                candidates.Add(Validate(source));
            }
            catch (Exception ex) when (source.Registered && ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                failures.Add(new ExtensionDeploymentFailure(source.Source, ex.Message));
                AddPreviouslyDeployedIds(liveRoot, source.Source, retainedSources);
            }
        }

        var sourcesById = candidates
            .GroupBy(candidate => candidate.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Source.Source).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var retained in retainedSources)
        {
            if (!sourcesById.TryGetValue(retained.Key, out var sourceNames))
                sourcesById[retained.Key] = [retained.Value];
            else if (!sourceNames.Contains(retained.Value, StringComparer.Ordinal))
                sourceNames.Add(retained.Value);
        }

        var collisions = sourcesById.Where(item => item.Value.Count > 1).ToArray();
        if (collisions.Length > 0)
        {
            var details = collisions.Select(item =>
                $"extension id '{item.Key}' is supplied by {string.Join(" and ", item.Value.Select(source => $"'{source}'"))}");
            throw new InvalidOperationException($"Extension source collision: {string.Join("; ", details)}.");
        }

        Directory.CreateDirectory(liveRoot);
        using var deploymentLock = TryAcquireDeploymentLock(liveRoot, failures);
        if (deploymentLock is null)
            return new ExtensionDeploymentResult(0, retainedSources.Keys.ToArray(), failures);

        var deployedIds = new HashSet<string>(retainedSources.Keys, StringComparer.OrdinalIgnoreCase);
        var retainedResidue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deployedCount = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                DeployAtomically(liveRoot, candidate, hooks, failures, retainedResidue);
                deployedIds.Add(candidate.Manifest.Id);
                deployedCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add(new ExtensionDeploymentFailure(candidate.Source.Source, ex.Message));
                var retainedAfterFailure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddPreviouslyDeployedIds(liveRoot, candidate.Source.Source, retainedAfterFailure);
                deployedIds.UnionWith(retainedAfterFailure.Keys);
            }
        }

        foreach (var directory in Directory.GetDirectories(liveRoot))
        {
            var name = Path.GetFileName(directory);
            if (deployedIds.Contains(name) || retainedResidue.Contains(directory))
                continue;

            if (!TryDeleteWithRetry(directory, hooks, out var cleanupFailure))
            {
                failures.Add(new ExtensionDeploymentFailure(
                    name.StartsWith(".deploy-", StringComparison.Ordinal) ? "deployment-cleanup" : name,
                    $"Could not remove retained deployment residue '{directory}': {cleanupFailure!.Message}"));
            }
        }

        return new ExtensionDeploymentResult(deployedCount, deployedIds.ToArray(), failures);
    }

    private static ValidatedSource Validate(ExtensionDeploymentSource source)
    {
        if (!Directory.Exists(source.OutputDirectory))
            throw new InvalidOperationException($"Built output for '{source.Source}' does not exist.");

        var manifestPath = source.ManifestPath ?? Path.Combine(source.OutputDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Built output for '{source.Source}' is missing {ManifestFileName}.");

        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(manifestPath), ManifestOptions)
            ?? throw new InvalidOperationException($"Manifest for '{source.Source}' could not be deserialized.");
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidOperationException($"Manifest for '{source.Source}' must define a non-empty id.");
        if (!ExtensionIdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidOperationException($"Manifest for '{source.Source}' has an invalid id.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidOperationException($"Manifest for '{source.Source}' must define name.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException($"Manifest for '{source.Source}' must define version.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            throw new InvalidOperationException($"Manifest for '{source.Source}' must define entryAssembly.");
        var extensionTypes = manifest.ExtensionTypes ?? [];
        if (extensionTypes.Count == 0)
            throw new InvalidOperationException($"Manifest for '{source.Source}' must define at least one extension type.");
        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "channel", "isolation", "session-store", "auth-handler", "router", "agent-registry",
            "agent-supervisor", "agent-communicator", "activity-broadcaster", "tool", "command",
            "hook-handler", "media-handler", "endpoint-contributor", "api-contributor"
        };
        var invalidTypes = extensionTypes.Where(type => !allowedTypes.Contains(type)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalidTypes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Manifest for '{source.Source}' declares unsupported extensionTypes: {string.Join(", ", invalidTypes)}.");
        }
        if (Path.IsPathRooted(manifest.EntryAssembly)
            || manifest.EntryAssembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Manifest for '{source.Source}' has an invalid entryAssembly.");
        }

        var outputRoot = Path.GetFullPath(source.OutputDirectory);
        var entryAssembly = Path.GetFullPath(Path.Combine(outputRoot, manifest.EntryAssembly));
        if (!IsWithin(outputRoot, entryAssembly) || !File.Exists(entryAssembly))
            throw new InvalidOperationException($"Entry assembly '{manifest.EntryAssembly}' for '{source.Source}' does not exist in its built output.");

        return new ValidatedSource(source, manifest, Path.GetFullPath(manifestPath));
    }

    private static void DeployAtomically(
        string liveRoot,
        ValidatedSource candidate,
        ExtensionDeploymentHooks? hooks,
        List<ExtensionDeploymentFailure> failures,
        HashSet<string> retainedResidue)
    {
        var token = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(liveRoot, $".deploy-{token}");
        var staged = Path.Combine(stagingRoot, candidate.Manifest.Id);
        var destination = Path.Combine(liveRoot, candidate.Manifest.Id);
        var backup = Path.Combine(liveRoot, $".deploy-{token}-backup");
        Directory.CreateDirectory(staged);

        try
        {
            CopyTree(candidate.Source.OutputDirectory, staged);
            File.Copy(candidate.ManifestPath, Path.Combine(staged, ManifestFileName), overwrite: true);
            File.WriteAllText(Path.Combine(staged, SourceMarkerFileName), candidate.Source.Source);

            if (Directory.Exists(destination))
                MoveWithRetry(destination, backup, ExtensionDeploymentOperation.Backup, hooks);

            try
            {
                MoveWithRetry(staged, destination, ExtensionDeploymentOperation.Activate, hooks);
            }
            catch (Exception activationFailure)
            {
                if (!Directory.Exists(backup))
                {
                    throw new InvalidOperationException(
                        $"Extension activation failed for '{destination}'; no prior deployment existed: {activationFailure.Message}",
                        activationFailure);
                }

                try
                {
                    if (Directory.Exists(destination))
                        DeleteWithRetry(destination, ExtensionDeploymentOperation.Rollback, hooks);
                    MoveWithRetry(backup, destination, ExtensionDeploymentOperation.Rollback, hooks);
                }
                catch (Exception rollbackFailure)
                {
                    throw new InvalidOperationException(
                        $"Extension activation failed for '{destination}', and restoring its prior deployment from retained backup '{backup}' also failed: {rollbackFailure.Message}",
                        new AggregateException(activationFailure, rollbackFailure));
                }

                throw new InvalidOperationException(
                    $"Extension activation failed for '{destination}'; the prior deployment was restored: {activationFailure.Message}",
                    activationFailure);
            }

            if (Directory.Exists(backup))
                CleanupOrReport(backup, candidate.Source.Source, hooks, failures, retainedResidue);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                CleanupOrReport(stagingRoot, candidate.Source.Source, hooks, failures, retainedResidue);
        }
    }

    private static FileStream? TryAcquireDeploymentLock(string liveRoot, List<ExtensionDeploymentFailure> failures)
    {
        var lockPath = Path.Combine(liveRoot, DeploymentLockFileName);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new ExtensionDeploymentFailure(
                "extension-deployment",
                $"Extension reconciliation is already in progress or its lock is unavailable at '{lockPath}': {ex.Message}"));
            return null;
        }
    }

    private static void MoveWithRetry(string source, string destination, ExtensionDeploymentOperation operation, ExtensionDeploymentHooks? hooks)
        => ExecuteWithRetry(operation, source, destination, () => Directory.Move(source, destination), hooks);

    private static void DeleteWithRetry(string path, ExtensionDeploymentOperation operation, ExtensionDeploymentHooks? hooks)
        => ExecuteWithRetry(operation, path, null, () => Directory.Delete(path, recursive: true), hooks);

    private static bool TryDeleteWithRetry(string path, ExtensionDeploymentHooks? hooks, out Exception? failure)
    {
        try
        {
            DeleteWithRetry(path, ExtensionDeploymentOperation.Cleanup, hooks);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
            return false;
        }
    }

    private static void CleanupOrReport(
        string path,
        string source,
        ExtensionDeploymentHooks? hooks,
        List<ExtensionDeploymentFailure> failures,
        HashSet<string> retainedResidue)
    {
        if (TryDeleteWithRetry(path, hooks, out var cleanupFailure))
            return;

        retainedResidue.Add(path);
        failures.Add(new ExtensionDeploymentFailure(
            source,
            $"Could not remove retained deployment residue '{path}': {cleanupFailure!.Message}"));
    }

    private static void ExecuteWithRetry(
        ExtensionDeploymentOperation operation,
        string path,
        string? destination,
        Action action,
        ExtensionDeploymentHooks? hooks)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                hooks?.BeforeOperation?.Invoke(operation, path, destination);
                action();
                return;
            }
            catch (Exception ex) when (attempt < MaximumAttempts && IsWindowsTransient(ex, hooks))
            {
                var delay = TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1)));
                (hooks?.Delay ?? Thread.Sleep)(delay);
            }
        }
    }

    private static bool IsWindowsTransient(Exception exception, ExtensionDeploymentHooks? hooks)
    {
        var isWindows = hooks?.IsWindows?.Invoke()
            ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (!isWindows || exception is not IOException and not UnauthorizedAccessException)
            return false;

        var error = exception.HResult & 0xffff;
        return error is 5 or 32 or 33;
    }

    private static void CopyTree(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static void AddPreviouslyDeployedIds(
        string liveRoot,
        string source,
        Dictionary<string, string> sourcesById)
    {
        if (!Directory.Exists(liveRoot))
            return;

        foreach (var directory in Directory.GetDirectories(liveRoot))
        {
            var marker = Path.Combine(directory, SourceMarkerFileName);
            if (File.Exists(marker) && string.Equals(File.ReadAllText(marker), source, StringComparison.Ordinal))
                sourcesById[Path.GetFileName(directory)] = source;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionIdPattern();

    private sealed record ValidatedSource(
        ExtensionDeploymentSource Source,
        ExtensionManifest Manifest,
        string ManifestPath);
}
