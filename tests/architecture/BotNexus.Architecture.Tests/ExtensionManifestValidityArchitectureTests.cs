using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fitness function guarding every shipped extension manifest against the rules enforced by
/// <c>AssemblyLoadContextExtensionLoader.ValidateManifest</c>.
/// </summary>
/// <remarks>
/// Issue #2365: three manifests (<c>botnexus-data-store</c>, <c>botnexus-debug-tool</c>,
/// <c>botnexus-qmd</c>) shipped without <c>entryAssembly</c> or <c>extensionTypes</c>. The loader
/// logs a warning and skips them, so the failure was silent and the tools were never available to
/// any agent. These tests fail the build instead of failing quietly at runtime.
/// </remarks>
public sealed class ExtensionManifestValidityArchitectureTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Extension types accepted by the gateway loader.</summary>
    private static readonly HashSet<string> AllowedExtensionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "channel",
        "isolation",
        "session-store",
        "auth-handler",
        "router",
        "agent-registry",
        "agent-supervisor",
        "agent-communicator",
        "activity-broadcaster",
        "tool",
        "command",
        "hook-handler",
        "media-handler",
        "endpoint-contributor",
        "api-contributor"
    };

    /// <summary>Every extension project must ship a manifest next to its csproj.</summary>
    [Fact]
    public void EveryExtensionProject_HasManifest()
    {
        // Only projects that already ship a manifest are in scope; some directories under
        // src/extensions are support libraries (Blazor clients, TUI host) that are not
        // independently loadable extensions and legitimately have no manifest.
        var missing = EnumerateExtensionProjects()
            .Select(project => Path.GetDirectoryName(project)!)
            .Where(IsLoadableExtensionDirectory)
            .Where(directory => !File.Exists(Path.Combine(directory, "botnexus-extension.json")))
            .Select(Path.GetFileName)
            .ToArray();

        missing.ShouldBeEmpty(
            "Extension projects without a botnexus-extension.json manifest: " + string.Join(", ", missing));
    }

    /// <summary>
    /// A directory is a loadable extension when its csproj declares the dynamic-loading/managed
    /// closure properties the gateway ALC requires. Support libraries do not.
    /// </summary>
    private static bool IsLoadableExtensionDirectory(string directory)
    {
        var csproj = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj is null)
            return false;

        var xml = File.ReadAllText(csproj);
        return Regex.IsMatch(xml, @"<CopyLocalLockFileAssemblies>\s*true\s*</CopyLocalLockFileAssemblies>", RegexOptions.IgnoreCase)
            || Regex.IsMatch(xml, @"<EnableDynamicLoading>\s*true\s*</EnableDynamicLoading>", RegexOptions.IgnoreCase);
    }

    /// <summary>Every manifest must satisfy the loader's validation rules.</summary>
    [Fact]
    public void EveryExtensionManifest_PassesLoaderValidation()
    {
        var manifests = EnumerateManifests();
        manifests.ShouldNotBeEmpty("No extension manifests were discovered under src/extensions.");

        var failures = new List<string>();

        foreach (var manifestPath in manifests)
        {
            var relative = Path.GetRelativePath(RepoRoot, manifestPath);
            ManifestRecord? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ManifestRecord>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            }
            catch (JsonException ex)
            {
                failures.Add($"{relative}: manifest is not valid JSON ({ex.Message}).");
                continue;
            }

            if (manifest is null)
            {
                failures.Add($"{relative}: manifest deserialized to null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(manifest.Id))
                failures.Add($"{relative}: must define a non-empty id.");
            if (string.IsNullOrWhiteSpace(manifest.Name))
                failures.Add($"{relative}: must define a non-empty name.");
            if (string.IsNullOrWhiteSpace(manifest.Version))
                failures.Add($"{relative}: must define a non-empty version.");

            if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            {
                failures.Add($"{relative}: must define entryAssembly.");
            }
            else
            {
                if (manifest.EntryAssembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    failures.Add($"{relative}: entryAssembly contains invalid file name characters.");
                if (Path.IsPathRooted(manifest.EntryAssembly))
                    failures.Add($"{relative}: entryAssembly cannot be an absolute path.");
                if (!manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{relative}: entryAssembly must be a .dll filename.");

                var expected = ExpectedAssemblyFileName(manifestPath);
                if (expected is not null &&
                    !string.Equals(expected, manifest.EntryAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{relative}: entryAssembly '{manifest.EntryAssembly}' does not match the project assembly '{expected}'.");
                }
            }

            var extensionTypes = manifest.ExtensionTypes ?? [];
            if (extensionTypes.Count == 0)
            {
                failures.Add($"{relative}: must define at least one extension type.");
            }
            else
            {
                var invalid = extensionTypes
                    .Where(type => !AllowedExtensionTypes.Contains(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (invalid.Length > 0)
                    failures.Add($"{relative}: declares unsupported extensionTypes: {string.Join(", ", invalid)}.");
            }
        }

        failures.ShouldBeEmpty(
            "Extension manifests fail gateway loader validation and would be silently skipped at startup:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>Manifest ids must be unique — duplicates collide in the loader's registry.</summary>
    [Fact]
    public void ExtensionManifestIds_AreUnique()
    {
        var duplicates = EnumerateManifests()
            .Select(path => JsonSerializer.Deserialize<ManifestRecord>(File.ReadAllText(path), ManifestJsonOptions)?.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        duplicates.ShouldBeEmpty("Duplicate extension manifest ids: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// Manifests must not carry keys that <c>ExtensionManifest</c> cannot bind, other than the
    /// documentation-only <c>description</c> mandated by <c>src/extensions/AGENTS.md</c>. Unbindable
    /// keys read as behaviour but deserialize to nothing — the inert <c>optional</c> and
    /// <c>enabledByDefault</c> in the QMD manifest were exactly this trap (#2365).
    /// </summary>
    [Fact]
    public void ExtensionManifests_DeclareNoInertKeys()
    {
        // 'description' has no ExtensionManifest member but is required by src/extensions/AGENTS.md
        // and is purely documentary — it declares no behaviour, so it cannot mislead.
        var bindable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "name", "description", "version", "entryAssembly",
            "extensionTypes", "dependencies", "enabled", "configSchema"
        };

        var offenders = new List<string>();
        foreach (var manifestPath in EnumerateManifests())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                continue;

            var unknown = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !bindable.Contains(name))
                .ToArray();

            if (unknown.Length > 0)
                offenders.Add($"{Path.GetRelativePath(RepoRoot, manifestPath)}: {string.Join(", ", unknown)}");
        }

        offenders.ShouldBeEmpty(
            "Extension manifests declare keys ExtensionManifest cannot bind (they are silently ignored):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> EnumerateManifests() =>
        Directory.EnumerateFiles(ExtensionsRoot, "botnexus-extension.json", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal);

    private static IEnumerable<string> EnumerateExtensionProjects() =>
        Directory.EnumerateDirectories(ExtensionsRoot)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal);

    private static bool IsBuildOutput(string path)
    {
        var relative = Path.GetRelativePath(ExtensionsRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the DLL filename the sibling csproj produces: explicit &lt;AssemblyName&gt; when
    /// present, otherwise the project filename. Returns null when no csproj sits beside the manifest.
    /// </summary>
    private static string? ExpectedAssemblyFileName(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        var csproj = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj is null)
            return null;

        var xml = File.ReadAllText(csproj);
        var match = Regex.Match(xml, @"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>", RegexOptions.IgnoreCase);
        var assemblyName = match.Success
            ? match.Groups[1].Value
            : Path.GetFileNameWithoutExtension(csproj);

        return assemblyName + ".dll";
    }

    private static string ExtensionsRoot => Path.Combine(RepoRoot, "src", "extensions");

    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }

    private sealed record ManifestRecord
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string EntryAssembly { get; init; } = string.Empty;
        public IReadOnlyList<string> ExtensionTypes { get; init; } = [];
    }
}
