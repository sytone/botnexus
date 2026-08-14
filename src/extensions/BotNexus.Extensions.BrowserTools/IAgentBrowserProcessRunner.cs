namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Result of one <c>agent-browser</c> invocation (#3031).
/// </summary>
/// <param name="ExitCode">Process exit code; <c>-1</c> when the process was abandoned on timeout.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
/// <param name="TimedOut">Whether the command exceeded its budget and was killed.</param>
public sealed record AgentBrowserProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    /// <summary>Whether the command completed normally with a zero exit code.</summary>
    public bool IsSuccess => !TimedOut && ExitCode == 0;
}

/// <summary>
/// The single seam through which this extension may start a process (#3031 AC9).
/// </summary>
/// <remarks>
/// Every test in this project substitutes a fake here. That is not a convenience: AC9 requires
/// that no test launches a real browser, and a seam that the production path cannot bypass is the
/// only way to make that structural rather than a matter of test discipline. <see cref="AgentBrowserCli"/>
/// holds one of these and has no other route to <see cref="System.Diagnostics.Process"/>.
/// </remarks>
public interface IAgentBrowserProcessRunner
{
    /// <summary>Runs one command to completion, or abandons it at <paramref name="timeout"/>.</summary>
    /// <param name="binaryPath">Absolute path of the resolved agent-browser executable.</param>
    /// <param name="arguments">Argument vector, passed as a list so nothing is shell-quoted.</param>
    /// <param name="environment">
    /// The COMPLETE child environment. The runner must clear whatever it inherited and use only
    /// this (#3031 AC4).
    /// </param>
    /// <param name="timeout">Hard wall-clock budget for the command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AgentBrowserProcessResult> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
