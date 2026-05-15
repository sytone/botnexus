using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.TeamsProxy.Models;

public sealed class BotActivity
{
    public string? Type { get; set; }

    public string? Id { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    public string? ServiceUrl { get; set; }

    public string? ChannelId { get; set; }

    public string? Locale { get; set; }

    public ChannelAccount? From { get; set; }

    public ConversationAccount? Conversation { get; set; }

    public ChannelAccount? Recipient { get; set; }

    public string? Text { get; set; }

    public string? ReplyToId { get; set; }

    public string? TextFormat { get; set; }

    public JsonElement? Attachments { get; set; }

    public JsonElement? Entities { get; set; }

    public JsonElement? Value { get; set; }

    public JsonElement? ChannelData { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public string? GetInboundValidationError()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return "Activity id is required.";
        }

        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            return "Activity serviceUrl is required.";
        }

        if (string.IsNullOrWhiteSpace(Conversation?.Id))
        {
            return "Activity conversation.id is required.";
        }

        return null;
    }
}
