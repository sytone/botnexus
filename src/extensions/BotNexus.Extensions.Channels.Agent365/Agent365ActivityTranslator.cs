using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Agents.Core.Models;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Pure, side-effect-free translation between the Microsoft 365 Agents SDK <see cref="Activity"/>
/// protocol and the BotNexus <see cref="InboundMessage"/>/<see cref="OutboundMessage"/> contracts.
/// </summary>
/// <remarks>
/// <para>
/// This is the core testable logic of the Agent 365 adapter. It is deliberately kept free of the
/// SDK connector, HTTP, and DI so both directions can be unit-tested by constructing plain objects
/// (see <c>Agent365ActivityTranslatorTests</c>). The adapter and inbound controller call these
/// methods and layer the transport concerns (dispatch, connector send) on top.
/// </para>
/// <para>
/// The channel identity <c>agent365</c> is a <see cref="ChannelKey"/> string; no central enum edit
/// is needed because <see cref="ChannelKey"/> is a stringly-typed value.
/// </para>
/// </remarks>
public static class Agent365ActivityTranslator
{
    /// <summary>The BotNexus channel key for the Agent 365 adapter.</summary>
    public static readonly ChannelKey ChannelKey = ChannelKey.From("agent365");

    /// <summary>
    /// Translates an inbound Agents SDK <see cref="Activity"/> (a <c>message</c>-type activity) into a
    /// BotNexus <see cref="InboundMessage"/>. Returns <see langword="null"/> when the activity is not
    /// a dispatchable user message (non-message type, or a message with neither text nor a supported
    /// attachment), so the caller can quietly acknowledge non-actionable activities.
    /// </summary>
    /// <param name="activity">The inbound activity.</param>
    /// <param name="targetAgentId">Optional agent binding to stamp as a routing hint.</param>
    /// <returns>The translated inbound message, or null when the activity is not actionable.</returns>
    public static InboundMessage? ToInboundMessage(Activity activity, string? targetAgentId = null)
    {
        ArgumentNullException.ThrowIfNull(activity);

        // Register tier only handles message activities. Conversation-update, typing, event, etc.
        // are acknowledged by the transport but produce no InboundMessage.
        if (!string.Equals(activity.Type, ActivityTypes.Message, StringComparison.OrdinalIgnoreCase))
            return null;

        var text = activity.Text ?? string.Empty;
        var contentParts = BuildContentParts(activity.Attachments);

        // Nothing to route: no text and no usable attachment.
        if (string.IsNullOrWhiteSpace(text) && contentParts is null)
            return null;

        var senderId = activity.From?.Id;
        if (string.IsNullOrWhiteSpace(senderId))
            return null;

        var conversationId = activity.Conversation?.Id;
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var metadata = new Dictionary<string, object?>
        {
            ["agent365ActivityId"] = activity.Id,
            ["agent365ChannelId"] = SafeChannelId(activity),
            ["agent365ServiceUrl"] = activity.ServiceUrl,
            ["agent365ConversationId"] = conversationId,
            ["agent365Recipient"] = activity.Recipient?.Id,
            ["agent365SenderName"] = activity.From?.Name,
        };

        return new InboundMessage
        {
            ChannelType = ChannelKey,
            SenderId = senderId,
            Sender = CitizenId.Of(UserId.From(senderId)),
            ChannelAddress = Agent365ChannelAddress.Encode(conversationId, activity.ServiceUrl),
            Content = text,
            ContentParts = contentParts,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                targetAgentId: targetAgentId,
                sessionId: null,
                conversationId: null),
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Builds an outbound Agents SDK reply <see cref="Activity"/> from a BotNexus
    /// <see cref="OutboundMessage"/>. The returned activity is a <c>message</c> activity carrying the
    /// content text and the decoded conversation/service-url reply target; the caller sends it via the
    /// connector's <c>ReplyToActivityAsync</c>. The optional <paramref name="replyToId"/> threads the
    /// reply against the originating inbound activity when known.
    /// </summary>
    /// <param name="message">The outbound message produced by the BotNexus loop.</param>
    /// <param name="replyToId">Optional inbound activity id to thread the reply against.</param>
    /// <returns>A reply activity ready to send through the connector.</returns>
    public static Activity ToReplyActivity(OutboundMessage message, string? replyToId = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        Agent365ChannelAddress.TryDecode(message.ChannelAddress, out var conversationId, out var serviceUrl);

        var content = message.Content ?? string.Empty;
        var text = string.IsNullOrWhiteSpace(message.DisplayPrefix)
            ? content
            : string.Concat(message.DisplayPrefix.Trim(), " ", content);

        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = text,
            ReplyToId = replyToId,
            ServiceUrl = serviceUrl,
            Conversation = string.IsNullOrEmpty(conversationId)
                ? null
                : new ConversationAccount { Id = conversationId },
        };

        return activity;
    }

    /// <summary>
    /// Maps supported Agents SDK <see cref="Attachment"/>s onto BotNexus
    /// <see cref="MessageContentPart"/>s. Only inline image attachments with a content URL are
    /// carried in the Register tier (vision-capable model input); everything else is ignored so
    /// unsupported attachment types never block dispatch. Returns null when no usable attachment is
    /// present.
    /// </summary>
    private static IReadOnlyList<MessageContentPart>? BuildContentParts(IList<Attachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
            return null;

        List<MessageContentPart>? parts = null;
        foreach (var attachment in attachments)
        {
            if (attachment is null)
                continue;

            var contentType = attachment.ContentType;
            if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(attachment.ContentUrl))
                continue;

            parts ??= [];
            parts.Add(new ReferenceContentPart
            {
                MimeType = contentType,
                Uri = attachment.ContentUrl,
                FileName = attachment.Name,
            });
        }

        return parts;
    }

    // ChannelId is an Agents SDK struct whose ToString() dereferences an inner value that is null on
    // a default-constructed Activity (e.g. a bare test payload or an activity with no channel set).
    // Guard it so translation never NREs on a partially-populated activity.
    private static string SafeChannelId(Activity activity)
    {
        try
        {
            return activity.ChannelId.ToString() ?? string.Empty;
        }
        catch (NullReferenceException)
        {
            return string.Empty;
        }
    }
}
