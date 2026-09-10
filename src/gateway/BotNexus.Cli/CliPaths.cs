namespace BotNexus.Cli;

/// <summary>
/// Canonical path resolution for CLI source and target directories.
/// </summary>
internal static class CliPaths
{
    /// <summary>
    /// Default source (repo) location: ~/botnexus
    /// </summary>
    public static string DefaultSource => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "botnexus");

    /// <summary>
    /// Default target (runtime home) location: ~/.botnexus
    /// </summary>
    public static string DefaultTarget => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".botnexus");

    /// <summary>
    /// Resolve the source directory. Explicit path wins; otherwise DefaultSource.
    /// </summary>
    public static string ResolveSource(string? explicitSource) =>
        string.IsNullOrWhiteSpace(explicitSource) ? DefaultSource : explicitSource;

    /// <summary>
    /// The gateway assembly a given repository root would launch.
    /// </summary>
    /// <remarks>
    /// One definition, because three call sites derived this same path independently and only one
    /// of them was ever passed to <c>StopAsync</c> - which is how a gateway started outside the CLI
    /// came to be reported "not running" while alive. Two places resolving the same thing is the
    /// shape that let <c>shell</c> and the <c>exec</c> extension disagree about the working
    /// directory for months (#2416).
    /// </remarks>
    public static string GatewayBinary(string repoRoot) => Path.Combine(
        repoRoot, "src", "gateway", "BotNexus.Gateway.Api", "bin", "Release", "net10.0",
        "BotNexus.Gateway.Api.dll");

    /// <summary>
    /// Resolve the target (BotNexus home) directory. Explicit path wins; then BOTNEXUS_HOME env var; otherwise DefaultTarget.
    /// </summary>
    public static string ResolveTarget(string? explicitTarget)
    {
        if (!string.IsNullOrWhiteSpace(explicitTarget))
            return explicitTarget;

        var homeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        if (!string.IsNullOrWhiteSpace(homeOverride))
            return homeOverride;

        return DefaultTarget;
    }
}
