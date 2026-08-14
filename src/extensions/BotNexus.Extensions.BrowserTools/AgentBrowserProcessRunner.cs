using System.Diagnostics;
using System.Text;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The real <see cref="IAgentBrowserProcessRunner"/>, over <see cref="Process"/> (#3031 AC4/AC5).
/// </summary>
/// <remarks>
/// <para>
/// Two properties of this class are security controls rather than implementation detail.
/// </para>
/// <para>
/// First, <see cref="ProcessStartInfo.Environment"/> is CLEARED before the supplied variables are
/// applied. .NET seeds that dictionary from the parent process, so merely adding the allow-list
/// on top of it would hand the child the full operator keyring plus the allow-list - the exact
/// opposite of the intent (GHSA-m4m8-xjp4-5rmm). The clear is what makes
/// <see cref="AgentBrowserEnvironment.Build"/> the complete description of what the child can see.
/// </para>
/// <para>
/// Second, the timeout always kills. A driver that hangs is worse than one that fails: the agent
/// loop has no way to distinguish "still working" from "wedged", so an unbounded wait turns one
/// bad page into a stalled session. Every exit from this method is either a completed process or
/// a killed one.
/// </para>
/// </remarks>
public sealed class AgentBrowserProcessRunner : IAgentBrowserProcessRunner
{
    /// <inheritdoc />
    public async Task<AgentBrowserProcessResult> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            // ArgumentList, never a joined Arguments string: the joined form is re-parsed by the
            // OS and a URL containing a quote or a space becomes two arguments or an injection.
            startInfo.ArgumentList.Add(argument);
        }

        // THE control. See the class remarks - adding without clearing is the vulnerability.
        startInfo.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new AgentBrowserUnavailableException(
                $"The agent-browser executable at '{binaryPath}' could not be started: {ex.Message}. "
                + AgentBrowserBinaryResolver.InstallGuidance,
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new AgentBrowserProcessResult(
                -1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new AgentBrowserProcessResult(
            process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            // entireProcessTree: agent-browser spawns Chrome. Killing only the parent orphans a
            // browser that keeps the profile locked and the port bound, so the NEXT command fails
            // for a reason that has nothing to do with the page being visited.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Already gone, or the platform refused. Either way there is nothing further to do
            // and throwing here would replace a timeout report with an unrelated failure.
        }
    }
}

/// <summary>
/// Raised when the browser cannot be driven at all: no binary, no Chrome, or a dead subprocess.
/// </summary>
/// <remarks>
/// A distinct type so the tools can turn it into an ACTIONABLE tool result (#3031 AC6) instead of
/// a generic stack trace. The message always names what to do next; an error that only says
/// "failed" costs the operator a support round-trip to learn what the process already knew.
/// </remarks>
public sealed class AgentBrowserUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentBrowserUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
