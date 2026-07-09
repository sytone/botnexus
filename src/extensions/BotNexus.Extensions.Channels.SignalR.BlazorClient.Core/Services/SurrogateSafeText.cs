namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Shared text-truncation helper for the Blazor portal preview surfaces (tool descriptions,
/// session debug snippets, steering queue rows). Mirrors the surrogate-safe boundary back-off in
/// <c>TelegramMessageSplitter.SliceSurrogateSafe</c>: cutting a UTF-16 string at a fixed code-unit
/// count can sever an astral character (emoji / CJK extension / rare glyph) that straddles the limit
/// into a lone high surrogate, which renders as U+FFFD. Backing off one code unit keeps previews
/// visually clean without pulling in the domain assembly (which must not ship in the WASM payload).
/// </summary>
public static class SurrogateSafeText
{
    /// <summary>
    /// Returns a prefix of <paramref name="value"/> at most <paramref name="max"/> UTF-16 code units
    /// long that never ends on a lone high surrogate. When the cut would land between the two halves of
    /// a surrogate pair, the length is reduced by one so the pair is dropped whole rather than severed.
    /// Values already within the limit (or null/empty) are returned unchanged.
    /// </summary>
    /// <param name="value">The source string to truncate. May be null.</param>
    /// <param name="max">The maximum number of UTF-16 code units to keep. Non-positive returns empty.</param>
    public static string SurrogateSafeTruncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        if (max <= 0)
        {
            return string.Empty;
        }

        var length = max;

        // If the cut lands on a high surrogate whose low half is beyond the limit, back off one code
        // unit so the astral character is dropped whole instead of leaving a lone (invalid) surrogate.
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
