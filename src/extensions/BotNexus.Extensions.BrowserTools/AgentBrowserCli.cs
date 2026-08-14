using System.Text;
using System.Text.Json;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Drives the <c>agent-browser</c> executable as a subprocess (#3031 AC4-AC7).
/// </summary>
/// <remarks>
/// <para>
/// This is the <see cref="IBrowserDriver"/> the guard layer wraps. It is deliberately NOT the
/// thing the tools talk to: <see cref="GuardedBrowserSession"/> owns the only reference, so a
/// tool cannot navigate or snapshot without passing the guards (#3031 AC8). The click, type and
/// screenshot commands are exposed here directly because they carry no URL to validate - they act
/// on the page the guard already admitted.
/// </para>
/// <para>
/// Session isolation is the <c>--session</c> argument, threaded onto every command. Two agents
/// with different session keys therefore never share a browser profile, cookie jar or logged-in
/// identity, which is the difference between "the browser tool" and "the browser tool that lets
/// agent A read agent B's authenticated pages".
/// </para>
/// </remarks>
public sealed class AgentBrowserCli : IBrowserDriver, IBrowserInteractionDriver, IAsyncDisposable
{
    /// <summary>
    /// Minimum budget for the FIRST navigate of a session (#3031 AC5).
    /// </summary>
    /// <remarks>
    /// A floor, not a default. The first navigate pays for cold daemon start plus the first
    /// Chrome launch, which routinely exceeds the steady-state budget on a cold machine; timing
    /// it out would make the tool look broken precisely on first use, and the retry would pay the
    /// same cold cost again. Every subsequent command uses the ordinary configured budget.
    /// </remarks>
    public const int FirstNavigateTimeoutFloorSeconds = 120;

    /// <summary>Markers in agent-browser output that mean Chrome itself is missing (#3031 AC6).</summary>
    private static readonly string[] ChromeMissingMarkers =
    [
        "chrome not found",
        "could not find chrome",
        "no chrome installation",
        "chrome is not installed",
        "browser not installed",
        "executable doesn't exist",
        "failed to launch the browser process",
    ];

    /// <summary>The remedy appended to every missing-Chrome failure. Names the command to run.</summary>
    public const string ChromeInstallGuidance =
        "Chrome for Testing is not installed, so the browser tools cannot open a page. An operator "
        + "must run 'agent-browser install' on this host once; it is deliberately not automated "
        + "because downloading and executing a browser is an operator decision. Until then every "
        + "browser tool call will fail fast rather than hang.";

    private readonly IAgentBrowserProcessRunner _runner;
    private readonly AgentBrowserResolution _resolution;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _hasNavigated;
    private bool _disposed;

    /// <summary>Creates a CLI driver bound to one session id.</summary>
    /// <param name="sessionId">
    /// The <c>--session</c> value. Derived from <c>AgentToolContributionContext</c>'s agent and
    /// session identity by the contributor; see <see cref="BrowserToolsContributor"/>.
    /// </param>
    /// <param name="resolution">Outcome of <see cref="AgentBrowserBinaryResolver"/>.</param>
    /// <param name="runner">Process seam; injected so tests never launch anything (AC9).</param>
    /// <param name="config">Timeout configuration; <c>null</c> uses defaults.</param>
    /// <param name="readParentVariable">
    /// Parent-environment reader forwarded to <see cref="AgentBrowserEnvironment.Build"/>.
    /// </param>
    public AgentBrowserCli(
        string sessionId,
        AgentBrowserResolution resolution,
        IAgentBrowserProcessRunner? runner = null,
        BrowserToolsConfig? config = null,
        Func<string, string?>? readParentVariable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(resolution);

        SessionId = sessionId;
        _resolution = resolution;
        _runner = runner ?? new AgentBrowserProcessRunner();

        var seconds = (config ?? new BrowserToolsConfig()).CommandTimeoutSeconds;
        _commandTimeout = TimeSpan.FromSeconds(seconds > 0
            ? seconds
            : BrowserToolsConfig.DefaultCommandTimeoutSeconds);

        // Built ONCE, from empty, at construction (AC4). Building it per command would give a
        // later caller a place to slip an extra variable in between invocations.
        _environment = AgentBrowserEnvironment.Build(readParentVariable);
    }

    /// <summary>The <c>--session</c> value every command carries.</summary>
    public string SessionId { get; }

    /// <summary>Whether a close command has been issued for this session.</summary>
    public bool IsClosed => _disposed;

    /// <summary>
    /// The budget the NEXT command would use. Exposed so AC5 is asserted against the value the
    /// driver will actually apply rather than against a re-derivation inside the test.
    /// </summary>
    public TimeSpan NextTimeoutFor(string command) =>
        !_hasNavigated && string.Equals(command, "navigate", StringComparison.Ordinal)
            ? Max(_commandTimeout, TimeSpan.FromSeconds(FirstNavigateTimeoutFloorSeconds))
            : _commandTimeout;

    /// <inheritdoc />
    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // The floor is read BEFORE the flag flips, so the first navigate of a session gets it and
        // the second does not, exactly as AC5 words it.
        var timeout = NextTimeoutFor("navigate");
        await RunAsync(["navigate", url], timeout, cancellationToken).ConfigureAwait(false);
        _hasNavigated = true;
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentUrlAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["url"], _commandTimeout, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    /// <inheritdoc />
    public async Task<string> GetPageTextAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["snapshot"], _commandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <inheritdoc />
    public async Task<string> ClickAsync(string selector, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var result = await RunAsync(["click", selector], _commandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    /// <inheritdoc />
    public async Task<string> TypeAsync(
        string selector,
        string text,
        bool submit = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var args = new List<string> { "type", selector, text ?? string.Empty };
        if (submit)
        {
            args.Add("--submit");
        }

        var result = await RunAsync(args, _commandTimeout, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    /// <inheritdoc />
    public async Task<string> ScreenshotAsync(
        string destinationPath,
        bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var args = new List<string> { "screenshot", "--output", destinationPath };
        if (fullPage)
        {
            args.Add("--full-page");
        }

        await RunAsync(args, _commandTimeout, cancellationToken).ConfigureAwait(false);
        return destinationPath;
    }

    /// <summary>
    /// Issues the session close. Idempotent, and never throws (#3031 AC7).
    /// </summary>
    /// <remarks>
    /// Teardown that can throw is teardown that leaks the next resource in the dispose chain, and
    /// a browser this agent can no longer reach is not a failure the agent can act on. A close
    /// that fails leaves an orphan process, which is bad; a close that throws leaves an orphan
    /// process AND aborts the rest of handle disposal, which is worse.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_resolution.IsResolved)
            {
                using var cts = new CancellationTokenSource(_commandTimeout);
                await _runner.RunAsync(
                    _resolution.BinaryPath!,
                    BuildArguments(["close"]),
                    _environment,
                    _commandTimeout,
                    cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Intentionally swallowed; see the remarks above.
        }
        finally
        {
            _gate.Dispose();
        }
    }

    /// <summary>Prefixes the session flag onto a command's arguments (#3031 AC7).</summary>
    internal IReadOnlyList<string> BuildArguments(IReadOnlyList<string> command)
    {
        var args = new List<string>(command.Count + 2) { "--session", SessionId };
        args.AddRange(command);
        return args;
    }

    private async Task<AgentBrowserProcessResult> RunAsync(
        IReadOnlyList<string> command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_resolution.IsResolved)
        {
            // AC6's first half: an unresolvable binary is reported the moment a call is made,
            // carrying the resolver's own install guidance. Nothing is launched and nothing waits.
            throw new AgentBrowserUnavailableException(
                _resolution.Message ?? AgentBrowserBinaryResolver.InstallGuidance);
        }

        // Serialised per session: agent-browser drives one browser per session id, and two
        // concurrent commands against the same profile race on the same page.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _runner.RunAsync(
                _resolution.BinaryPath!,
                BuildArguments(command),
                _environment,
                timeout,
                cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                throw new AgentBrowserUnavailableException(
                    $"The agent-browser command '{command[0]}' exceeded its "
                    + $"{timeout.TotalSeconds:0}s budget and was terminated. The browser tools "
                    + "fail fast rather than hang so the agent loop is never wedged by one page.");
            }

            if (!result.IsSuccess)
            {
                var combined = result.StandardError + "\n" + result.StandardOutput;

                if (LooksLikeMissingChrome(combined))
                {
                    // AC6's second half. Recognised explicitly so the agent is told to install
                    // Chrome rather than handed a raw launcher stack trace it cannot act on.
                    throw new AgentBrowserUnavailableException(ChromeInstallGuidance);
                }

                throw new AgentBrowserUnavailableException(
                    $"The agent-browser command '{command[0]}' failed with exit code "
                    + $"{result.ExitCode}: {Summarise(result.StandardError)}");
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether output indicates a missing Chrome installation rather than a page-level failure.
    /// </summary>
    internal static bool LooksLikeMissingChrome(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        foreach (var marker in ChromeMissingMarkers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Summarise(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "(no diagnostic output)";
        }

        var trimmed = stderr.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "...";
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>
    /// Turns an agent/session identity into a filesystem- and CLI-safe <c>--session</c> value.
    /// </summary>
    /// <remarks>
    /// agent-browser uses the session id as a directory name for the browser profile. Passing a
    /// raw key through would let a '/' or '..' in an identifier point the profile somewhere else,
    /// so everything outside a conservative set is replaced rather than escaped. A truncated key
    /// is disambiguated by a short hash of the FULL key, so two long keys sharing a prefix still
    /// get two different browsers - which is the property AC7 is actually about.
    /// </remarks>
    public static string ToSessionId(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        var builder = new StringBuilder(sessionKey.Length);
        foreach (var c in sessionKey)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        var sanitised = builder.ToString();
        var digest = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)))[..8];

        const int MaxPrefix = 48;
        var prefix = sanitised.Length <= MaxPrefix ? sanitised : sanitised[..MaxPrefix];
        return $"{prefix}-{digest}";
    }

    /// <summary>
    /// Serialises a driver result for a tool response. Kept here so the tools share one shape.
    /// </summary>
    internal static string Json(object payload) => JsonSerializer.Serialize(payload);
}
