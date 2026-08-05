using System.Globalization;
using System.Text;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Encodes and decodes the opaque <c>callback_data</c> carried by an <c>ask_user</c> inline-keyboard
/// button (#2323).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a token rather than the choice text.</b> The Telegram Bot API caps <c>callback_data</c> at
/// <b>64 BYTES</b> - not 64 characters. The limit applies to the UTF-8 encoding, so a payload built
/// from a user-visible choice label can exceed it at well under 64 characters (any emoji or
/// non-Latin script costs 2-4 bytes per character). Telegram rejects the entire <c>sendMessage</c>
/// call when any single button breaches the cap, which would take the whole prompt down with it -
/// the agent then blocks forever on an <c>ask_user</c> the user never saw.
/// </para>
/// <para>
/// The token therefore carries only a fixed-shape correlation pair: the ask_user request id and the
/// zero-based <em>index</em> of the choice. Choice text never appears. With the gateway's 32-char
/// request ids (<c>Guid.ToString("N")</c>) a token is 39 bytes at three-digit indexes, leaving
/// generous headroom. <see cref="TryEncode"/> nevertheless re-measures the encoded byte length on
/// every call and refuses rather than assumes: request-id generation is not this file's to
/// guarantee, and a silent breach here is a dropped prompt in production.
/// </para>
/// <para>
/// <b>The index is resolved back to a value at handling time</b> against the choices carried by the
/// pending prompt, which is also what makes a stale button press on a superseded prompt harmless:
/// the request id will not match and the resolver rejects it.
/// </para>
/// </remarks>
internal static class TelegramAskUserCallbackToken
{
    /// <summary>
    /// Telegram's hard ceiling on <c>callback_data</c>, in UTF-8 bytes.
    /// Reference: https://core.telegram.org/bots/api#inlinekeyboardbutton
    /// </summary>
    internal const int MaxCallbackDataBytes = 64;

    /// <summary>Discriminator prefix so unrelated future callback payloads are distinguishable.</summary>
    private const string Prefix = "au:";

    /// <summary>
    /// Builds the callback payload for choice <paramref name="choiceIndex"/> of
    /// <paramref name="requestId"/>, returning <see langword="false"/> when the result would breach
    /// <see cref="MaxCallbackDataBytes"/>. A false return is a signal to degrade the whole prompt to
    /// a numbered text list, never to send a truncated (and therefore un-decodable) token.
    /// </summary>
    internal static bool TryEncode(string requestId, int choiceIndex, out string callbackData)
    {
        callbackData = string.Empty;

        if (string.IsNullOrWhiteSpace(requestId) || requestId.Contains(':', StringComparison.Ordinal) || choiceIndex < 0)
            return false;

        var candidate = string.Concat(
            Prefix,
            requestId,
            ":",
            choiceIndex.ToString(CultureInfo.InvariantCulture));

        // Measure BYTES, not characters. See the type remarks.
        if (Encoding.UTF8.GetByteCount(candidate) > MaxCallbackDataBytes)
            return false;

        callbackData = candidate;
        return true;
    }

    /// <summary>
    /// Parses a callback payload produced by <see cref="TryEncode"/>. Returns <see langword="false"/>
    /// for anything else, including payloads from other features and hand-crafted junk.
    /// </summary>
    internal static bool TryDecode(string? callbackData, out string requestId, out int choiceIndex)
    {
        requestId = string.Empty;
        choiceIndex = -1;

        if (string.IsNullOrEmpty(callbackData) || !callbackData.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var body = callbackData[Prefix.Length..];
        var separator = body.LastIndexOf(':');
        if (separator <= 0 || separator == body.Length - 1)
            return false;

        var id = body[..separator];
        if (!int.TryParse(body[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            return false;

        requestId = id;
        choiceIndex = index;
        return true;
    }
}
