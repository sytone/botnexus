using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences the environment handed to child processes: a tool that runs a command an AGENT composed
/// must build that command's environment from an allow-list, never inherit the gateway's own.
/// </summary>
/// <remarks>
/// <para>
/// The gateway process holds provider API keys and whatever an operator exported for the
/// <c>env:</c> credential references in their configuration. <c>ISecretResolver</c> is careful to
/// keep a resolved credential out of an agent's context - but that guarantee is void if the agent
/// can read the same variables out of its own shell, which .NET grants by default: a
/// <c>ProcessStartInfo</c> seeds its environment from the parent.
/// </para>
/// <para>
/// The browser worker was hardened this way for GHSA-m4m8-xjp4-5rmm and the shell tools were not,
/// because nothing required a new spawn site to make the choice. This test is that requirement:
/// every file in <c>src/</c> that constructs a <c>ProcessStartInfo</c> must appear in exactly one
/// of the two lists below, so adding a spawn site is a decision recorded in a reviewed file rather
/// than a default nobody noticed.
/// </para>
/// </remarks>
public sealed class ToolSubprocessEnvironmentFenceArchitectureTests : ArchitectureTest
{
    private const string SpawnMarker = "new ProcessStartInfo";

    /// <summary>
    /// Spawn sites that run agent-composed commands. These MUST replace the inherited environment.
    /// </summary>
    private static readonly string[] MustSanitiseEnvironment =
    [
        "ShellTool.cs",
        "ExecTool.cs",
        "AgentBrowserProcessRunner.cs",
    ];

    /// <summary>
    /// Spawn sites that do not run agent-composed commands, each with the reason it is exempt.
    /// </summary>
    /// <remarks>
    /// Exempt because the command is fixed by this repository or written by the operator, not
    /// assembled from model output: CLI subcommands the operator invoked, the updater, the git
    /// runner behind plugin install, the OS keyring binary, the QMD CLI, an operator-authored
    /// <c>command</c> cron action, the MCP servers an operator configured, and the
    /// <c>which</c>/<c>where</c> probes used to locate a shell.
    /// <para>
    /// Exempt is not the same as harmless. An MCP server and a cron command still inherit the
    /// gateway environment; that is defensible because an operator chose the binary, and it is
    /// recorded here so the next person can revisit it rather than rediscover it.
    /// </para>
    /// </remarks>
    private static readonly string[] ExemptSpawnSites =
    [
        "StdioMcpTransport.cs",
        "ProcessGitCommandRunner.cs",
        "QmdCliBackend.cs",
        "BuildOutputStreamer.cs",
        "InstallCommand.cs",
        "ServeCommand.cs",
        "UpdateCommand.cs",
        "GatewayProcessManager.cs",
        "LaunchdServiceManager.cs",
        "CommandCronAction.cs",
        "KeyringSecretProvider.cs",
        "UpdateCheckService.cs",
        "PathUtils.cs",
    ];

    private IReadOnlyList<string> SpawnSiteFiles() =>
        [.. Directory
            .EnumerateFiles(Repository.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(SpawnMarker, StringComparison.Ordinal))];

    [Fact]
    public void EveryAgentCommandSpawnSite_BuildsItsEnvironmentFromAnAllowList()
    {
        foreach (var fileName in MustSanitiseEnvironment)
        {
            var path = SpawnSiteFiles().SingleOrDefault(p => Path.GetFileName(p) == fileName);

            path.ShouldNotBeNull(
                $"{fileName} should still construct a ProcessStartInfo; if it moved, this fence stopped guarding it.");

            var source = File.ReadAllText(path);
            var sanitises = source.Contains("ToolProcessEnvironment.ApplyTo(", StringComparison.Ordinal)
                            || source.Contains("Environment.Clear()", StringComparison.Ordinal);

            sanitises.ShouldBeTrue(
                $"{fileName} runs commands an agent composed, so it must replace the inherited "
                + "environment (ToolProcessEnvironment.ApplyTo) rather than let the child read the "
                + "gateway's provider keys and env: credentials.");
        }
    }

    [Fact]
    public void EverySpawnSiteInTheRepository_IsEitherSanitisedOrExplicitlyExempt()
    {
        var classified = MustSanitiseEnvironment.Concat(ExemptSpawnSites).ToHashSet(StringComparer.Ordinal);

        var unclassified = SpawnSiteFiles()
            .Select(Path.GetFileName)
            .Where(name => !classified.Contains(name!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        unclassified.ShouldBeEmpty(
            "a new process spawn site must declare whether it runs agent-composed commands. Add it "
            + "to MustSanitiseEnvironment (and call ToolProcessEnvironment.ApplyTo) if an agent can "
            + "influence the command, or to ExemptSpawnSites with the reason if it cannot.");
    }

    [Fact]
    public void TheFenceActuallySeesTheSpawnSitesItClaimsToGuard()
    {
        // A fence that matches nothing passes. This one has three named files it must find and a
        // repository-wide sweep that must be non-trivial; both have been wrong before in this repo
        // while still reporting green.
        var found = SpawnSiteFiles();

        found.Count.ShouldBeGreaterThanOrEqualTo(
            MustSanitiseEnvironment.Length + ExemptSpawnSites.Length,
            "the sweep found fewer spawn sites than are classified, so it is not reading the tree.");

        foreach (var fileName in MustSanitiseEnvironment)
        {
            found.ShouldContain(
                p => Path.GetFileName(p) == fileName,
                $"the sweep must locate {fileName}, otherwise the guard above vacuously passes.");
        }
    }
}
