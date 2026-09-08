namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Pure construction and verification helpers for the isolated <c>dotnet pack</c> that
/// <see cref="LocalCliInstallFixture"/> performs.
///
/// Why this exists (issue #3255): the pack used to run with MSBuild's default output paths, so it
/// wrote the repo-shared <c>src/**/bin/Release</c> and <c>obj</c> trees while other test assemblies
/// in the same gate run were building those very trees. The resulting package was intermittently
/// internally inconsistent, and the installed CLI died at assembly-load time much later, in a
/// different test. Nothing in the harness could tell whether the isolation was actually in force.
///
/// Splitting argument construction out of the fixture makes the isolation contract assertable
/// without invoking a real pack, and <see cref="DescribeIsolationFailure"/> turns "the redirect
/// silently stopped working" into a named failure at the step that produced it rather than an
/// opaque symptom in an unrelated test.
///
/// <para><b>Version stamping (issue #3237, mirroring the #3388 fix already applied to the E2E
/// project).</b> This pack used to pass the synthetic <c>99.99.99-local-&lt;id&gt;</c> stamp as BOTH
/// <c>PackageVersion</c> and <c>Version</c>. <c>Version</c> is a global MSBuild property, so it
/// flowed into every <c>ProjectReference</c> and asked MSBuild to recompile the CLI's entire
/// dependency closure under an assembly version that exists nowhere else on the machine. The CLI
/// was then compiled to bind <c>BotNexus.Agent.Providers.Core, Version=99.99.99.0</c>, while
/// whichever copy of that dependency the pack actually collected could still carry the repo's real
/// <c>0.45.0.0</c> — at which point the installed tool starts and dies during <c>init</c> with
/// <c>Could not load file or assembly 'BotNexus.Agent.Providers.Core, Version=99.99.99.0'</c>.
/// That is the #3237 evidence exactly, and it explains why the guard added by PR #3243 was silent:
/// the guard compared the layout against <c>ToAssemblyVersion(PackVersion)</c>, i.e. against the
/// same synthetic number the defect invents, so a layout that was internally consistent at the
/// repo version could still be judged "correct" or "wrong" for reasons unrelated to what the CLI
/// would actually bind.</para>
///
/// <para><b>The fix is to stamp only the PACKAGE.</b> <c>PackageVersion</c> alone gives the nupkg
/// the unique identity <c>dotnet tool install --version</c> needs for per-run isolation, while
/// <c>Version</c> stays at the repo's real assembly version. Every assembly in the package then
/// carries one identity, nothing in the closure needs rebuilding at a synthetic version, and the
/// skew becomes unrepresentable rather than merely unlikely. <c>ArtifactsPath</c> (#3255) is the
/// complementary half and is not interchangeable with it: <c>PackageVersion</c>-only stamping stops
/// the pack from REQUESTING a version the shared Release tree never produced, while
/// <c>ArtifactsPath</c> stops the pack from WRITING that shared tree and racing a concurrent build.</para>
/// </summary>
internal static class CliPackIsolation
{
    /// <summary>
    /// MSBuild switches that must appear on the pack command line for it to be isolated from the
    /// repo's shared build trees. <c>ArtifactsPath</c> does the actual redirection; the other two
    /// stop a reused build node or shared compiler server from carrying repo-rooted state into
    /// this build.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredIsolationSwitches =
    [
        "/p:ArtifactsPath=",
        "/nodeReuse:false",
        "/p:UseSharedCompilation=false",
    ];

    /// <summary>
    /// The repo's real assembly version, taken from this test assembly. Every project in the repo —
    /// the CLI and its dependencies alike — is stamped by the same solution-wide
    /// <c>Directory.Build.props</c>, so reading it here needs no file parsing and cannot drift from
    /// what a Release build produces.
    /// </summary>
    public static Version RepoAssemblyVersion =>
        typeof(CliPackIsolation).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// The assembly version the installed CLI will bind its dependencies against, given the
    /// arguments produced by <see cref="BuildPackArguments"/>. Because <c>Version</c> is no longer
    /// the synthetic pack stamp, this is simply the repo version — stated as a function so the
    /// install-layout guard and the pack command line cannot drift apart (issue #3237).
    /// </summary>
    public static Version ExpectedBoundVersion(Version assemblyVersion) => new(
        assemblyVersion.Major,
        assemblyVersion.Minor,
        Math.Max(assemblyVersion.Build, 0),
        Math.Max(assemblyVersion.Revision, 0));

    /// <summary>
    /// Builds the full <c>dotnet pack</c> argument string. This is the ONLY place the pack command
    /// line is assembled, so the isolation switches cannot be dropped by editing one of two copies.
    ///
    /// <paramref name="assemblyVersion"/> is passed as <c>Version</c> so it matches the Release
    /// output already on disk; <paramref name="packVersion"/> is passed as <c>PackageVersion</c>
    /// ONLY. Setting both to the synthetic stamp is the #3237/#3388 defect — see the class remarks.
    /// </summary>
    public static string BuildPackArguments(
        string cliProject,
        Version assemblyVersion,
        string packVersion,
        string packOutputDir,
        string artifactsDir) =>
        $"pack \"{cliProject}\" --configuration Release --output \"{packOutputDir}\" " +
        $"/p:Version={assemblyVersion.ToString(3)} /p:PackageVersion={packVersion} " +
        $"/p:ArtifactsPath=\"{artifactsDir}\" " +
        "/nodeReuse:false /p:UseSharedCompilation=false --nologo";

    /// <summary>
    /// Returns the required isolation switches absent from <paramref name="packArguments"/>.
    /// Empty means the command line asks for isolation; it does not by itself prove MSBuild
    /// honoured it, which is what <see cref="ArtifactsDirWasPopulated"/> checks.
    /// </summary>
    public static IReadOnlyList<string> FindMissingIsolationSwitches(string packArguments) =>
        RequiredIsolationSwitches
            .Where(s => !packArguments.Contains(s, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// True when the redirected artifacts directory actually received build output. A pack that
    /// leaves this directory empty built somewhere else — i.e. into the shared repo trees — and the
    /// #3255 race is live again even though the switch was on the command line.
    /// </summary>
    public static bool ArtifactsDirWasPopulated(string artifactsDir) =>
        !string.IsNullOrWhiteSpace(artifactsDir)
        && Directory.Exists(artifactsDir)
        && Directory.EnumerateFileSystemEntries(artifactsDir, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Builds the pack-step failure message for a pack that was not isolated, naming which half of
    /// the contract broke so the reader does not have to re-derive it.
    /// </summary>
    public static string DescribeIsolationFailure(string packArguments, string artifactsDir)
    {
        var missing = FindMissingIsolationSwitches(packArguments);
        var reason = missing.Count > 0
            ? "the pack command line is missing required isolation switch(es): " + string.Join(", ", missing)
            : $"the pack command line requested isolation but '{artifactsDir}' received no build output, " +
              "so MSBuild wrote somewhere else — most likely the repo's shared bin/obj trees";

        return string.Join(Environment.NewLine + Environment.NewLine,
            "Local CLI pack was not isolated from the repo's shared build trees: " + reason + ".",
            "An un-isolated pack races the concurrent 'dotnet build src/dirs.proj -c Release' run by the " +
            "gate runner and by the E2E/ExtensionBoot fixtures, producing an internally inconsistent " +
            "package whose CLI fails at assembly-load time in a later, unrelated test (see issue #3255).",
            "Pack arguments were:" + Environment.NewLine + "  " + packArguments);
    }
}
