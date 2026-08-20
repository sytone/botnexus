using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function guarding the container packaging contract for extensions (#2376).
///
/// <para>
/// <b>The defect.</b> The published image ran a single publish step
/// (<c>dotnet publish src/gateway/BotNexus.Gateway.Api/...</c>) and nothing else. Extensions are
/// discovered at runtime by scanning a probe directory for per-extension folders containing a
/// <c>botnexus-extension.json</c> manifest, and the probe directory defaulted to
/// <c>{BOTNEXUS_HOME}/extensions</c> = <c>/app/config/extensions</c>. On a stock
/// <c>docker run</c> that path does not exist, so the gateway logged
/// <c>"Extensions directory '/app/config/extensions' does not exist. Skipping discovery."</c>
/// and booted with zero extensions: no <c>MapHub&lt;GatewayHub&gt;("/hub/gateway")</c> (that lives
/// in the SignalR channel extension), therefore no portal and no realtime channel, and
/// <c>GET /api/extensions</c> returned <c>[]</c>. The image was green on <c>/health</c> and
/// structurally incapable of talking to an agent.
/// </para>
///
/// <para>
/// <b>Why a static fence.</b> CI cannot run Docker, and the existing gateway boot smoke gate
/// (<c>BotNexus.Integration.ExtensionBoot.Tests</c>, PR #2277) deploys the extension set itself via
/// <c>ServeCommand.DeployExtensions</c> before booting. That gate proves the extensions <i>load</i>;
/// it says nothing about whether the <i>image</i> contains them, which is exactly the gap that let
/// this ship. This fence closes that gap the same way
/// <see cref="DockerHealthcheckArchitectureTests"/> does: by parsing the Dockerfile text with zero
/// Docker dependency.
/// </para>
///
/// <para>
/// <b>Two independent conditions</b> — both are required, and each failed on its own would
/// reproduce the bug:
/// </para>
/// <list type="number">
///   <item><description>
///     The build stage must publish the extension projects into the image, not only the gateway.
///   </description></item>
///   <item><description>
///     The runtime stage must place them on a probe path that is NOT shadowed by a declared
///     <c>VOLUME</c>. Baking extensions under <c>/app/config</c> would look correct in the
///     Dockerfile and still yield an empty directory at runtime, because a caller's
///     <c>-v host:/app/config</c> mount hides the image content.
///   </description></item>
/// </list>
/// </summary>
public sealed class ContainerExtensionPackagingArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string DockerfilePath => Path.Combine(RepoRoot, "Dockerfile");

    /// <summary>The runtime probe-root environment variable honoured by the extension loader.</summary>
    private const string ExtensionsPathEnvVar = "BOTNEXUS_EXTENSIONS_PATH";

    /// <summary>
    /// The image must publish the extension projects, not only the gateway API. Without this the
    /// image physically contains no extension assemblies and no probe path can help.
    /// </summary>
    [Fact]
    public void Dockerfile_PublishesExtensionProjectsIntoTheImage()
    {
        var dockerfile = File.ReadAllText(DockerfilePath);

        var publishesExtensions =
            Regex.IsMatch(dockerfile, @"dotnet\s+publish[^\r\n]*src/extensions", RegexOptions.IgnoreCase)
            || Regex.IsMatch(dockerfile, @"src/extensions[^\r\n]*botnexus-extension\.json", RegexOptions.IgnoreCase);

        publishesExtensions.ShouldBeTrue(
            "The Dockerfile publishes no extension projects, so the resulting image ships zero extensions: "
            + "no SignalR channel (the only MapHub<GatewayHub>(\"/hub/gateway\") in the repo lives in "
            + "BotNexus.Extensions.Channels.SignalR), therefore no portal and no realtime channel, and "
            + "GET /api/extensions returns []. Add a build-stage step that publishes every project under "
            + "src/extensions that ships a botnexus-extension.json manifest. See issue #2376.\nFile: "
            + DockerfilePath);
    }

    /// <summary>
    /// The published extensions must reach the runtime stage — a build-stage publish that is never
    /// COPYed forward leaves the shipped image exactly as broken.
    /// </summary>
    [Fact]
    public void Dockerfile_CopiesPublishedExtensionsIntoTheRuntimeStage()
    {
        var dockerfile = File.ReadAllText(DockerfilePath);
        var runtimeStage = ExtractRuntimeStage(dockerfile);

        var copiesExtensions = Regex.IsMatch(
            runtimeStage,
            @"COPY\s+--from=[\w.-]+\s+[^\r\n]*extensions",
            RegexOptions.IgnoreCase);

        copiesExtensions.ShouldBeTrue(
            "The runtime stage never copies the published extensions out of the build stage, so the "
            + "shipped image contains no extension assemblies regardless of what the build stage produced. "
            + "Add a `COPY --from=build /app/extensions ./extensions` (or equivalent) to the runtime stage. "
            + "See issue #2376.\nFile: " + DockerfilePath);
    }

    /// <summary>
    /// The extension probe root must not sit under a declared <c>VOLUME</c>. This is the subtle half
    /// of #2376: baking extensions into <c>/app/config/extensions</c> reads as correct but is
    /// shadowed the moment a caller mounts their config, silently restoring the empty-extensions bug.
    /// </summary>
    [Fact]
    public void Dockerfile_ExtensionProbeRootIsNotShadowedByAVolume()
    {
        var dockerfile = File.ReadAllText(DockerfilePath);
        var probeRoot = ResolveProbeRoot(dockerfile);

        probeRoot.ShouldNotBeNull(
            $"The Dockerfile ships extensions but declares no {ExtensionsPathEnvVar}, so the loader falls back "
            + "to {BOTNEXUS_HOME}/extensions. BOTNEXUS_HOME is a declared VOLUME in this image, so that path is "
            + "empty on a stock `docker run` and every shipped extension is invisible. Set "
            + $"`ENV {ExtensionsPathEnvVar}=/app/extensions`. See issue #2376.\nFile: " + DockerfilePath);

        foreach (var volume in ExtractVolumePaths(dockerfile))
        {
            IsUnder(probeRoot!, volume).ShouldBeFalse(
                $"The extension probe root '{probeRoot}' is inside the declared VOLUME '{volume}'. Anything baked "
                + "there at build time is shadowed by the caller's bind mount, so the gateway discovers zero "
                + "extensions on a stock `docker run` even though the image contains them — the exact failure "
                + "mode of issue #2376. Move the probe root outside every declared VOLUME (e.g. /app/extensions)."
                + "\nFile: " + DockerfilePath);
        }
    }

    /// <summary>
    /// Every extension that ships a manifest must actually be reachable by the packaging step. A
    /// per-project publish loop keyed on manifest discovery satisfies this; an explicit hand-listed
    /// subset would silently omit newly added extensions, which is the class of drift that produced
    /// the original defect.
    /// </summary>
    [Fact]
    public void Dockerfile_PackagesEveryManifestedExtension()
    {
        var dockerfile = File.ReadAllText(DockerfilePath);
        var manifests = EnumerateManifestedExtensionDirectories();

        manifests.ShouldNotBeEmpty("No extension manifests were discovered under src/extensions.");

        // The packaging step must be manifest-driven (a loop over discovered manifests) rather than a
        // hardcoded list, otherwise a newly added extension is omitted from the image with no signal.
        var manifestDriven = Regex.IsMatch(
            dockerfile,
            @"botnexus-extension\.json",
            RegexOptions.IgnoreCase);

        if (manifestDriven)
            return;

        var missing = manifests
            .Select(Path.GetFileName)
            .Where(name => !dockerfile.Contains(name!, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        missing.ShouldBeEmpty(
            "The Dockerfile packages extensions from a hardcoded list that omits: "
            + string.Join(", ", missing)
            + ". Prefer a manifest-driven loop over src/extensions/**/botnexus-extension.json so new "
            + "extensions are packaged automatically. See issue #2376.\nFile: " + DockerfilePath);
    }

    /// <summary>
    /// Guards the manifest+assembly pairing the loader requires: discovery reads
    /// <c>botnexus-extension.json</c> and then demands the declared <c>entryAssembly</c> exist beside
    /// it (see #2365/PR #2366). Packaging that drops the manifest — several extension csprojs do not
    /// declare it as a <c>Content</c> item — yields an image whose extension folders are skipped.
    /// </summary>
    [Fact]
    public void Dockerfile_PackagingPreservesManifestsAlongsideEntryAssemblies()
    {
        var dockerfile = File.ReadAllText(DockerfilePath);

        // The publish output of a project only contains botnexus-extension.json when the csproj marks
        // it as Content/CopyToOutputDirectory. Several do not, so packaging must copy it explicitly.
        var projectsMissingContentItem = EnumerateManifestedExtensionDirectories()
            .Where(directory =>
            {
                var csproj = Directory
                    .EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                return csproj is not null
                    && !File.ReadAllText(csproj).Contains("botnexus-extension.json", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFileName)
            .ToArray();

        if (projectsMissingContentItem.Length == 0)
            return;

        var copiesManifestExplicitly = Regex.IsMatch(
            dockerfile,
            @"cp\s+[^\r\n]*botnexus-extension\.json",
            RegexOptions.IgnoreCase);

        copiesManifestExplicitly.ShouldBeTrue(
            "These extension projects do not declare botnexus-extension.json as a Content item, so it is "
            + "absent from their publish output: " + string.Join(", ", projectsMissingContentItem)
            + ". The container packaging step must copy the manifest into each extension folder explicitly, "
            + "otherwise discovery skips the folder entirely (it looks for the manifest first) and the "
            + "extension is silently missing from the image. See issues #2376 and #2365.\nFile: "
            + DockerfilePath);
    }

    /// <summary>
    /// The Blazor portal must survive `dotnet publish`, not merely `dotnet build` (#2376).
    ///
    /// <para>
    /// <c>SignalREndpointContributor</c> probes for <c>{extensionDir}/blazor</c> and silently skips
    /// portal registration when it is absent — no error, no warning that reaches a health check.
    /// The SignalR csproj bundles the WASM clients with <c>Copy</c> tasks into
    /// <c>$(OutputPath)</c>, which serves the build-based local deploy path. <c>dotnet publish</c>
    /// computes its own file list and does not sweep arbitrary <c>$(OutputPath)</c> subdirectories,
    /// so the published container shipped a working <c>/hub/gateway</c> and a <b>404 portal</b>.
    /// The fix registers the bundles as <c>ResolvedFileToPublish</c>; this fence stops a future edit
    /// from dropping back to build-only copies.
    /// </para>
    /// </summary>
    [Fact]
    public void SignalRExtension_BundlesBlazorClientsIntoPublishOutput()
    {
        var csproj = Path.Combine(
            RepoRoot, "src", "extensions", "BotNexus.Extensions.Channels.SignalR",
            "BotNexus.Extensions.Channels.SignalR.csproj");

        File.Exists(csproj).ShouldBeTrue($"SignalR extension project not found at {csproj}");
        var xml = File.ReadAllText(csproj);

        // Only relevant if the project actually bundles a Blazor client at all.
        if (!xml.Contains("blazor", StringComparison.OrdinalIgnoreCase))
            return;

        xml.Contains("ResolvedFileToPublish", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            "The SignalR extension bundles the Blazor WASM portal into $(OutputPath) with Copy tasks, but "
            + "never contributes those files to ResolvedFileToPublish. `dotnet publish` — which is how the "
            + "container image packages extensions — does not sweep arbitrary $(OutputPath) subdirectories, "
            + "so the published extension folder has no blazor/ directory. SignalREndpointContributor then "
            + "skips portal registration silently and the image serves a 404 at / while /hub/gateway works. "
            + "See issue #2376.\nFile: " + csproj);
    }

    private static string[] EnumerateManifestedExtensionDirectories()
    {
        var extensionsRoot = Path.Combine(RepoRoot, "src", "extensions");
        if (!Directory.Exists(extensionsRoot))
            return [];

        return Directory
            .EnumerateFiles(extensionsRoot, "botnexus-extension.json", SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Resolves the effective extension probe root declared by the Dockerfile, if any.</summary>
    private static string? ResolveProbeRoot(string dockerfile)
    {
        var match = Regex.Match(
            dockerfile,
            @"^\s*ENV\s+" + ExtensionsPathEnvVar + @"[=\s]+(?<value>\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        return match.Success ? NormalisePath(match.Groups["value"].Value.Trim('"')) : null;
    }

    private static string[] ExtractVolumePaths(string dockerfile)
    {
        var paths = new List<string>();
        foreach (Match match in Regex.Matches(
            dockerfile, @"^\s*VOLUME\s+(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            foreach (Match quoted in Regex.Matches(match.Groups["value"].Value, "\"(?<path>[^\"]+)\""))
                paths.Add(NormalisePath(quoted.Groups["path"].Value));

            if (!match.Groups["value"].Value.Contains('"'))
            {
                foreach (var token in match.Groups["value"].Value.Split(
                    [' ', '\t', '[', ']', ','], StringSplitOptions.RemoveEmptyEntries))
                {
                    paths.Add(NormalisePath(token));
                }
            }
        }

        return [.. paths.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Extracts the final (runtime) stage of a multi-stage Dockerfile.</summary>
    private static string ExtractRuntimeStage(string dockerfile)
    {
        var fromMatches = Regex.Matches(dockerfile, @"^\s*FROM\s+", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return fromMatches.Count == 0
            ? dockerfile
            : dockerfile[fromMatches[^1].Index..];
    }

    /// <summary>True when <paramref name="candidate"/> is the volume path itself or nested beneath it.</summary>
    private static bool IsUnder(string candidate, string volume)
    {
        if (string.Equals(candidate, volume, StringComparison.Ordinal))
            return true;

        return candidate.StartsWith(volume.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalises a container path. Container paths are always POSIX-style regardless of the host
    /// running the test, so this deliberately does not use <see cref="Path"/> APIs.
    /// </summary>
    private static string NormalisePath(string value)
    {
        var trimmed = value.Trim().Trim('"');
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
