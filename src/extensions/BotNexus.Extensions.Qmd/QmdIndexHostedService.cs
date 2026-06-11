using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Qmd;

/// <summary>
/// Background service that periodically re-indexes QMD stores with <c>AutoUpdate = true</c>.
/// Runs <c>qmd update</c> followed by <c>qmd embed</c> for each store at its configured interval.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Does NOT block gateway startup — first update runs asynchronously after service starts.</item>
///   <item>Graceful handling when <c>qmd</c> is not installed: logs warning, marks stores unhealthy.</item>
///   <item>Staggered execution: stores are updated sequentially to avoid file lock contention.</item>
///   <item>5-minute timeout per update operation.</item>
///   <item>Cancellation on gateway shutdown via <see cref="BackgroundService.StopAsync"/>.</item>
/// </list>
/// </remarks>
public sealed class QmdIndexHostedService : BackgroundService
{
    private readonly IQmdBackend _backend;
    private readonly QmdConfig _config;
    private readonly ILogger<QmdIndexHostedService> _logger;
    private readonly Dictionary<string, QmdStoreHealthInfo> _healthInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum duration for a single qmd update operation.</summary>
    internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public QmdIndexHostedService(IQmdBackend backend, QmdConfig config, ILogger<QmdIndexHostedService> logger)
    {
        _backend = backend;
        _config = config;
        _logger = logger;

        foreach (var store in config.Stores.Where(s => s.AutoUpdate))
        {
            _healthInfo[store.Name] = new QmdStoreHealthInfo(store.Name);
        }
    }

    /// <summary>
    /// Gets health information for all auto-update stores.
    /// </summary>
    public IReadOnlyDictionary<string, QmdStoreHealthInfo> HealthInfo => _healthInfo;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var autoUpdateStores = _config.Stores.Where(s => s.AutoUpdate).ToList();
        if (autoUpdateStores.Count == 0)
        {
            _logger.LogInformation("QmdIndexHostedService: no stores configured with AutoUpdate=true. Service idle.");
            return;
        }

        // Delay slightly to avoid blocking startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("QmdIndexHostedService: starting with {Count} auto-update store(s).", autoUpdateStores.Count);

        // Run initial update for all stores
        await UpdateAllStoresAsync(autoUpdateStores, stoppingToken).ConfigureAwait(false);

        // Periodic loop
        while (!stoppingToken.IsCancellationRequested)
        {
            // Find next store due for update
            var nextDue = FindNextDueStore(autoUpdateStores);
            if (nextDue is null)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            var waitTime = nextDue.Value.dueAt - DateTimeOffset.UtcNow;
            if (waitTime > TimeSpan.Zero)
            {
                await Task.Delay(waitTime, stoppingToken).ConfigureAwait(false);
            }

            await UpdateStoreAsync(nextDue.Value.store, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateAllStoresAsync(IReadOnlyList<QmdStoreConfig> stores, CancellationToken ct)
    {
        foreach (var store in stores)
        {
            if (ct.IsCancellationRequested) break;
            await UpdateStoreAsync(store, ct).ConfigureAwait(false);
        }
    }

    internal async Task UpdateStoreAsync(QmdStoreConfig store, CancellationToken ct)
    {
        var health = _healthInfo.GetValueOrDefault(store.Name) ?? new QmdStoreHealthInfo(store.Name);
        _healthInfo[store.Name] = health;

        _logger.LogInformation("QmdIndexHostedService: updating store '{StoreName}'...", store.Name);
        var startTime = DateTimeOffset.UtcNow;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UpdateTimeout);

        try
        {
            await _backend.UpdateIndexAsync(store.Name, timeoutCts.Token).ConfigureAwait(false);
            await _backend.EmbedAsync(store.Name, timeoutCts.Token).ConfigureAwait(false);
            var duration = DateTimeOffset.UtcNow - startTime;

            health.LastSuccessfulUpdate = DateTimeOffset.UtcNow;
            health.LastUpdateDuration = duration;
            health.ConsecutiveFailures = 0;
            health.IsHealthy = true;

            _logger.LogInformation(
                "QmdIndexHostedService: store '{StoreName}' updated successfully in {Duration:F1}s.",
                store.Name, duration.TotalSeconds);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            health.ConsecutiveFailures++;
            health.IsHealthy = health.ConsecutiveFailures < 3;
            _logger.LogError(
                "QmdIndexHostedService: store '{StoreName}' update timed out after {Timeout}s (failure #{Count}).",
                store.Name, UpdateTimeout.TotalSeconds, health.ConsecutiveFailures);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Gateway shutdown — propagate
        }
        catch (Exception ex)
        {
            health.ConsecutiveFailures++;
            health.IsHealthy = health.ConsecutiveFailures < 3;
            _logger.LogWarning(ex,
                "QmdIndexHostedService: store '{StoreName}' update failed (failure #{Count}).",
                store.Name, health.ConsecutiveFailures);
        }
    }

    private (QmdStoreConfig store, DateTimeOffset dueAt)? FindNextDueStore(IReadOnlyList<QmdStoreConfig> stores)
    {
        (QmdStoreConfig store, DateTimeOffset dueAt)? earliest = null;

        foreach (var store in stores)
        {
            var health = _healthInfo.GetValueOrDefault(store.Name);
            var lastUpdate = health?.LastSuccessfulUpdate ?? DateTimeOffset.MinValue;
            var dueAt = lastUpdate + TimeSpan.FromMinutes(store.UpdateIntervalMinutes);

            if (earliest is null || dueAt < earliest.Value.dueAt)
            {
                earliest = (store, dueAt);
            }
        }

        return earliest;
    }
}

/// <summary>
/// Tracks health status for a single QMD store.
/// </summary>
public sealed class QmdStoreHealthInfo
{
    /// <summary>Creates a new health info instance for the given store.</summary>
    public QmdStoreHealthInfo(string storeName) => StoreName = storeName;

    /// <summary>Store name.</summary>
    public string StoreName { get; }

    /// <summary>Timestamp of last successful update.</summary>
    public DateTimeOffset? LastSuccessfulUpdate { get; set; }

    /// <summary>Duration of last successful update.</summary>
    public TimeSpan? LastUpdateDuration { get; set; }

    /// <summary>Number of consecutive update failures.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Whether the store is considered healthy (fewer than 3 consecutive failures).</summary>
    public bool IsHealthy { get; set; } = true;
}
