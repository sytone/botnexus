using System.Globalization;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Outcome of inspecting the clean-shutdown marker at boot.
/// </summary>
/// <param name="WasClean">
/// <c>true</c> when the previous run wrote a shutdown marker (it stopped gracefully);
/// <c>false</c> when no marker was found, meaning the prior run terminated uncleanly.
/// </param>
/// <param name="LastKnownUtc">
/// The last instant the previous run is known to have been alive.
/// <para>
/// For a clean stop this is the timestamp recorded in the shutdown marker. For an unclean
/// termination it is the last liveness stamp the dying run managed to write, which is what
/// makes the crash-window diagnostic useful: it is accurate to within one
/// <see cref="CleanShutdownMarker.DefaultLivenessRefreshInterval"/>.
/// </para>
/// Null only when no stamp of either kind is available (genuine first boot, or a data
/// directory that could not be read) - callers must then omit the timestamp entirely rather
/// than reporting a placeholder.
/// </param>
public readonly record struct PreviousRunResult(bool WasClean, DateTimeOffset? LastKnownUtc);

/// <summary>
/// Tracks whether the gateway shut down cleanly using a marker file
/// (<c>~/.botnexus/.gateway-clean-shutdown</c>).
/// <para>
/// Boot sequence: call <see cref="DetectPreviousRun"/> to learn how the last run ended,
/// then <see cref="MarkRunning"/> to clear the marker for the current run. A graceful stop
/// calls <see cref="MarkCleanShutdown"/>. If the process dies hard, the marker is absent on
/// next boot and the prior run is reported as unclean — the signal a silent death previously
/// left no trace of.
/// </para>
/// </summary>
public sealed class CleanShutdownMarker
{
    private const string MarkerFileName = ".gateway-clean-shutdown";
    private const string LivenessFileName = ".gateway-liveness";

    /// <summary>
    /// How often the liveness stamp is rewritten while the gateway runs. This is the accuracy
    /// bound on the "last known alive" instant reported after an unclean termination: the real
    /// death happened somewhere in the interval starting at the reported stamp. Kept coarse on
    /// purpose - the diagnostic wants a crash *window*, not a heartbeat, and a tighter interval
    /// would buy no investigative value while writing to disk more often.
    /// </summary>
    public static readonly TimeSpan DefaultLivenessRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem;
    private readonly string _markerPath;
    private readonly string _livenessPath;
    private readonly string _dataDirectory;

    /// <summary>
    /// Creates a marker manager rooted at <paramref name="dataDirectory"/> (the writable
    /// BotNexus data directory). The marker file lives directly inside that directory.
    /// </summary>
    public CleanShutdownMarker(IFileSystem fileSystem, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _fileSystem = fileSystem;
        _dataDirectory = dataDirectory;
        _markerPath = _fileSystem.Path.Combine(dataDirectory, MarkerFileName);
        _livenessPath = _fileSystem.Path.Combine(dataDirectory, LivenessFileName);
    }

    /// <summary>The absolute path of the marker file, exposed for logging/diagnostics.</summary>
    public string MarkerPath => _markerPath;

    /// <summary>The absolute path of the liveness stamp file, exposed for logging/diagnostics.</summary>
    public string LivenessPath => _livenessPath;

    /// <summary>
    /// Inspects the marker to determine how the previous run ended. Never throws; any I/O or
    /// parse failure degrades to a best-effort answer so boot is never blocked by diagnostics.
    /// </summary>
    public PreviousRunResult DetectPreviousRun()
    {
        try
        {
            // No shutdown marker: the previous run died without stopping gracefully. Fall back to
            // the liveness stamp it was refreshing while alive - that is the whole point of the
            // stamp, and the only way this branch can report a real instant.
            if (!_fileSystem.File.Exists(_markerPath))
                return new PreviousRunResult(WasClean: false, LastKnownUtc: ReadLiveness());

            var raw = _fileSystem.File.ReadAllText(_markerPath).Trim();
            // A present marker means the last run reached graceful shutdown, even if we can't
            // parse the timestamp it wrote — so it's clean, just with an unknown timestamp.
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var stamp))
            {
                return new PreviousRunResult(WasClean: true, LastKnownUtc: stamp);
            }

            return new PreviousRunResult(WasClean: true, LastKnownUtc: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable marker: fail safe as "unclean" so an operator investigates. The liveness
            // stamp is a separate file, so try it anyway - it may still be readable.
            return new PreviousRunResult(WasClean: false, LastKnownUtc: ReadLiveness());
        }
    }

    /// <summary>
    /// Reads the liveness stamp left behind by a previous run, or null when there is none or it
    /// cannot be read/parsed. Never throws.
    /// </summary>
    private DateTimeOffset? ReadLiveness()
    {
        try
        {
            if (!_fileSystem.File.Exists(_livenessPath))
                return null;

            var raw = _fileSystem.File.ReadAllText(_livenessPath).Trim();
            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var stamp)
                ? stamp
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the liveness stamp for the current run. Called once at boot and then on every tick
    /// of the refresh timer, so a hard kill leaves the most recent write behind as evidence of
    /// when the process was last alive. Best effort: never throws.
    /// </summary>
    public void RefreshLiveness() => RefreshLiveness(DateTimeOffset.UtcNow);

    /// <summary>
    /// Overload accepting an explicit timestamp; used by tests and callers that already hold the
    /// instant to record.
    /// </summary>
    public void RefreshLiveness(DateTimeOffset timestampUtc)
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_dataDirectory))
                _fileSystem.Directory.CreateDirectory(_dataDirectory);
            _fileSystem.File.WriteAllText(
                _livenessPath,
                timestampUtc.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a missed refresh only widens the reported crash window.
        }
    }

    /// <summary>
    /// Starts refreshing the liveness stamp every <paramref name="interval"/> (defaulting to
    /// <see cref="DefaultLivenessRefreshInterval"/>). Dispose the returned handle to stop the
    /// refresh - typically on graceful shutdown, after which the clean-shutdown marker takes over
    /// as the authoritative record.
    /// </summary>
    public IDisposable StartLivenessRefresh(TimeSpan? interval = null)
    {
        var period = interval ?? DefaultLivenessRefreshInterval;
        RefreshLiveness();
        return new Timer(_ => RefreshLiveness(), state: null, dueTime: period, period: period);
    }

    /// <summary>
    /// Builds the operator-facing unclean-termination warning for <paramref name="previousRun"/>.
    /// <para>
    /// Kept here rather than inline at the call site so the message shape is directly testable:
    /// the whole point of #3680 is that the timestamp clause must carry a real instant when one
    /// exists and must be <em>absent</em> when one does not, never a placeholder.
    /// </para>
    /// </summary>
    /// <returns>The warning text, or null when the previous run was clean (no warning is due).</returns>
    public static string? BuildUncleanWarning(PreviousRunResult previousRun)
    {
        if (previousRun.WasClean)
            return null;

        return previousRun.LastKnownUtc is { } lastKnown
            ? $"previous gateway run terminated uncleanly (last known alive: "
              + $"{lastKnown.ToString("o", CultureInfo.InvariantCulture)}, accurate to within "
              + $"{DefaultLivenessRefreshInterval})"
            : "previous gateway run terminated uncleanly (no liveness stamp available)";
    }

    /// <summary>
    /// Clears the marker for the current run. Called immediately after
    /// <see cref="DetectPreviousRun"/> so that any subsequent hard exit leaves no marker and
    /// is therefore detectable as unclean on the following boot.
    /// </summary>
    public void MarkRunning()
    {
        try
        {
            if (_fileSystem.File.Exists(_markerPath))
                _fileSystem.File.Delete(_markerPath);
            // Seed the liveness stamp immediately so even a crash seconds into boot reports a
            // real instant instead of leaving the previous run's stamp in place.
            RefreshLiveness();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a leftover marker only risks a missed unclean-death warning.
        }
    }

    /// <summary>
    /// Records a graceful shutdown by writing the current UTC timestamp to the marker file,
    /// creating the data directory if necessary.
    /// </summary>
    public void MarkCleanShutdown() => MarkCleanShutdown(DateTimeOffset.UtcNow);

    /// <summary>
    /// Overload accepting an explicit timestamp; used by tests and callers that already hold
    /// the shutdown time.
    /// </summary>
    public void MarkCleanShutdown(DateTimeOffset timestampUtc)
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_dataDirectory))
                _fileSystem.Directory.CreateDirectory(_dataDirectory);
            _fileSystem.File.WriteAllText(_markerPath, timestampUtc.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — inability to write the marker only risks a false unclean warning.
        }
    }
}
