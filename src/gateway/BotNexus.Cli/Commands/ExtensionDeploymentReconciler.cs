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

/// <summary>
/// Validates extension outputs before live mutation, then replaces each deployment directory through
/// a same-parent staging rename. Registered validation failures retain source-owned last-known-good
/// directories so stale pruning cannot turn an unavailable build into an undeploy.
/// </summary>
internal static partial class ExtensionDeploymentReconciler
{
    private const string ManifestFileName = "botnexus-extension.json";
    private const string SourceMarkerFileName = ".botnexus-deployment-source";
    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };

    internal static ExtensionDeploymentResult Reconcile(
        string liveRoot,
        IReadOnlyCollection<ExtensionDeploymentSource> sources,
        Action<string, string>? beforeActivate = null)
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
        var deployedIds = new HashSet<string>(retainedSources.Keys, StringComparer.OrdinalIgnoreCase);
        var deployedCount = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                DeployAtomically(liveRoot, candidate, beforeActivate);
                deployedIds.Add(candidate.Manifest.Id);
                deployedCount++;
            }
            catch (Exception ex) when (candidate.Source.Registered && ex is IOException or UnauthorizedAccessException or InvalidOperationException)
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
            if (name.StartsWith(".deploy-", StringComparison.Ordinal) || deployedIds.Contains(name))
                continue;

            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
        Action<string, string>? beforeActivate)
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
                Directory.Move(destination, backup);

            try
            {
                beforeActivate?.Invoke(staged, destination);
                Directory.Move(staged, destination);
            }
            catch
            {
                if (Directory.Exists(backup))
                {
                    if (Directory.Exists(destination))
                        Directory.Delete(destination, recursive: true);
                    Directory.Move(backup, destination);
                }
                throw;
            }

            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
        }
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
