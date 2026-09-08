using System.Runtime.InteropServices;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// xUnit collection fixture that packs the in-tree BotNexus.Cli source as a NuGet
/// global tool with a unique pre-release version and installs it into an isolated
/// --tool-path. Lets integration tests exercise pre-release CLI features (e.g. the
/// integration-mock provider, non-interactive `provider add`) before they ship to
/// nuget.org.
///
/// Contrast with <see cref="CliInstallFixture"/>, which validates the
/// already-published package on nuget.org. This fixture is the harness for
/// PR-time validation of CLI changes.
///
/// The pack-and-install runs once per test run. Failures are captured (not thrown)
/// so dependent tests can skip gracefully and the install diagnostics are visible
/// in test output.
///
/// <para><b>Why the pack is redirected to a per-run ArtifactsPath (issue #3255).</b>
/// This fixture used to run <c>dotnet pack</c> with default output paths, so it wrote the
/// repo's shared <c>src/**/bin/Release</c> and <c>obj</c> trees. Those exact trees are written
/// concurrently by other test assemblies in the same gate run - <c>NewUserExperienceFixture</c>
/// and <c>ExtensionBootFixture</c> both shell out to
/// <c>dotnet build src/dirs.proj -c Release</c>, and the runner itself performs a Release build
/// of <c>src/dirs.proj</c> before the test phase. xUnit runs test ASSEMBLIES in parallel, so the
/// pack raced those builds over the same files.</para>
///
/// <para>The race is not a compile failure - it is a torn read. The pack step collects the
/// project's output closure from <c>bin/Release</c> at the moment it copies; a concurrent build
/// that has deleted-and-not-yet-rewritten a dependency, or has rewritten one carrying the
/// ordinary <c>0.44.0</c> assembly version rather than this run's <c>99.99.99</c> pack stamp,
/// yields a package that is internally inconsistent. The installed CLI then starts and dies with
/// <c>Could not load file or assembly 'BotNexus.Agent.Providers.Core, Version=99.99.99.0'</c>,
/// which is precisely the #3237 evidence. Because the corruption lands in the ONE shared fixture,
/// every test in this collection fails together and none fails when the interleaving misses -
/// the observed all-or-nothing flake.</para>
///
/// <para><b>Why only the PACKAGE carries the synthetic version stamp (issue #3237).</b> The pack
/// passes <c>/p:PackageVersion=99.99.99-local-&lt;id&gt;</c> but leaves MSBuild <c>Version</c> at the
/// repo's real assembly version. Setting both - the behaviour before this fix - propagated the
/// synthetic value through every <c>ProjectReference</c>, so the CLI was compiled to bind
/// <c>BotNexus.Agent.Providers.Core, Version=99.99.99.0</c> while the copy of that dependency the
/// pack collected could still carry the repo version. The installed tool then started and died
/// during <c>init</c> with exactly that message. See <see cref="CliPackIsolation"/> for the full
/// mechanism; the equivalent fix for the E2E project is #3388.</para>
///
/// <para>The fix isolates the shared state rather than serialising against it: the pack builds
/// into <c>&lt;sandbox&gt;/build</c> via <c>ArtifactsPath</c>, so it neither reads nor writes any
/// path another fixture touches. Node reuse and the shared compilation/MSBuild servers are also
/// disabled, since a reused node retains the repo-rooted path state this redirection exists to
/// avoid.</para>
/// </summary>
public sealed class LocalCliInstallFixture : IAsyncLifetime
{
    private const string PackageId = "BotNexus.Cli";
    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

    public string PackVersion { get; private set; } = string.Empty;
    public string PackOutputDir { get; private set; } = string.Empty;

    /// <summary>
    /// Per-run MSBuild output root (<c>ArtifactsPath</c>) for the pack. Redirecting the pack away
    /// from the repo's shared <c>bin/</c> and <c>obj/</c> trees is the #3255 fix: see the class
    /// remarks. Always lives inside the per-run sandbox, never inside the repo.
    /// </summary>
    public string PackArtifactsDir { get; private set; } = string.Empty;
    public string ToolPath { get; private set; } = string.Empty;
    public string CliExecutablePath { get; private set; } = string.Empty;

    public bool Succeeded { get; private set; }
    public int PackExitCode { get; private set; } = -1;
    public int InstallExitCode { get; private set; } = -1;
    public string PackOutput { get; private set; } = string.Empty;
    public string InstallOutput { get; private set; } = string.Empty;
    public string? Error { get; private set; }

    /// <summary>
    /// Files present under <see cref="ToolPath"/> after install, captured once so every dependent
    /// test can render the layout in a failure message without re-walking a directory that the
    /// fixture may already have torn down (issue #3237 AC2).
    /// </summary>
    public IReadOnlyList<string> InstalledFiles { get; private set; } = [];

    /// <summary>
    /// Startup-critical assemblies that were absent from the install layout. Non-empty means the
    /// packed binary would have failed at assembly-load time; the fixture fails here instead,
    /// naming them (issue #3237 AC1).
    /// </summary>
    public IReadOnlyList<string> MissingAssemblies { get; private set; } = [];

    /// <summary>Human-readable failure detail for the layout guard, or null when the layout is complete.</summary>
    public string? LayoutFailure { get; private set; }

    /// <summary>
    /// The exact <c>dotnet pack</c> argument string used, captured so the isolation contract
    /// (issue #3255) is assertable from a test rather than only readable in source.
    /// </summary>
    public string PackArguments { get; private set; } = string.Empty;

    /// <summary>
    /// Failure detail when the pack was not isolated from the repo's shared build trees, or null
    /// when isolation held. Non-null means the #3255 race is live again.
    /// </summary>
    public string? PackIsolationFailure { get; private set; }

    /// <summary>
    /// Required assemblies present in the layout but carrying an assembly version the packed CLI
    /// does not bind against (issue #3237 — presence alone was not sufficient).
    /// </summary>
    public IReadOnlyList<string> VersionMismatches { get; private set; } = [];

    /// <summary>
    /// The repo's real assembly version, passed to the pack as <c>Version</c>. Captured so tests can
    /// assert the #3237 contract against the LIVE fixture rather than only against a constructed
    /// argument string.
    /// </summary>
    public Version AssemblyVersion { get; private set; } = new(0, 0, 0, 0);

    /// <summary>
    /// The assembly version the installed CLI actually binds its dependencies against. Since the
    /// #3237 fix this is the repo version, NOT the synthetic <c>99.99.99</c> pack stamp: the stamp
    /// identifies the package only.
    /// </summary>
    public Version ExpectedBoundVersion { get; private set; } = new(0, 0, 0, 0);

    public async Task InitializeAsync()
    {
        try
        {
            var runId = Guid.NewGuid().ToString("N");
            PackVersion = $"99.99.99-local-{runId[..8]}";
            var sandboxRoot = Path.Combine(Path.GetTempPath(), "botnexus-local-cli", runId);
            PackOutputDir = Path.Combine(sandboxRoot, "pack");
            PackArtifactsDir = Path.Combine(sandboxRoot, "build");
            ToolPath = Path.Combine(sandboxRoot, "tool");
            Directory.CreateDirectory(PackOutputDir);
            Directory.CreateDirectory(PackArtifactsDir);
            Directory.CreateDirectory(ToolPath);

            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "botnexus.exe" : "botnexus";
            CliExecutablePath = Path.Combine(ToolPath, exeName);

            var repoRoot = RepoLocator.FindRepoRoot();
            var cliProject = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");

            // --- pack -------------------------------------------------------
            // ArtifactsPath moves every intermediate and output of this pack into the per-run
            // sandbox, so it cannot race the concurrent `dotnet build src/dirs.proj -c Release`
            // performed by the runner and by two other integration fixtures (issue #3255).
            // nodeReuse / UseSharedCompilation / MSBUILD_SERVER are disabled for the same reason
            // a shared build node would carry repo-rooted state across into this isolated build.
            //
            // The synthetic 99.99.99 stamp identifies the PACKAGE only; MSBuild `Version` stays at
            // the repo's real assembly version (issue #3237, mirroring #3388). Stamping `Version`
            // too propagated the synthetic value through every ProjectReference, so the CLI was
            // compiled to bind `BotNexus.Agent.Providers.Core, Version=99.99.99.0` while the copy
            // of that dependency packaged alongside it could carry the repo version - which is
            // exactly the load failure this issue records.
            AssemblyVersion = CliPackIsolation.RepoAssemblyVersion;
            ExpectedBoundVersion = CliPackIsolation.ExpectedBoundVersion(AssemblyVersion);
            PackArguments = CliPackIsolation.BuildPackArguments(
                cliProject, AssemblyVersion, PackVersion, PackOutputDir, PackArtifactsDir);

            var packResult = await ProcessRunner.RunAsync(
                "dotnet",
                PackArguments,
                environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                timeout: PackTimeout);

            PackExitCode = packResult.ExitCode;
            PackOutput = packResult.Combined;
            if (PackExitCode != 0)
                return;

            // A pack that exited 0 but wrote nothing to the redirected artifacts directory built
            // into the shared repo trees instead, which is exactly the state #3255 removes. Fail
            // here, by name, rather than shipping a package that may be a torn read.
            if (CliPackIsolation.FindMissingIsolationSwitches(PackArguments).Count > 0
                || !CliPackIsolation.ArtifactsDirWasPopulated(PackArtifactsDir))
            {
                PackIsolationFailure = CliPackIsolation.DescribeIsolationFailure(PackArguments, PackArtifactsDir);
                return;
            }

            // --- install ----------------------------------------------------
            var installResult = await ProcessRunner.RunAsync(
                "dotnet",
                $"tool install --tool-path \"{ToolPath}\" --add-source \"{PackOutputDir}\" --version {PackVersion} {PackageId}",
                timeout: InstallTimeout);

            InstallExitCode = installResult.ExitCode;
            InstallOutput = installResult.Combined;
            if (InstallExitCode != 0 || !File.Exists(CliExecutablePath))
                return;

            // --- verify the install layout BEFORE any test invokes the binary ------
            // #3237: a packed CLI that is missing BotNexus.Agent.Providers.Core starts fine and
            // then dies at assembly-load time inside `init`, which surfaces as an opaque
            // "ExitCode should be 0 but was 1" in an unrelated test. Check by name here so the
            // failure names the assembly at the step that produced it.
            InstalledFiles = CliInstallLayout.EnumerateFiles(ToolPath);
            MissingAssemblies = CliInstallLayout.FindMissingAssemblies(InstalledFiles);
            if (MissingAssemblies.Count > 0)
            {
                LayoutFailure = CliInstallLayout.FormatMissingAssemblyFailure(
                    ToolPath,
                    MissingAssemblies,
                    InstalledFiles,
                    CliInstallLayout.ReadPackagedToolAssemblies(PackOutputDir));
                return;
            }

            // Presence is not enough: the observed #3237 failure had the file on disk and still
            // could not bind, because a dependency carried an assembly version other than the one
            // the CLI was compiled against. The expected version is the REPO version, not the
            // synthetic pack stamp - see the AssemblyVersion remarks.
            VersionMismatches = CliInstallLayout.FindVersionMismatches(ToolPath, ExpectedBoundVersion);
            if (VersionMismatches.Count > 0)
            {
                LayoutFailure = CliInstallLayout.FormatVersionMismatchFailure(
                    ToolPath,
                    ExpectedBoundVersion,
                    VersionMismatches,
                    InstalledFiles);
                return;
            }

            Succeeded = true;
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
            Succeeded = false;
        }
    }

    public Task DisposeAsync()
    {
        // Walk back to the per-run sandbox root and remove the whole tree.
        try
        {
            var sandboxRoot = Path.GetDirectoryName(ToolPath);
            if (!string.IsNullOrEmpty(sandboxRoot) && Directory.Exists(sandboxRoot))
                Directory.Delete(sandboxRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; locked .nupkg or tool files on Windows are not worth failing the suite over.
        }
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class LocalCliCollection : ICollectionFixture<LocalCliInstallFixture>
{
    public const string Name = "Local CLI install collection";
}
