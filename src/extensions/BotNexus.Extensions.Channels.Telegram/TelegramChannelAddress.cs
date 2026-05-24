using System.Globalization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Telegram-specific encoder/decoder that folds the native
/// <c>message_thread_id</c> (forum topic) into the opaque
/// <see cref="ChannelAddress"/> the core uses for routing.
/// </summary>
/// <remarks>
/// The chosen format is <c>&lt;chatId&gt;/topic:&lt;threadId&gt;</c>. Examples:
/// <list type="bullet">
///   <item><description>chat <c>12345</c> root: <c>12345</c></description></item>
///   <item><description>chat <c>12345</c> topic <c>67</c>: <c>12345/topic:67</c></description></item>
///   <item><description>supergroup <c>-1001234567890</c> topic <c>5</c>: <c>-1001234567890/topic:5</c></description></item>
///   <item><description>general topic (always <c>1</c> on Telegram): <c>12345/topic:1</c></description></item>
/// </list>
/// <para>
/// <c>ChannelAddress</c> itself remains opaque to the core router — this encoding
/// is a Telegram convention. Future channel extensions (Teams, Slack) define their
/// own encoding because their native sub-address shapes differ.
/// </para>
/// <para>
/// Slash is illegal inside a Telegram chat ID (signed 64-bit integer) so it is
/// a safe delimiter for this channel. The <c>topic:</c> prefix keeps the encoding
/// self-describing in logs and SQL.
/// </para>
/// </remarks>
public static class TelegramChannelAddress
{
    private const string TopicSeparator = "/topic:";

    /// <summary>
    /// Encodes a Telegram chat ID and optional forum-topic identifier into a
    /// <see cref="ChannelAddress"/>. When <paramref name="messageThreadId"/> is
    /// <see langword="null"/> the address is just the bare chat ID.
    /// </summary>
    public static ChannelAddress Encode(long chatId, int? messageThreadId)
    {
        var primary = chatId.ToString(CultureInfo.InvariantCulture);
        if (messageThreadId is null)
            return ChannelAddress.From(primary);

        var composite = string.Concat(
            primary,
            TopicSeparator,
            messageThreadId.Value.ToString(CultureInfo.InvariantCulture));
        return ChannelAddress.From(composite);
    }

    /// <summary>
    /// Attempts to decode a Telegram-encoded <see cref="ChannelAddress"/> back to
    /// the originating <c>(chatId, messageThreadId?)</c> pair. Returns
    /// <see langword="false"/> when the primary segment is not a valid 64-bit
    /// integer; non-numeric or empty topic segments are treated as "no topic"
    /// rather than failing the whole decode, because legacy bindings created via
    /// the REST API may carry arbitrary <c>thread_id</c> strings that the
    /// migration appends verbatim.
    /// </summary>
    public static bool TryDecode(ChannelAddress address, out long chatId, out int? messageThreadId)
    {
        chatId = 0;
        messageThreadId = null;

        if (address.IsEmpty)
            return false;

        var value = address.Value;
        var separatorIndex = value.LastIndexOf(TopicSeparator, StringComparison.Ordinal);

        string primary;
        string? topic;
        if (separatorIndex < 0)
        {
            primary = value;
            topic = null;
        }
        else
        {
            primary = value.Substring(0, separatorIndex);
            topic = value.Substring(separatorIndex + TopicSeparator.Length);
            if (topic.Length == 0)
                topic = null;
        }

        if (!long.TryParse(primary, NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId))
            return false;

        if (topic is not null
            && int.TryParse(topic, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTopic))
        {
            messageThreadId = parsedTopic;
        }

        return true;
    }
}
