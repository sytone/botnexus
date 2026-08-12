using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory;

/// <summary>
/// Runs one <see cref="MemorySessionReconciler"/> pass at startup so memory rows left behind by
/// sessions deleted while the gateway was down (or before #2956 added the delete path) stop being
/// searchable (issue #2956).
/// </summary>
/// <remarks>
/// The pass runs detached from <see cref="StartAsync"/> so a slow session-corpus enumeration never
/// delays gateway startup, and any fault is logged rather than taking the host down: reconciliation
/// is a convergence nicety, not a precondition for serving traffic.
/// </remarks>
public sealed class MemorySessionReconciliationService(
    MemorySessionReconciler reconciler,
    ILogger<MemorySessionReconciliationService> logger) : IHostedService
{
    private readonly MemorySessionReconciler _reconciler = reconciler;
    private readonly ILogger<MemorySessionReconciliationService> _logger = logger;
    private readonly CancellationTokenSource _stoppingCts = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _reconciler.ReconcileAsync(_stoppingCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup memory/session reconciliation failed.");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();
        return Task.CompletedTask;
    }
}
