using System.Reflection;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// Pure construction helpers for the <c>dotnet pack</c> that <see cref="NewUserExperienceFixture"/>
/// performs on the in-tree CLI.
///
/// <para><b>Why this exists (issue #3388).</b> The fixture used to pack with
/// <c>/p:Version=99.99.99-e2e-&lt;id&gt; /p:PackageVersion=99.99.99-e2e-&lt;id&gt;</c>. Because
/// <c>Version</c> is a global MSBuild property it flows into every <c>ProjectReference</c>, so the
/// pack recompiled the CLI's whole dependency closure under a synthetic assembly version. Two
/// consequences, both measured:</para>
///
/// <list type="number">
///   <item><description><b>Correctness.</b> The runner's <c>build-release</c> phase and the
///   fixture's own prebuild write the same <c>src/**/bin/Release</c> tree at the repo's real
///   version. Whichever writer lands last decides what the pack copies, so a CLI compiled against
///   <c>99.99.99.0</c> was packaged next to a dependency stamped with the repo version. That binds
///   to nothing and the installed tool dies during <c>init</c> with
///   <c>Could not load file or assembly 'BotNexus.Agent.Providers.Core, Version=99.99.99.0'</c> -
///   the exact #3388 evidence, and the same family as #3255/#3237.</description></item>
///   <item><description><b>Cost.</b> Rebuilding the closure at a synthetic version guarantees a
///   cold compile of every referenced project from inside a testhost, which is the fixture-startup
///   cost #3314 attributed and could not remove.</description></item>
/// </list>
///
/// <para><b>The fix is to stamp only the PACKAGE.</b> <c>PackageVersion</c> alone gives the nupkg
/// the unique identity <c>dotnet tool install --version</c> needs for per-run isolation;
/// <c>Version</c> stays at the repo's real assembly version, so every assembly in the package -
/// CLI included - carries one identity, no dependency needs rebuilding, and the pack degrades to
/// an up-to-date check over output the runner already produced. Version skew becomes
/// unrepresentable rather than merely unlikely.</para>
///
/// <para>Kept free of process invocation and filesystem side effects so the contract is assertable
/// without running a real pack.</para>
/// </summary>
internal static class E2ECliPack
{
    /// <summary>
    /// Name of the machine-wide mutex serialising both the pack and the solution prebuild
    /// (issue #2739 for the prebuild, extended to the pack by #3388). Both write the shared
    /// <c>src/**/bin/Release</c> tree, so they must not overlap with each other or with a
    /// concurrent copy of this fixture in another test host.
    /// </summary>
    public const string PrebuildMutexName = @"Global\botnexus-e2e-prebuild";

    /// <summary>
    /// The repo's real assembly version, taken from this test assembly. Every project in the repo
    /// - the CLI and its dependencies included - is stamped by the same solution-wide
    /// <c>Directory.Build.props</c>, so reading it here needs no file parsing and cannot drift
    /// from what the runner's Release build produced.
    /// </summary>
    public static Version RepoAssemblyVersion =>
        typeof(E2ECliPack).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Builds the unique NuGet package version for one run. Pre-release only: it identifies the
    /// package, never an assembly.
    /// </summary>
    public static string BuildPackageVersion(string runId) =>
        $"99.99.99-e2e-{runId[..8]}";

    /// <summary>
    /// Builds the full <c>dotnet pack</c> argument string. This is the only place the command line
    /// is assembled.
    ///
    /// <paramref name="assemblyVersion"/> is passed as <c>Version</c> so it matches the Release
    /// output already on disk; <paramref name="packageVersion"/> is passed as
    /// <c>PackageVersion</c> only. Setting both to the synthetic stamp is the #3388 defect.
    ///
    /// <c>/nodeReuse:false</c> and <c>/p:UseSharedCompilation=false</c> force MSBuild and the
    /// Roslyn compile server to exit, so <c>dotnet pack</c> returns control instead of leaving
    /// build nodes attached to our captured stdout (which manifests as a spurious timeout).
    ///
    /// <paramref name="artifactsDir"/> redirects MSBuild's intermediate and output artifacts into
    /// the per-run sandbox. This is the complementary half of the same defect, and the two fixes
    /// are not interchangeable: <c>PackageVersion</c>-only stamping stops the pack from REQUESTING
    /// a version the shared Release tree never produced, while <c>ArtifactsPath</c> stops the pack
    /// from WRITING that shared tree and so racing a concurrent Release build (#3255).
    /// </summary>
    public static string BuildPackArguments(
        string cliProject,
        Version assemblyVersion,
        string packageVersion,
        string packOutputDir,
        string artifactsDir) =>
        $"pack \"{cliProject}\" --configuration Release --output \"{packOutputDir}\" " +
        $"/p:Version={assemblyVersion.ToString(3)} /p:PackageVersion={packageVersion} " +
        $"/p:ArtifactsPath=\"{artifactsDir}\" " +
        "/nodeReuse:false /p:UseSharedCompilation=false --nologo";

    /// <summary>
    /// The assembly version the installed CLI will bind its dependencies against, given the
    /// arguments above. Because <c>Version</c> is no longer the synthetic stamp, this is simply the
    /// repo version - stated as a function so the install-layout guard and the pack cannot drift
    /// apart.
    /// </summary>
    public static Version ExpectedBoundVersion(Version assemblyVersion) => new(
        assemblyVersion.Major,
        assemblyVersion.Minor,
        Math.Max(assemblyVersion.Build, 0),
        Math.Max(assemblyVersion.Revision, 0));

    /// <summary>
    /// Startup-critical assemblies the packed CLI binds during <c>init</c>. Absence or version skew
    /// in any of these produces a process that starts and then dies at assembly-load time, which
    /// surfaces as an opaque non-zero exit from an unrelated-looking CLI call much later.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredAssemblies =
    [
        "BotNexus.Cli.dll",
        "BotNexus.Gateway.dll",
        "BotNexus.Agent.Providers.Core.dll",
    ];

    /// <summary>
    /// Returns a description for each required assembly under <paramref name="installDir"/> that is
    /// absent or carries an assembly version other than <paramref name="expected"/>.
    ///
    /// This is the fail-fast for #3388: it converts the deferred
    /// <c>Could not load file or assembly ... Version=99.99.99.0</c> into a failure at the
    /// pack/install step that names the assembly and both versions, so one failing run is enough
    /// to diagnose. Mirrors the guard <c>CliInstallLayout</c> applies in the CLI integration
    /// project; duplicated rather than shared because this project deliberately carries no
    /// ProjectReference to it.
    /// </summary>
    public static IReadOnlyList<string> FindLayoutFaults(string installDir, Version expected)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return [$"install directory '{installDir}' does not exist"];

        var byName = Directory
            .EnumerateFiles(installDir, "*.dll", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var faults = new List<string>();
        foreach (var required in RequiredAssemblies)
        {
            if (!byName.TryGetValue(required, out var path))
            {
                faults.Add($"{required} is MISSING from the install layout");
                continue;
            }

            Version? actual;
            try
            {
                actual = AssemblyName.GetAssemblyName(path).Version;
            }
            catch (Exception ex)
            {
                faults.Add($"{required} identity unreadable at {path} ({ex.GetType().Name})");
                continue;
            }

            if (actual != expected)
                faults.Add($"{required} expected {expected}, found {actual?.ToString() ?? "<none>"} at {path}");
        }

        faults.Sort(StringComparer.Ordinal);
        return faults;
    }
}
