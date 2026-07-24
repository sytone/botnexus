namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// The canonical, ordered set of doctor checks that make up the aggregate <c>botnexus doctor</c>
/// suite. This registry is the single seam issue #2041 requires: adding a check here automatically
/// includes it in the bare <c>doctor</c> run (and in the check-inventory the CLI advertises), so a
/// new diagnostic can never be silently omitted by a hardcoded parent handler.
/// <para>
/// Order is deterministic and meaningful - cheap configuration checks run first, then filesystem
/// and reconciliation checks - so scripted output and the final summary are stable across runs.
/// </para>
/// </summary>
internal static class DoctorCheckRegistry
{
    /// <summary>
    /// Builds the ordered check list. A factory (not a static array) so each invocation gets fresh
    /// instances and tests can build an isolated suite without shared state.
    /// </summary>
    public static IReadOnlyList<IDoctorCheck> CreateDefault() =>
    [
        new ConfigHealthCheck(),
        new LocationAccessibilityCheck(),
        new PersistentAgentFolderCheck(),
        new SubAgentWorkspaceCheck(),
    ];
}
