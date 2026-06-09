using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Cron.Actions;

/// <summary>
/// Executes a cron job by running a shell command as a subprocess with timeout-safe process tree cleanup.
/// Action type: "command".
/// </summary>
/// <remarks>
/// Security: command jobs inherit the same security model as interactive exec — they run with the
/// gateway process identity. The command is stored in <see cref="CronJob.ShellCommand"/> and must be
/// non-null for command action types.
///
/// Process management:
/// <list type="bullet">
///   <item>Uses <c>pwsh -NoProfile -c</c> as the default shell (cross-platform via .NET Process).</item>
///   <item>Timeout defaults to 120 seconds; configurable via <see cref="CronJob.Metadata"/> key "timeoutSeconds".</item>
///   <item>On timeout: kills the process tree (Windows: taskkill /T, POSIX: process group -KILL).</item>
///   <item>Captures stdout + stderr and records the combined output in the cron run.</item>
/// </list>
/// </remarks>
public sealed class CommandCronAction : ICronAction
{
    /// <summary>Default timeout in seconds if not specified in job metadata.</summary>
    internal const int DefaultTimeoutSeconds = 120;

    /// <summary>Maximum allowed output capture length in characters.</summary>
    internal const int MaxOutputChars = 50_000;

    /// <inheritdoc/>
    public string ActionType => "command";

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var command = context.Job.ShellCommand;
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException(
                $"Cron job '{context.Job.Id}' has action type 'command' but ShellCommand is null or empty.");

        var logger = context.Services.GetService<ILogger<CommandCronAction>>();
        var timeoutSeconds = ResolveTimeout(context.Job);

        logger?.LogInformation(
            "CommandCronAction: executing command for job '{JobId}' (timeout={Timeout}s).",
            context.Job.Id, timeoutSeconds);

        var result = await RunProcessAsync(command, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            logger?.LogWarning(
                "CommandCronAction: job '{JobId}' timed out after {Timeout}s. Partial output length: {OutputLength}.",
                context.Job.Id, timeoutSeconds, result.Output.Length);

            throw new TimeoutException(
                $"Command timed out after {timeoutSeconds}s. Partial output ({result.Output.Length} chars): "
                + TruncateForError(result.Output));
        }

        if (result.ExitCode != 0)
        {
            logger?.LogWarning(
                "CommandCronAction: job '{JobId}' exited with code {ExitCode}. Output length: {OutputLength}.",
                context.Job.Id, result.ExitCode, result.Output.Length);

            throw new InvalidOperationException(
                $"Command exited with code {result.ExitCode}. Output: " + TruncateForError(result.Output));
        }

        logger?.LogInformation(
            "CommandCronAction: job '{JobId}' completed successfully. Output length: {OutputLength}.",
            context.Job.Id, result.Output.Length);
    }

    /// <summary>
    /// Runs the command in a subprocess with timeout and output capture.
    /// </summary>
    internal static async Task<CommandResult> RunProcessAsync(
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList = { "-NoProfile", "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var outputBuilder = new System.Text.StringBuilder();
        var outputLock = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock)
            {
                if (outputBuilder.Length < MaxOutputChars)
                    outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock)
            {
                if (outputBuilder.Length < MaxOutputChars)
                    outputBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout — kill the process tree
            KillProcessTree(process);
            return new CommandResult(
                ExitCode: -1,
                Output: outputBuilder.ToString(),
                TimedOut: true);
        }

        return new CommandResult(
            ExitCode: process.ExitCode,
            Output: outputBuilder.ToString(),
            TimedOut: false);
    }

    /// <summary>
    /// Kills the process and all descendants (process tree).
    /// On Windows uses taskkill /T; on POSIX kills the process directly
    /// (which .NET 8+ Kill(entireProcessTree: true) handles natively).
    /// </summary>
    internal static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited between timeout and kill.
        }
        catch (SystemException)
        {
            // Platform-specific failure (access denied, zombie process, etc.)
            // Best-effort — the timeout has fired so we move on.
        }
    }

    private static int ResolveTimeout(CronJob job)
    {
        if (job.Metadata is not null
            && job.Metadata.TryGetValue("timeoutSeconds", out var rawTimeout)
            && rawTimeout is not null)
        {
            if (rawTimeout is int intVal && intVal > 0)
                return intVal;

            if (rawTimeout is long longVal && longVal > 0)
                return (int)Math.Min(longVal, int.MaxValue);

            if (rawTimeout is string strVal && int.TryParse(strVal, out var parsed) && parsed > 0)
                return parsed;
        }

        return DefaultTimeoutSeconds;
    }

    private static string TruncateForError(string output)
    {
        const int maxErrorChars = 2000;
        return output.Length <= maxErrorChars
            ? output
            : output[..maxErrorChars] + "... (truncated)";
    }

    /// <summary>
    /// Result of a command execution attempt.
    /// </summary>
    internal sealed record CommandResult(int ExitCode, string Output, bool TimedOut);
}
