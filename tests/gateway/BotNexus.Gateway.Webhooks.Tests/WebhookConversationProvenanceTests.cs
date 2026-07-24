using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Webhooks;
using Shouldly;

namespace BotNexus.Gateway.Webhooks.Tests;

public sealed class WebhookConversationProvenanceTests
{
    private static Conversation NewConversation() => new()
    {
        ConversationId = ConversationId.From("c-1"),
        AgentId = AgentId.From("a-1"),
    };

    [Fact]
    public void Stamp_WritesSourceAndWebhookId()
    {
        var conv = NewConversation();
        var webhookId = WebhookId.From("wh_abc123");

        WebhookConversationProvenance.Stamp(conv.Metadata, webhookId);

        conv.Metadata[WebhookConversationProvenance.SourceKey].ShouldBe(WebhookConversationProvenance.SourceWebhook);
        conv.Metadata[WebhookConversationProvenance.WebhookIdKey].ShouldBe("wh_abc123");
    }

    [Fact]
    public void TryGetWebhookId_WhenStamped_ReturnsTrueWithId()
    {
        var conv = NewConversation();
        WebhookConversationProvenance.Stamp(conv.Metadata, WebhookId.From("wh_abc123"));

        var id = WebhookConversationProvenance.TryGetWebhookId(conv);

        id.ShouldNotBeNull();
        id!.Value.Value.ShouldBe("wh_abc123");
    }

    [Fact]
    public void TryGetWebhookId_LegacyConversationWithoutProvenance_ReturnsFalse()
    {
        var conv = NewConversation();

        WebhookConversationProvenance.TryGetWebhookId(conv).ShouldBeNull();
    }

    [Fact]
    public void TryGetWebhookId_TitleLooksLikeWebhookButNoProvenance_ReturnsFalse()
    {
        // Identify by provenance, NOT title. A conversation merely titled "Webhook: ..." is not
        // treated as webhook-owned.
        var conv = NewConversation();
        conv.Title = "Webhook: Deploy notifications";

        WebhookConversationProvenance.TryGetWebhookId(conv).ShouldBeNull();
    }

    [Fact]
    public void TryGetWebhookId_WrongSource_ReturnsFalse()
    {
        var conv = NewConversation();
        conv.Metadata[WebhookConversationProvenance.SourceKey] = "cron";
        conv.Metadata[WebhookConversationProvenance.WebhookIdKey] = "wh_abc123";

        WebhookConversationProvenance.TryGetWebhookId(conv).ShouldBeNull();
    }

    [Fact]
    public void TryGetWebhookId_SurvivesJsonRoundTrip()
    {
        // Metadata is persisted as JSON; when hydrated from SQLite the values arrive as
        // JsonElement. The reader must still resolve the id.
        var conv = NewConversation();
        WebhookConversationProvenance.Stamp(conv.Metadata, WebhookId.From("wh_roundtrip"));

        var json = System.Text.Json.JsonSerializer.Serialize(conv.Metadata);
        var hydrated = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        var rehydrated = new Conversation
        {
            ConversationId = ConversationId.From("c-1"),
            AgentId = AgentId.From("a-1"),
            Metadata = hydrated,
        };

        var id = WebhookConversationProvenance.TryGetWebhookId(rehydrated);

        id.ShouldNotBeNull();
        id!.Value.Value.ShouldBe("wh_roundtrip");
    }
}
