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
}
