using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Deploys the prebuilt gateway extension carried by a plugin into the extensions root, where the
/// gateway's own loader discovers it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deployment is a copy, never a build.</b> Plugins are promoted verbatim, so a carried
/// extension is already compiled. This type validates the carried manifest, then copies its
/// directory; it never invokes a compiler and never loads an assembly.
/// </para>
/// <para>
/// <b>It refuses to write over a directory that already exists.</b> Install runs inside the
/// gateway process, which has the assemblies of every loaded extension mapped. Overwriting one in
/// place fails with <c>IOException: ... because it is being used by another process</c> - the same
/// failure ordinary redeploys hit when a live gateway holds its extension DLLs. Creating a
/// directory that did not exist cannot conflict, so first install is safe; replacing one is not,
/// and is refused here rather than half-applied. Update and uninstall of a deployed extension are
/// a separate staged-swap slice.
/// </para>
/// </remarks>
public sealed class PluginExtensionDeployer
{
    /// <summary>Conventional file name of an extension manifest.</summary>
    public const string ExtensionManifestFileName = "botnexus-extension.json";

    /// <summary>
    /// Plugin-domain directories never copied into a deployed extension. The extension manifest
    /// commonly sits at the plugin root, which would otherwise drag the plugin's own metadata and
    /// its skills into the extensions tree, where nothing reads them and a stale copy would
    /// outlive the plugin.
    /// </summary>
    private static readonly string[] ExcludedTopLevelDirectories =
        [".botnexus-plugin", "skills", ".git"];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<PluginExtensionDeployer> _logger;

    /// <summary>Creates a deployer.</summary>
    /// <param name="logger">Optional logger; deployment is silent when omitted.</param>
    public PluginExtensionDeployer(ILogger<PluginExtensionDeployer>? logger = null)
    {
        _logger = logger ?? NullLogger<PluginExtensionDeployer>.Instance;
    }

    /// <summary>
    /// Validates and deploys the extension a plugin carries.
    /// </summary>
    /// <param name="pluginName">Installing plugin, used only to name failures.</param>
    /// <param name="pluginDirectory">Directory the plugin was promoted into.</param>
    /// <param name="reference">The manifest's <c>extension</c> block.</param>
    /// <param name="extensionsRoot">Directory holding deployed extensions.</param>
    /// <returns>
    /// The deployed extension id and the files written, or a failure naming the offending field.
    /// </returns>
    public PluginExtensionDeployResult Deploy(
        string pluginName,
        string pluginDirectory,
        PluginExtensionRef reference,
        string extensionsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsRoot);

        if (string.IsNullOrWhiteSpace(reference.Manifest))
        {
            return Fail(pluginName, "extension.manifest", "A carried extension must name its manifest path.");
        }

        // Contain the declared path inside the plugin directory. A manifest path is author-supplied
        // and reaches here straight from a cloned repository, so "../../.ssh" must be impossible
        // rather than merely unlikely.
        var pluginRoot = Path.GetFullPath(pluginDirectory);
        var manifestPath = Path.GetFullPath(Path.Combine(pluginRoot, reference.Manifest));
        if (!IsInside(pluginRoot, manifestPath))
        {
            return Fail(
                pluginName,
                "extension.manifest",
                $"Carried extension manifest path '{reference.Manifest}' resolves outside the plugin directory.");
        }

        if (!File.Exists(manifestPath))
        {
            return Fail(
                pluginName,
                "extension.manifest",
                $"Carried extension manifest '{reference.Manifest}' was not found in the plugin.");
        }

        CarriedExtensionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CarriedExtensionManifest>(
                File.ReadAllText(manifestPath), SerializerOptions);
        }
        catch (JsonException ex)
        {
            return Fail(
                pluginName,
                "extension.manifest",
                $"Carried extension manifest '{reference.Manifest}' is not valid JSON: {ex.Message}");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
        {
            return Fail(
                pluginName,
                "extension.manifest",
                $"Carried extension manifest '{reference.Manifest}' declares no 'id'.");
        }

        // The id names a directory under the extensions root. Anything that is not a single plain
        // segment could escape it or collide with a parent.
        if (!IsSafeSegment(manifest.Id))
        {
            return Fail(
                pluginName,
                "extension.id",
                $"Carried extension id '{manifest.Id}' is not a valid directory name.");
        }

        var sourceDirectory = Path.GetDirectoryName(manifestPath)!;

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            return Fail(
                pluginName,
                "extension.entryAssembly",
                $"Carried extension '{manifest.Id}' declares no 'entryAssembly'.");
        }

        // Prebuilt means prebuilt: catch a plugin that declared an extension but never committed
        // its build output, at install, rather than at the next gateway start.
        var entryAssemblyPath = Path.GetFullPath(Path.Combine(sourceDirectory, manifest.EntryAssembly));
        if (!IsInside(sourceDirectory, entryAssemblyPath) || !File.Exists(entryAssemblyPath))
        {
            return Fail(
                pluginName,
                "extension.entryAssembly",
                $"Carried extension '{manifest.Id}' names entry assembly '{manifest.EntryAssembly}', which is not present in the plugin. A carried extension must be prebuilt and committed.");
        }

        // Third-party code must not land ahead of the gateway's authentication. Contributors map
        // before it by default - that is where they have always mapped, and the portal shell needs
        // it to serve the page a user authenticates from - so a plugin that says nothing would
        // inherit an unauthenticated position by accident. Requiring the declaration makes the
        // placement a decision its author had to make rather than one they fell into.
        if (!IsAfterAuthentication(manifest.EndpointPhase))
        {
            return Fail(
                pluginName,
                "extension.endpointPhase",
                $"Carried extension '{manifest.Id}' must declare \"endpointPhase\": \"after-authentication\" so its routes sit behind the gateway's authentication. Extensions mapping ahead of authentication cannot be installed as plugins.");
        }

        var destination = Path.Combine(extensionsRoot, manifest.Id);
        if (Directory.Exists(destination))
        {
            return Fail(
                pluginName,
                "extension.id",
                $"Extension '{manifest.Id}' is already deployed at '{destination}'. It was not overwritten: a deployed extension's assemblies are loaded by the running gateway and replacing them in place would fail. Remove it and restart first.");
        }

        var written = Copy(sourceDirectory, destination, isPluginRoot: PathsEqual(sourceDirectory, pluginRoot));

        _logger.LogInformation(
            "Deployed extension {ExtensionId} carried by plugin {Plugin} ({FileCount} files). A gateway restart is required to activate it.",
            manifest.Id,
            pluginName,
            written.Count);

        return new PluginExtensionDeployResult
        {
            Succeeded = true,
            ExtensionId = manifest.Id,
            Files = written,
        };
    }

    /// <summary>
    /// Reads what a carried extension would contribute, for disclosure BEFORE an operator consents.
    /// </summary>
    /// <remarks>
    /// Best effort by design: this runs on content that has not been validated yet, and a manifest
    /// too malformed to describe is not a reason to fail the install differently - the deploy step
    /// will reject it in a moment with a message naming the actual field. Returning null here means
    /// only that the consent prompt cannot be specific.
    /// </remarks>
    /// <param name="pluginDirectory">Directory holding the staged plugin content.</param>
    /// <param name="reference">The manifest's <c>extension</c> block.</param>
    public static CarriedExtensionManifest? Describe(string pluginDirectory, PluginExtensionRef? reference)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory)
            || reference is null
            || string.IsNullOrWhiteSpace(reference.Manifest))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(pluginDirectory);
            var manifestPath = Path.GetFullPath(Path.Combine(root, reference.Manifest));
            if (!IsInside(root, manifestPath) || !File.Exists(manifestPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CarriedExtensionManifest>(
                File.ReadAllText(manifestPath), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// One sentence naming what a carried extension contributes, or <c>null</c> when nothing could
    /// be read. Used to make the consent prompt specific rather than generic.
    /// </summary>
    /// <param name="manifest">Carried extension manifest, as read by <see cref="Describe"/>.</param>
    public static string? SummariseContributions(CarriedExtensionManifest? manifest)
    {
        if (manifest is null)
        {
            return null;
        }

        List<string> parts = [];

        if (manifest.ExtensionTypes is { Count: > 0 } types)
        {
            parts.Add(string.Join(", ", types));
        }

        foreach (var nav in manifest.Nav ?? [])
        {
            if (!string.IsNullOrWhiteSpace(nav.Path))
            {
                parts.Add(string.IsNullOrWhiteSpace(nav.Label)
                    ? $"a menu entry at {nav.Path}"
                    : $"a menu entry '{nav.Label}' at {nav.Path}");
            }
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Removes a deployed extension's directory. Intended for rolling back a failed install, where
    /// the content was written moments earlier and cannot yet be loaded by anything.
    /// </summary>
    /// <param name="extensionsRoot">Directory holding deployed extensions.</param>
    /// <param name="extensionId">Deployed extension id.</param>
    public void TryRemove(string extensionsRoot, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionsRoot) || !IsSafeSegment(extensionId))
        {
            return;
        }

        var directory = Path.Combine(extensionsRoot, extensionId);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not remove deployed extension directory {Directory}. It may be held by the running gateway.",
                directory);
        }
    }

    private static List<string> Copy(string source, string destination, bool isPluginRoot)
    {
        Directory.CreateDirectory(destination);
        var written = new List<string>();

        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceFile).Replace(Path.DirectorySeparatorChar, '/');

            // Only when the extension manifest sits at the plugin root do plugin-domain directories
            // share the tree; an extension in its own subdirectory has nothing to exclude.
            if (isPluginRoot && IsExcluded(relative))
            {
                continue;
            }

            var targetPath = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(sourceFile, targetPath, overwrite: true);
            written.Add(relative);
        }

        written.Sort(StringComparer.Ordinal);
        return written;
    }

    private static bool IsExcluded(string relativePath) =>
        ExcludedTopLevelDirectories.Any(dir =>
            relativePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase));

    // Mirrors the gateway's own lenient parse: "after-authentication", "afterauthentication" and
    // "AfterAuthentication" all count. Anything else, including absent, does not.
    private static bool IsAfterAuthentication(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Equals("afterauthentication", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value != "."
        && value != ".."
        && !value.Contains('/', StringComparison.Ordinal)
        && !value.Contains('\\', StringComparison.Ordinal);

    private static bool IsInside(string root, string candidate)
    {
        var normalisedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalisedCandidate = Path.GetFullPath(candidate);
        return normalisedCandidate.Equals(normalisedRoot, PathComparison)
            || normalisedCandidate.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), PathComparison);

    // Linux is the deployment target and its filesystem is case sensitive; matching that keeps a
    // containment check from accepting a path the OS would treat as different.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static PluginExtensionDeployResult Fail(string pluginName, string field, string message) =>
        new()
        {
            Succeeded = false,
            PluginName = pluginName,
            Field = field,
            Message = message,
        };
}
