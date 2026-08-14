using BotNexus.Domain.Text;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The guarded entry point every browser call must pass through (#3030).
/// </summary>
/// <remarks>
/// <para>
/// The guards live here rather than inside the tools on purpose. A guard that is merely "called
/// by the tool" is bypassed the moment a second tool is added; a guard that owns the only method
/// which touches the driver cannot be. The tools (a separate stage of #2899) will call
/// <see cref="NavigateAsync"/> and <see cref="SnapshotAsync"/> and will have no other route to
/// the browser.
/// </para>
/// <para>
/// Every denial happens BEFORE the driver is touched, which is what makes AC1's "without
/// launching a subprocess" testable: the fake driver records its calls, and a denied navigation
/// leaves that record empty.
/// </para>
/// </remarks>
public sealed class GuardedBrowserSession
{
    private readonly IBrowserDriver _driver;
    private readonly BrowserToolsConfig _config;
    private readonly BrowserGuardState _guardState;
    private readonly IBrowserFileSystem _fileSystem;
    private readonly string _workspacePath;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a guarded session over the supplied driver.</summary>
    /// <param name="driver">The underlying browser driver.</param>
    /// <param name="workspacePath">Absolute path of the agent workspace (spill root).</param>
    /// <param name="config">Guard configuration; <c>null</c> uses defaults.</param>
    /// <param name="guardState">
    /// Guard initialisation outcome. <c>null</c> is treated as <see cref="BrowserGuardState.Ready"/>;
    /// a failed state denies every call.
    /// </param>
    /// <param name="fileSystem">Filesystem abstraction for spill writes (see <see cref="IBrowserFileSystem"/>).</param>
    /// <param name="clock">Time source for spill file names; injectable so tests are deterministic.</param>
    public GuardedBrowserSession(
        IBrowserDriver driver,
        string workspacePath,
        BrowserToolsConfig? config = null,
        BrowserGuardState? guardState = null,
        IBrowserFileSystem? fileSystem = null,
        Func<DateTimeOffset>? clock = null)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _workspacePath = workspacePath ?? throw new ArgumentNullException(nameof(workspacePath));
        _config = config ?? new BrowserToolsConfig();
        _guardState = guardState ?? BrowserGuardState.Ready;
        _fileSystem = fileSystem ?? new BrowserFileSystem();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Validates the target and, only if it is admitted, navigates.
    /// </summary>
    /// <returns>An allowed result, or the denial reason. The driver is untouched on denial.</returns>
    public async Task<BrowserGuardResult> NavigateAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (!_guardState.IsReady)
        {
            return DeniedBecauseGuardsUnavailable();
        }

        var verdict = BrowserToolsUrlGuard.Validate(url, _config);
        if (!verdict.IsAllowed)
        {
            return verdict;
        }

        await _driver.NavigateAsync(url!, cancellationToken).ConfigureAwait(false);
        return BrowserGuardResult.Allowed;
    }

    /// <summary>
    /// Re-validates the browser's CURRENT location and, only if it is still admitted, returns the
    /// page text in an untrusted-content envelope.
    /// </summary>
    /// <remarks>
    /// The re-read is the point of this method (AC3). The URL that passed validation at navigation
    /// time is not the URL the content came from: page script can rewrite <c>location.href</c>
    /// afterwards, so a page that passed the guard can redirect itself onto the metadata endpoint
    /// and have the agent read the result back. Validating the ORIGINAL url here would be a guard
    /// that inspects the wrong value and always passes.
    /// </remarks>
    public async Task<BrowserSnapshotResult> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!_guardState.IsReady)
        {
            return BrowserSnapshotResult.FromDenial(DeniedBecauseGuardsUnavailable());
        }

        var currentUrl = await _driver.GetCurrentUrlAsync(cancellationToken).ConfigureAwait(false);

        var verdict = BrowserToolsUrlGuard.Validate(currentUrl, _config);
        if (!verdict.IsAllowed)
        {
            return BrowserSnapshotResult.FromDenial(BrowserGuardResult.Denied(
                "Browser snapshot denied: the page navigated itself to a URL that fails the "
                + $"browser guard after load. {verdict.Reason}"));
        }

        var text = await _driver.GetPageTextAsync(cancellationToken).ConfigureAwait(false)
            ?? string.Empty;

        string? spillPath = null;
        var inline = text;

        if (text.Length > _config.SnapshotMaxChars)
        {
            spillPath = await SpillAsync(text, cancellationToken).ConfigureAwait(false);
            // SafeTruncate rather than a raw substring: cutting mid-grapheme would hand the model
            // a broken surrogate pair at exactly the boundary an attacker can position.
            inline = TextTruncation.SafeTruncate(text, _config.SnapshotMaxChars) ?? string.Empty;
        }

        return BrowserSnapshotResult.FromContent(
            BrowserSnapshotEnvelope.Wrap(currentUrl, inline, spillPath),
            spillPath);
    }

    private async Task<string> SpillAsync(string text, CancellationToken cancellationToken)
    {
        var relativeDir = _fileSystem.CombinePath("tmp", "browser");
        var absoluteDir = _fileSystem.CombinePath(_workspacePath, "tmp", "browser");
        _fileSystem.CreateDirectory(absoluteDir);

        var name = $"snapshot-{_clock().UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt";

        await _fileSystem
            .WriteAllTextAsync(_fileSystem.CombinePath(absoluteDir, name), text, cancellationToken)
            .ConfigureAwait(false);

        // A workspace-RELATIVE path is returned deliberately: it is what the agent's read tool
        // accepts, and an absolute path would leak the host's directory layout into the transcript.
        return $"{relativeDir.Replace('\\', '/')}/{name}";
    }

    private BrowserGuardResult DeniedBecauseGuardsUnavailable() =>
        BrowserGuardResult.Denied(
            "Browser access denied: the safety guards failed to initialise, so every browser "
            + $"call is refused. {_guardState.FailureReason}");
}

/// <summary>
/// Outcome of a guarded snapshot: either envelope-wrapped content, or a denial (#3030).
/// </summary>
public readonly struct BrowserSnapshotResult
{
    private BrowserSnapshotResult(bool isAllowed, string? content, string? reason, string? spillPath)
    {
        IsAllowed = isAllowed;
        Content = content;
        Reason = reason;
        SpillPath = spillPath;
    }

    /// <summary>Whether content was returned.</summary>
    public bool IsAllowed { get; }

    /// <summary>The envelope-wrapped page text; <c>null</c> when denied.</summary>
    public string? Content { get; }

    /// <summary>Denial reason; <c>null</c> when allowed.</summary>
    public string? Reason { get; }

    /// <summary>
    /// Workspace-relative path of the full untruncated text, when the snapshot was truncated.
    /// </summary>
    public string? SpillPath { get; }

    internal static BrowserSnapshotResult FromContent(string content, string? spillPath) =>
        new(true, content, null, spillPath);

    internal static BrowserSnapshotResult FromDenial(BrowserGuardResult denial) =>
        new(false, null, denial.Reason, null);
}
