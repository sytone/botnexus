using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Persistence contract for webhook registrations. Implementations include
/// <c>SqliteWebhookRegistrationStore</c> (production) and an in-memory stub
/// for unit tests.
/// </summary>
public interface IWebhookRegistrationStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<WebhookRegistration> CreateAsync(WebhookRegistration registration, CancellationToken ct = default);
    Task<WebhookRegistration?> GetAsync(WebhookId webhookId, CancellationToken ct = default);
    Task<IReadOnlyList<WebhookRegistration>> ListAsync(AgentId? agentId = null, CancellationToken ct = default);
    Task<WebhookRegistration> UpdateAsync(WebhookRegistration registration, CancellationToken ct = default);

    /// <summary>
    /// Records successful inbound use without rewriting registration fields that may have
    /// changed concurrently, especially the conversation pin established by first delivery.
    /// </summary>
    Task TouchLastUsedAsync(
        WebhookId webhookId,
        DateTimeOffset lastUsedAt,
        CancellationToken ct = default);

    Task DeleteAsync(WebhookId webhookId, CancellationToken ct = default);

    /// <summary>
    /// Atomically pins <paramref name="conversationId"/> onto the registration ONLY if
    /// <c>PinnedConversationId</c> is currently <c>null</c>. Returns the winning
    /// conversation id (which may have been set by a concurrent call). Returns
    /// <c>null</c> if the registration no longer exists.
    /// </summary>
    Task<ConversationId?> TryPinConversationAsync(
        WebhookId webhookId,
        ConversationId conversationId,
        CancellationToken ct = default);
}
