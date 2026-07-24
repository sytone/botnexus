namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// The health classification a single doctor check reports for its area. The aggregate
/// <c>botnexus doctor</c> suite folds these into healthy/warning/error counts and a deterministic
/// exit code, so the ordering here (Healthy &lt; Warning &lt; Error) is significant: the worst
/// outcome across all sections determines the process exit code.
/// </summary>
public enum DoctorOutcome
{
    /// <summary>The checked area is fully healthy - nothing to report.</summary>
    Healthy,

    /// <summary>The area is usable but has a recoverable finding an operator should review.</summary>
    Warning,

    /// <summary>The area is broken or inaccessible - a script should treat this as a failure.</summary>
    Error
}

/// <summary>
/// The result of running one <see cref="IDoctorCheck"/>. Carries the overall <see cref="Outcome"/>
/// plus a short headline and optional detail lines rendered under the check's section. Checks never
/// throw to signal a finding - they return a result so the aggregate runner can continue with the
/// remaining independent checks (issue #2041).
/// </summary>
/// <param name="Outcome">The worst classification found by the check.</param>
/// <param name="Summary">A one-line headline shown next to the section title.</param>
/// <param name="Details">Optional additional lines shown under the headline (already plain text).</param>
public sealed record DoctorCheckResult(DoctorOutcome Outcome, string Summary, IReadOnlyList<string> Details)
{
    /// <summary>Convenience factory for a healthy result with no extra detail.</summary>
    public static DoctorCheckResult Healthy(string summary)
        => new(DoctorOutcome.Healthy, summary, []);

    /// <summary>Convenience factory for a warning result.</summary>
    public static DoctorCheckResult Warning(string summary, params string[] details)
        => new(DoctorOutcome.Warning, summary, details);

    /// <summary>Convenience factory for an error result.</summary>
    public static DoctorCheckResult Error(string summary, params string[] details)
        => new(DoctorOutcome.Error, summary, details);
}

/// <summary>
/// Ambient inputs a doctor check needs to run. Passed to every registered check so a new check can
/// be added without changing the aggregate runner's signature (issue #2041 extensibility goal).
/// </summary>
/// <param name="ConfigPath">Absolute path to the resolved <c>config.json</c> for the active --target.</param>
/// <param name="HomePath">The resolved BotNexus home directory backing the config.</param>
/// <param name="Verbose">True when the operator passed <c>--verbose</c>.</param>
public sealed record DoctorCheckContext(string ConfigPath, string HomePath, bool Verbose);

/// <summary>
/// One diagnostic performed by <c>botnexus doctor</c>. Implementations are discovered through
/// <see cref="DoctorCheckRegistry"/> and run in a deterministic order by the aggregate suite; a new
/// implementation added to the registry is automatically included in the bare <c>doctor</c> run,
/// which is the core anti-regression requirement of issue #2041 (no hardcoded parent handler that
/// silently omits new checks).
/// </summary>
public interface IDoctorCheck
{
    /// <summary>Stable identifier used for ordering and machine-readable output.</summary>
    string Id { get; }

    /// <summary>Human-readable section title rendered in the aggregate report.</summary>
    string Title { get; }

    /// <summary>
    /// Runs the check and returns its result. Implementations must not throw for an ordinary
    /// finding - a broken or inaccessible area is reported as <see cref="DoctorOutcome.Error"/> so
    /// the aggregate runner can keep executing the remaining independent checks.
    /// </summary>
    Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken);
}
