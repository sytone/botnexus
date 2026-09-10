using System.Diagnostics;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>Compatibility facade over the host-shared lifecycle owner; no extension-local output or handles.</summary>
public class ManagedProcess : BackgroundProcess
{
    internal const int MaxOutputBytes = OutputRetentionPolicy.MaxOutputBytes;
    /// <summary>A start failure proves the command was not dispatched.</summary>
    public const string NotDispatchedMessage =
        "Failed to start process - the command did not run and no side effect occurred. " +
        "It is safe to retry once the cause is resolved.";

    internal ManagedProcess(Process process, string command, DateTimeOffset startedAt)
        : base(process, command, startedAt) { }

    /// <summary>Preserves the extension's public kill outcome while the shared owner handles retention.</summary>
    public ProcessKillState KillState => !KillRequested ? ProcessKillState.NotRequested
        : KillUnconfirmed ? ProcessKillState.Unconfirmed : ProcessKillState.Confirmed;

    internal static ManagedProcess Start(ProcessStartInfo startInfo, string command, CancellationToken cancellationToken = default)
        => Start(startInfo, command, cancellationToken, null);

    internal static ManagedProcess Start(ProcessStartInfo startInfo, string command, CancellationToken cancellationToken, Action<Process>? onStarted)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException(NotDispatchedMessage);
            onStarted?.Invoke(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new ManagedProcess(process, command, DateTimeOffset.UtcNow);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(2_000); }
            catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }
}
