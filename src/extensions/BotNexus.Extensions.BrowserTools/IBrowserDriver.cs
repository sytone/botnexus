namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The minimal surface the guard layer needs from an underlying browser driver (#3030).
/// </summary>
/// <remarks>
/// Deliberately tiny and deliberately free of any launch/dispose semantics. The guards must be
/// testable - and provably non-launching - without a subprocess anywhere in the picture, so the
/// contract exposes only the three reads a guarded snapshot needs. Subprocess launching and
/// session isolation are a separate stage of #2899 and are out of scope here.
/// </remarks>
public interface IBrowserDriver
{
    /// <summary>Navigates the browser to the supplied URL.</summary>
    Task NavigateAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the browser's CURRENT location, which may differ from the URL last navigated to
    /// because page script can rewrite <c>location.href</c> after load.
    /// </summary>
    Task<string> GetCurrentUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the current page's text content.</summary>
    Task<string> GetPageTextAsync(CancellationToken cancellationToken = default);
}
