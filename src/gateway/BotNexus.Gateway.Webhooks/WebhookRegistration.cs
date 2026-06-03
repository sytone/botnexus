using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Persisted registration of an inbound webhook endpoint. A registration binds
/// a <see cref="AgentId"/> (and optionally a <see cref="ConversationId"/>) to a
/// shared HMAC secret. Every inbound POST must carry a valid
/// <c>X-BotNexus-Signature-256</c> header computed with that secret.
///
/// <para>
/// <strong>Secret storage:</strong> The plaintext secret is stored in the SQLite
/// database (protected by OS file permissions), allowing HMAC verification at
/// request time. The secret is shown to the user exactly once at registration and
/// is not re-exposed via the API. Consistent with how the gateway API token is
/// stored in config.json.
/// </para>
/// </summary>
public sealed record WebhookRegistration
{
    /// <summary>Stable, opaque registration identifier (e.g. <c>wh_abc123def456</c>).</summary>
    public required WebhookId Id { get; init; }

    /// <summary>Human-readable label for portal display.</summary>
    public required string Label { get; init; }

    /// <summary>Target agent that will receive and process inbound messages.</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>
    /// Optional conversation to pin all inbound messages to. When <c>null</c>, a
    /// conversation is created on the first POST and reused for all subsequent POSTs
    /// to this webhook (per-webhook conversation, not per-call). Stamped via CAS —
    /// see <see cref="IWebhookRegistrationStore.TryPinConversationAsync"/>.
    /// </summary>
    public ConversationId? PinnedConversationId { get; init; }

    /// <summary>
    /// Plaintext HMAC secret. Stored in SQLite protected by OS file permissions.
    /// Never returned via the API after the initial create response.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Default response mode applied when the caller does not supply
    /// <c>responseMode</c> in the request body.
    /// </summary>
    public WebhookResponseMode DefaultResponseMode { get; init; } = WebhookResponseMode.Async;

    /// <summary>Whether this registration will accept inbound POSTs.</summary>
    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}
