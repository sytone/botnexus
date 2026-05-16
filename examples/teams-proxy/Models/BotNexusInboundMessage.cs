using System.Text.Json;

namespace BotNexus.TeamsProxy.Models;

public sealed class BotNexusInboundMessage
{
    public required string MessageId { get; init; }

    public required string ActivityId { get; init; }

    public required string ConversationId { get; init; }

    public required string ServiceUrl { get; init; }

    public string? ChannelId { get; init; }

    public string? TenantId { get; init; }

    public ChannelAccount? From { get; init; }

    public ChannelAccount? Recipient { get; init; }

    public string? Text { get; init; }

    public JsonElement? Attachments { get; init; }

    public JsonElement? Entities { get; init; }

    public JsonElement? Value { get; init; }

    public JsonElement? ChannelData { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public required BotActivity RawActivity { get; init; }

    public static BotNexusInboundMessage FromActivity(BotActivity activity)
    {
        var activityId = activity.Id
            ?? throw new InvalidOperationException("Activity id is required before queueing.");
        var conversationId = activity.Conversation?.Id
            ?? throw new InvalidOperationException("Conversation id is required before queueing.");
        var serviceUrl = activity.ServiceUrl
            ?? throw new InvalidOperationException("Service URL is required before queueing.");

        return new BotNexusInboundMessage
        {
            MessageId = activityId,
            ActivityId = activityId,
            ConversationId = conversationId,
            ServiceUrl = serviceUrl,
            ChannelId = activity.ChannelId,
            TenantId = activity.Conversation?.TenantId ?? TryReadTeamsTenantId(activity.ChannelData),
            From = activity.From,
            Recipient = activity.Recipient,
            Text = activity.Text,
            Attachments = activity.Attachments,
            Entities = activity.Entities,
            Value = activity.Value,
            ChannelData = activity.ChannelData,
            ReceivedAt = DateTimeOffset.UtcNow,
            RawActivity = activity
        };
    }

    private static string? TryReadTeamsTenantId(JsonElement? channelData)
    {
        if (channelData is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }

        return root.TryGetProperty("tenant", out var tenant)
            && tenant.ValueKind == JsonValueKind.Object
            && tenant.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
    }
}
