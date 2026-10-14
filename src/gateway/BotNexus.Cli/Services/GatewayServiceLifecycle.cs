namespace BotNexus.Cli.Services;

/// <summary>
/// Coordinates API-first gateway shutdown with an OS service fallback before definition removal.
/// </summary>
internal sealed class GatewayServiceLifecycle
{
    private static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IGatewayProcessManager _processManager;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _exitTimeout;
    private readonly TimeSpan _pollInterval;

    internal GatewayServiceLifecycle(
        IGatewayProcessManager processManager,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? exitTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _processManager = processManager;
        _delay = delay ?? Task.Delay;
        _exitTimeout = exitTimeout ?? DefaultExitTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    internal async Task<ServiceOperationResult> StopAndRemoveAsync(
        IOsServiceManager serviceManager,
        string homePath,
        string gatewayBinaryPath,
        string gatewayUrl,
        CancellationToken cancellationToken)
    {
        if (!await serviceManager.IsInstalledAsync(cancellationToken).ConfigureAwait(false))
            return await serviceManager.UninstallAsync(cancellationToken).ConfigureAwait(false);

        if (await serviceManager.IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            // Service-hosted gateways may have neither an identity-bearing PID file nor a distinct
            // process image (legacy dotnet-hosted installs). The authenticated API request must not
            // depend on obtaining a process handle first.
            await _processManager.RequestPlannedShutdownAsync(homePath, gatewayUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!await WaitUntilStoppedAsync(serviceManager, cancellationToken).ConfigureAwait(false))
            {
                await _processManager.StopAsync(
                    homePath,
                    gatewayBinaryPath,
                    cancellationToken,
                    gatewayUrl).ConfigureAwait(false);

                if (!await WaitUntilStoppedAsync(serviceManager, cancellationToken).ConfigureAwait(false))
                {
                    // Process escalation is not final for a service-hosted legacy install: the
                    // service controller is the authoritative last resort, and remains bounded.
                    var nativeStop = await serviceManager.StopAsync(cancellationToken).ConfigureAwait(false);
                    if (!nativeStop.Success)
                        return nativeStop;

                    if (!await WaitUntilStoppedAsync(serviceManager, cancellationToken).ConfigureAwait(false))
                        return new ServiceOperationResult(false, "Service did not stop within the bounded fallback timeout; definition was not removed.");
                }
            }
        }

        if (await serviceManager.IsRunningAsync(cancellationToken).ConfigureAwait(false))
            return new ServiceOperationResult(false, "Service is still running; definition was not removed.");

        return await serviceManager.UninstallAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitUntilStoppedAsync(IOsServiceManager serviceManager, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_exitTimeout);

        try
        {
            while (await serviceManager.IsRunningAsync(timeout.Token).ConfigureAwait(false))
                await _delay(_pollInterval, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
