namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Refuses external browser execution because this transport cannot enforce the shared
/// public-only destination policy at connection time (#4030).
/// </summary>
/// <remarks>
/// Lexical URL checks and a post-load URL read cannot protect against DNS changes, redirects,
/// subresources or network requests triggered by page interactions. A proxy flag alone also
/// cannot prove that the browser has no direct or bypass route. Until a supported transport
/// provides connection-bound enforcement, no command may start or attach to a browser daemon.
/// This includes reads and close: invoking an external executable is itself outside this boundary.
/// Test runners exercise command formatting only; they are not evidence of network enforcement.
/// </remarks>
public sealed class AgentBrowserProcessRunner : IAgentBrowserProcessRunner
{
    /// <summary>
    /// Explains why installing a browser or changing proxy settings cannot enable this transport.
    /// </summary>
    public const string DestinationEnforcementGuidance =
        "Browser execution denied: connection-bound destination enforcement is unavailable for "
        + "the external agent-browser transport. All browser commands are refused before process "
        + "start, including public URLs, because DNS changes, redirects, subresources and proxy "
        + "bypass routes cannot be constrained by URL checks. Installing a binary or changing "
        + "proxy settings does not remove this safety boundary.";

    /// <inheritdoc />
    public Task<AgentBrowserProcessResult> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();

        // There is intentionally no launch seam or configuration escape hatch here. Denying only
        // navigate would let snapshot/click/type attach to an already-running, unguarded daemon.
        return Task.FromException<AgentBrowserProcessResult>(
            new AgentBrowserUnavailableException(DestinationEnforcementGuidance));
    }
}

/// <summary>
/// Raised when the browser cannot be driven, including an unavailable safety boundary.
/// </summary>
/// <remarks>
/// A distinct type lets tools return a bounded explanation rather than a launcher stack trace.
/// </remarks>
public sealed class AgentBrowserUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentBrowserUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
