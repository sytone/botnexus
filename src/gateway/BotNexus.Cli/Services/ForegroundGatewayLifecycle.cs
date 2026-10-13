using System.Diagnostics;

namespace BotNexus.Cli.Services;

/// <summary>
/// Keeps foreground gateway children attached to the CLI lifecycle while delegating every planned
/// shutdown and bounded native escalation to <see cref="IGatewayProcessManager"/>.
/// </summary>
internal static class ForegroundGatewayLifecycle
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(20);

    internal static async Task<int> WaitForExitAsync(
        Process process,
        IGatewayProcessManager processManager,
        string homePath,
        string gatewayBinaryPath,
        string gatewayUrl,
        CancellationToken cancellationToken)
    {
        var pidFilePath = Path.Combine(homePath, "gateway.pid");
        var pidRecord = GatewayPidFile.Serialize(GatewayPidFile.Capture(process));
        await File.WriteAllTextAsync(pidFilePath, pidRecord, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            var stop = await processManager.StopAsync(
                homePath,
                gatewayBinaryPath,
                cleanup.Token,
                gatewayUrl);

            if (stop.Outcome == GatewayStopOutcome.Failed)
                throw new InvalidOperationException(stop.Message ?? "Foreground gateway failed to stop.");

            await process.WaitForExitAsync(cleanup.Token);
        }
        finally
        {
            // Do not remove a record that another gateway replaced while this frame was unwinding.
            if (process.HasExited &&
                File.Exists(pidFilePath) &&
                string.Equals(await File.ReadAllTextAsync(pidFilePath, CancellationToken.None), pidRecord, StringComparison.Ordinal))
            {
                File.Delete(pidFilePath);
            }
        }

        return process.ExitCode;
    }
}
