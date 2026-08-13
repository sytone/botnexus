using BotNexus.Domain.Text;
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
/// Authorization (issue #2462): every execution passes through <see cref="ICommandCronAuthorizer"/>
/// <b>before</b> <c>Process.Start()</c>. The gated event is <b>FIRING</b> - each scheduled or manual
/// run - not <b>AUTHORING</b> (creating/updating a job that carries a shellCommand), which this
/// change leaves unchanged. A denial throws, so the scheduler records a failed run carrying the
/// denial reason and the subprocess is never started. The policy vocabulary is the existing
/// exec/shell tool-boundary surface (<c>IToolPolicyProvider</c> / <c>ToolApprovalFallback</c>),
/// not a second parallel model.
///
/// Process management:
/// <list type="bullet">
///   <item>Uses <c>pwsh -NoProfile -c</c> as the default shell (cross-platform via .NET Process).</item>
///   <item>Timeout defaults to 120 seconds; configurable via <see cref="CronJob.Metadata"/> key "timeoutSeconds".
///   A value of <c>0</c> means <b>unlimited</b> (#2904): no timeout is armed and the run is bounded only
///   by the ambient cancellation token. A negative or unparseable value warns and falls back to the default.</item>
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

        // FIRING gate (#2462): evaluated on every execution, before any process is started.
        // Fails closed - if no authorizer is registered we construct the default one, which itself
        // denies when the shared tool policy provider is absent.
        var authorizer = context.Services.GetService<ICommandCronAuthorizer>()
            ?? new ToolPolicyCommandCronAuthorizer(
                context.Services.GetService<BotNexus.Gateway.Abstractions.Security.IToolPolicyProvider>(),
                context.Services.GetService<ILogger<ToolPolicyCommandCronAuthorizer>>());

        var decision = authorizer.AuthorizeFiring(context.Job, command);
        if (!decision.Allowed)
        {
            logger?.LogError(
                "CommandCronAction: DENIED command execution for job '{JobId}': {Reason}. No process was started.",
                context.Job.Id, decision.Reason);

            throw new UnauthorizedAccessException(
                $"Cron command job '{context.Job.Id}' was denied by the command authorization policy: {decision.Reason}");
        }

        var timeoutSeconds = ResolveTimeout(context.Job, logger);

        logger?.LogInformation(
            "CommandCronAction: executing command for job '{JobId}' (timeout={Timeout}).",
            context.Job.Id, DescribeTimeout(timeoutSeconds));

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
    /// <param name="timeoutSeconds">
    /// Seconds to allow the process, or <c>null</c> for an unlimited run (#2904). When null no
    /// <c>CancelAfter</c> is armed, so only <paramref name="cancellationToken"/> can end the wait.
    /// </param>
    internal static async Task<CommandResult> RunProcessAsync(
        string command,
        int? timeoutSeconds,
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

        // #2904: null is the explicit "unlimited" sentinel - arm nothing, leaving the ambient token
        // as the only thing that can cancel the wait. The kill path below is unchanged and still
        // fires for every armed timeout.
        if (timeoutSeconds is int armedTimeout)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(armedTimeout));

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

    /// <summary>
    /// Resolves this job's process timeout. Returns <c>null</c> for an explicit unlimited run
    /// (<c>timeoutSeconds: 0</c>, #2904). Delegates to <see cref="CronTimeoutResolver"/> so the
    /// scheduler and this action agree on both the sentinel and the set of accepted value shapes -
    /// this site previously accepted neither <c>double</c> nor <c>JsonElement</c>.
    /// </summary>
    private static int? ResolveTimeout(CronJob job, ILogger? logger)
        => CronTimeoutResolver.Resolve(job, DefaultTimeoutSeconds, logger);

    /// <summary>Renders a resolved timeout for logging, so "unlimited" is not logged as a bare blank.</summary>
    private static string DescribeTimeout(int? timeoutSeconds)
        => timeoutSeconds is int s ? $"{s}s" : "unlimited";

    private static string TruncateForError(string output)
    {
        const int maxErrorChars = 2000;

        // Cron error text routinely carries model output, which is emoji-dense (#2883).
        return TextTruncation.SafeTruncate(output, maxErrorChars, "... (truncated)")!;
    }

    /// <summary>
    /// Result of a command execution attempt.
    /// </summary>
    internal sealed record CommandResult(int ExitCode, string Output, bool TimedOut);
}
