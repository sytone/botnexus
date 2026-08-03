namespace BotNexus.Gateway.Configuration.Shadow;

/// <summary>
/// Holds the most recent shadow comparison so it can be inspected after the fact (#2766 AC8).
///
/// <para>
/// <b>Why a sink rather than only a log line.</b> A diff that exists only in startup logs is
/// effectively unavailable: by the time anyone wants to know whether the migration is faithful, the
/// line has scrolled past, been rotated, or is buried in tens of thousands of INF records. Cutover is
/// meant to be an evidence-based decision ("shadow has produced N clean starts"), and evidence you
/// cannot retrieve on demand does not support a decision.
/// </para>
///
/// <para>
/// Deliberately in-memory and single-slot. Persisting the history is a larger design (retention,
/// location, schema) and belongs with the store itself; the immediate need is that an operator can ask
/// the running gateway "what did the last shadow run find?" and get an answer without scraping logs.
/// </para>
/// </summary>
public interface IConfigShadowReportSink
{
    /// <summary>The most recent report, or <see langword="null"/> if no shadow run has completed.</summary>
    ConfigShadowDiffReport? Latest { get; }

    /// <summary>
    /// The most recent failure, or <see langword="null"/> if the last run completed. Held separately
    /// from <see cref="Latest"/> because "the migration threw" and "the migration produced differences"
    /// are different findings, and collapsing them would let a crashing migration read as a clean one
    /// that simply had not run yet.
    /// </summary>
    string? LastFailure { get; }

    /// <summary>Records a completed comparison.</summary>
    void Record(ConfigShadowDiffReport report);

    /// <summary>Records that a shadow run failed without producing a comparison.</summary>
    void RecordFailure(string description);
}

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IConfigShadowReportSink"/>.
/// </summary>
public sealed class ConfigShadowReportSink : IConfigShadowReportSink
{
    private readonly Lock _gate = new();
    private ConfigShadowDiffReport? _latest;
    private string? _lastFailure;

    /// <inheritdoc />
    public ConfigShadowDiffReport? Latest
    {
        get { lock (_gate) { return _latest; } }
    }

    /// <inheritdoc />
    public string? LastFailure
    {
        get { lock (_gate) { return _lastFailure; } }
    }

    /// <inheritdoc />
    public void Record(ConfigShadowDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _latest = report;
            // A successful run clears the previous failure: leaving it set would make a recovered
            // system look permanently broken.
            _lastFailure = null;
        }
    }

    /// <inheritdoc />
    public void RecordFailure(string description)
    {
        lock (_gate)
        {
            _lastFailure = description;
        }
    }
}
