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
    /// Builds the full <c>dotnet pack</c> argument string. This is the ONLY place the pack command
    /// line is assembled, so the isolation switches cannot be dropped by editing one of two copies.
    /// </summary>
    public static string BuildPackArguments(string cliProject, string packVersion, string packOutputDir, string artifactsDir) =>
        $"pack \"{cliProject}\" --configuration Release --output \"{packOutputDir}\" " +
        $"/p:Version={packVersion} /p:PackageVersion={packVersion} " +
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
