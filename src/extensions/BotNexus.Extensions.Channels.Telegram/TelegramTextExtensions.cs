namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Telegram-side <c>this string</c> text extensions (#2925), so an engineer holding a message
/// string can discover the MarkdownV2 conversion and escaping without knowing the formatter class
/// name.
/// </summary>
/// <remarks>
/// These live in the Telegram channel assembly rather than the domain home
/// (<c>BotNexus.Domain.Text.StringTextExtensions</c>) because MarkdownV2 escaping is channel
/// grammar, not general-purpose text policy, and hoisting it into the domain would put a
/// Telegram-specific operation on every string in the product.
/// </remarks>
internal static class TelegramTextExtensions
{
    /// <summary>
    /// Converts a markdown string (as produced by LLMs) into Telegram MarkdownV2 format.
    /// Recognized formatting is converted; all other special characters are escaped.
    /// Returns an empty string for null/empty input.
    /// </summary>
    public static string ToTelegramMarkdownV2(this string? markdown)
        => TelegramMarkdownFormatter.ConvertCore(markdown);

    /// <summary>
    /// Escapes all MarkdownV2 special characters in a plain-text string.
    /// Use this for structural strings (display prefixes, tool names, labels) that
    /// must appear as literal text with no formatting applied.
    /// </summary>
    public static string EscapeTelegramMarkdownV2(this string? text)
        => TelegramMarkdownFormatter.EscapeMarkdownV2Core(text);
}
