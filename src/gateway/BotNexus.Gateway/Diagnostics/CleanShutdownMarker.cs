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
/// The timestamp recorded in the marker, when the marker was clean and parseable.
/// Null when there was no marker or its contents could not be parsed.
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

    private readonly IFileSystem _fileSystem;
    private readonly string _markerPath;
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
    }

    /// <summary>The absolute path of the marker file, exposed for logging/diagnostics.</summary>
    public string MarkerPath => _markerPath;

    /// <summary>
    /// Inspects the marker to determine how the previous run ended. Never throws; any I/O or
    /// parse failure degrades to a best-effort answer so boot is never blocked by diagnostics.
    /// </summary>
    public PreviousRunResult DetectPreviousRun()
    {
        try
        {
            if (!_fileSystem.File.Exists(_markerPath))
                return new PreviousRunResult(WasClean: false, LastKnownUtc: null);

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
            // Unreadable marker: fail safe as "unclean" so an operator investigates.
            return new PreviousRunResult(WasClean: false, LastKnownUtc: null);
        }
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
