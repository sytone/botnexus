using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Authoritative provenance stamping and reading for webhook-owned conversations.
/// <para>
/// A conversation created by the inbound webhook endpoint is tagged with a stable source
/// marker and the originating <see cref="WebhookId"/> on its <see cref="Conversation.Metadata"/>.
/// Retention and any other automation identify webhook conversations by this provenance, never
/// by the human-readable <see cref="Conversation.Title"/> (which is user-editable and therefore
/// not authoritative).
/// </para>
/// </summary>
public static class WebhookConversationProvenance
{
    /// <summary>
    /// Metadata key whose value is the provenance source marker (<see cref="SourceWebhook"/>)
    /// for conversations opened by the inbound webhook endpoint.
    /// </summary>
    public const string SourceKey = "source";

    /// <summary>Provenance source value stamped on webhook-owned conversations.</summary>
    public const string SourceWebhook = "webhook";

    /// <summary>
    /// Metadata key whose value is the originating registration id (<see cref="WebhookId.Value"/>).
    /// </summary>
    public const string WebhookIdKey = "webhookId";

    /// <summary>
    /// Stamps webhook provenance onto <paramref name="metadata"/> so the resulting conversation
    /// can be identified as webhook-owned by its authoritative source id rather than its title.
    /// Overwrites any pre-existing provenance keys so the stamp is deterministic.
    /// </summary>
    /// <param name="metadata">The conversation metadata dictionary to stamp.</param>
    /// <param name="webhookId">The originating webhook registration id.</param>
    public static void Stamp(IDictionary<string, object?> metadata, WebhookId webhookId)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata[SourceKey] = SourceWebhook;
        metadata[WebhookIdKey] = webhookId.Value;
    }

    /// <summary>
    /// Attempts to read the authoritative webhook registration id from a conversation's provenance.
    /// Returns <c>true</c> only when the conversation is marked with the webhook source and carries a
    /// non-empty <see cref="WebhookIdKey"/> value. Legacy conversations created before provenance
    /// stamping (no <see cref="SourceKey"/>) return <c>false</c> and are therefore never treated as
    /// webhook-owned by retention.
    /// </summary>
    /// <remarks>
    /// Metadata values survive persistence as JSON; when hydrated from SQLite the stored strings
    /// arrive as <see cref="System.Text.Json.JsonElement"/>. Reading through <see cref="object.ToString"/>
    /// yields the underlying string in both the live (in-memory <see cref="string"/>) and the
    /// round-tripped (<c>JsonElement</c>) cases, so this reader is storage-agnostic.
    /// </remarks>
    /// <param name="conversation">The conversation to inspect.</param>
    /// <returns>The resolved webhook id, or <c>null</c> when the conversation is not webhook-owned.</returns>
    public static WebhookId? TryGetWebhookId(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var metadata = conversation.Metadata;
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(SourceKey, out var source)
            || source?.ToString() is not { } sourceText
            || !string.Equals(sourceText, SourceWebhook, StringComparison.Ordinal))
        {
            return null;
        }

        if (!metadata.TryGetValue(WebhookIdKey, out var idValue)
            || idValue?.ToString() is not { Length: > 0 } idText)
        {
            return null;
        }

        return WebhookId.From(idText);
    }
}
