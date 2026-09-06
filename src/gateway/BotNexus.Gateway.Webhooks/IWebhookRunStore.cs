using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Persistence contract for webhook run records. Callers poll run records after
/// receiving a <c>202 Accepted</c> response from the inbound endpoint.
/// </summary>
public interface IWebhookRunStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<WebhookRun> CreateAsync(WebhookRun run, CancellationToken ct = default);
    Task<WebhookRun?> GetAsync(WebhookRunId runId, CancellationToken ct = default);
    Task<WebhookRun> UpdateAsync(WebhookRun run, CancellationToken ct = default);

    /// <summary>
    /// Lists recent runs for a given registration, newest first.
    /// </summary>
    Task<IReadOnlyList<WebhookRun>> ListByWebhookAsync(
        WebhookId webhookId,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>
    /// Purges terminal runs older than the specified cutoff — <c>Completed</c>, <c>Failed</c>,
    /// <c>Timeout</c> and <c>Rejected</c>. Runs still in flight (<c>Pending</c>, <c>Queued</c> or
    /// <c>Running</c>) are never purged regardless of age.
    /// </summary>
    /// <param name="cutoff">Runs with CompletedAt before this time will be deleted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of runs purged.</returns>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
