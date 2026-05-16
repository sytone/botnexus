namespace BotNexus.TeamsProxy.Models;

public sealed class BotNexusOutboundMessage
{
    public string? ResponseId { get; set; }

    public string? ServiceUrl { get; set; }

    public string? ConversationId { get; set; }

    public string? ReplyToActivityId { get; set; }

    public string? Text { get; set; }

    public string? Locale { get; set; }

    public string? ChannelId { get; set; }

    public ChannelAccount? From { get; set; }

    public ConversationAccount? Conversation { get; set; }

    public ChannelAccount? Recipient { get; set; }

    public BotActivity? Activity { get; set; }

    public string GetMessageId()
    {
        return !string.IsNullOrWhiteSpace(ResponseId)
            ? ResponseId
            : Guid.NewGuid().ToString("n");
    }

    public BotActivity ToActivity()
    {
        if (Activity is not null)
        {
            Activity.Type ??= "message";
            return Activity;
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new InvalidOperationException("Outbound message must include either text or activity.");
        }

        return new BotActivity
        {
            Type = "message",
            ServiceUrl = ServiceUrl,
            ChannelId = ChannelId,
            Conversation = Conversation ?? new ConversationAccount { Id = ConversationId },
            From = From,
            Recipient = Recipient,
            ReplyToId = ReplyToActivityId,
            Text = Text,
            Locale = Locale
        };
    }

    public string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(ServiceUrl))
        {
            return "serviceUrl is required.";
        }

        if (string.IsNullOrWhiteSpace(ConversationId))
        {
            return "conversationId is required.";
        }

        if (Activity is null && string.IsNullOrWhiteSpace(Text))
        {
            return "Either text or activity is required.";
        }

        return null;
    }
}
