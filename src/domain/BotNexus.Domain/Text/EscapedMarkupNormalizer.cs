using System.Text;
using System.Text.RegularExpressions;

namespace BotNexus.Domain.Text;

/// <summary>
/// Decodes common escape spellings of a character (<c>\uXXXX</c>, <c>\xXX</c>, HTML named and
/// numeric entities) into a scan buffer, runs a caller-supplied marker pattern against the
/// decoded text, and removes the matched spans from the ORIGINAL string (issue #2808).
/// </summary>
/// <remarks>
/// <para>
/// This exists because a sanitizer that scans raw text sees an injection marker only in its
/// literal spelling, while the model that later reads the stored text sees it decoded. The scan
/// and the interpretation happen at different encodings, and nothing normalised between them -
/// so <c>\u003c|im_start|\u003e</c> was inert at scan time and live at replay time.
/// </para>
/// <para>
/// <b>Why this lives in exactly one place.</b> The obvious alternative - adding an escaped-form
/// variant beside every literal pattern - would create a SECOND spelling of "what a marker looks
/// like". A duplicated definition of what is unsafe is precisely the defect class being fixed
/// here: the two spellings drift, and the newer marker is only added to one of them. So the
/// definition of "what a marker looks like" stays written exactly once, in literal form, and
/// the definition of "what an encoding is" stays written exactly once, here. Every sanitizer
/// (<c>MemoryContentSanitizer</c>, <c>AssistantTextSanitizer</c>) CONSUMES this seam rather than
/// restating either half.
/// </para>
/// <para>
/// Decoding is a single linear left-to-right pass with fixed-width lookahead, and the caller's
/// pattern is run once over the decoded buffer - no construct here can backtrack superlinearly.
/// </para>
/// </remarks>
public static class EscapedMarkupNormalizer
{
    // Longest named entity handled below ("&quot;" / "&apos;") plus headroom for numeric forms.
    private const int MaxEntityLength = 10;

    /// <summary>
    /// Returns <paramref name="text"/> with every span that matches <paramref name="pattern"/>
    /// removed, matching against a decoded view so escaped spellings of the marker are caught
    /// as well as the literal spelling.
    /// </summary>
    /// <param name="text">
    /// The untrusted text. Returned by reference when nothing matches, so a clean input costs
    /// no allocation beyond the scan buffer.
    /// </param>
    /// <param name="pattern">
    /// The marker pattern, written in LITERAL form only. It must be linear-time bounded - it is
    /// applied to attacker-controlled input.
    /// </param>
    /// <returns>The original text with all matched spans (in original spelling) deleted.</returns>
    public static string ReplaceMatches(string text, Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (string.IsNullOrEmpty(text))
            return text;

        // Cheap pre-check: no escape introducer means the decoded view equals the original, so
        // the ordinary literal scan is already correct and complete.
        if (text.IndexOf('\\') < 0 && text.IndexOf('&') < 0)
        {
            var direct = pattern.Replace(text, string.Empty);
            return ReferenceEquals(direct, text) || string.Equals(direct, text, StringComparison.Ordinal)
                ? text
                : direct;
        }

        var (decoded, originStart, originEnd) = Decode(text);
        var matches = pattern.Matches(decoded);
        if (matches.Count == 0)
            return text;

        // Map each decoded-space match back to the span of ORIGINAL characters that produced it,
        // then delete those spans. Matches arrive in ascending, non-overlapping order.
        var result = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Length == 0)
                continue;

            var start = originStart[match.Index];
            var end = originEnd[match.Index + match.Length - 1];
            if (start < cursor)
                continue;

            result.Append(text, cursor, start - cursor);
            cursor = end;
        }

        if (cursor == 0)
            return text;

        result.Append(text, cursor, text.Length - cursor);
        return result.ToString();
    }

    /// <summary>
    /// Decodes escape spellings into a scan buffer, recording for every decoded character the
    /// half-open span of original characters it came from.
    /// </summary>
    private static (string Decoded, int[] Start, int[] End) Decode(string text)
    {
        var buffer = new StringBuilder(text.Length);
        var start = new int[text.Length];
        var end = new int[text.Length];
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];
            var consumed = 1;
            var produced = ch;

            if (ch == '\\' && i + 1 < text.Length)
            {
                var kind = text[i + 1];
                if ((kind is 'u' or 'U') && TryHex(text, i + 2, 4, out var u))
                {
                    produced = (char)u;
                    consumed = 6;
                }
                else if ((kind is 'x' or 'X') && TryHex(text, i + 2, 2, out var x))
                {
                    produced = (char)x;
                    consumed = 4;
                }
            }
            else if (ch == '&' && TryEntity(text, i, out var entity, out var entityLength))
            {
                produced = entity;
                consumed = entityLength;
            }

            start[buffer.Length] = i;
            end[buffer.Length] = i + consumed;
            buffer.Append(produced);
            i += consumed;
        }

        return (buffer.ToString(), start, end);
    }

    private static bool TryHex(string text, int offset, int length, out int value)
    {
        value = 0;
        if (offset + length > text.Length)
            return false;

        for (var i = 0; i < length; i++)
        {
            var digit = HexDigit(text[offset + i]);
            if (digit < 0)
            {
                value = 0;
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }

    private static int HexDigit(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Recognises the entity forms that can spell a marker character: the five named XML
    /// entities plus decimal and hexadecimal numeric references. Bounded lookahead keeps the
    /// scan linear.
    /// </summary>
    private static bool TryEntity(string text, int offset, out char value, out int length)
    {
        value = '\0';
        length = 0;

        var limit = Math.Min(text.Length, offset + MaxEntityLength);
        var terminator = -1;
        for (var i = offset + 1; i < limit; i++)
        {
            if (text[i] == ';')
            {
                terminator = i;
                break;
            }
        }

        if (terminator < 0)
            return false;

        var body = text.AsSpan(offset + 1, terminator - offset - 1);
        if (body.Length == 0)
            return false;

        length = terminator - offset + 1;

        if (body[0] == '#')
        {
            var digits = body[1..];
            var hex = digits.Length > 0 && (digits[0] is 'x' or 'X');
            if (hex)
                digits = digits[1..];
            if (digits.Length == 0)
                return Fail(out value, out length);

            var code = 0;
            foreach (var digit in digits)
            {
                var parsed = hex ? HexDigit(digit) : (digit is >= '0' and <= '9' ? digit - '0' : -1);
                if (parsed < 0)
                    return Fail(out value, out length);
                code = hex ? (code << 4) | parsed : (code * 10) + parsed;
                if (code > char.MaxValue)
                    return Fail(out value, out length);
            }

            value = (char)code;
            return true;
        }

        value = body switch
        {
            "lt" => '<',
            "gt" => '>',
            "amp" => '&',
            "quot" => '"',
            "apos" => '\'',
            _ => '\0',
        };

        return value != '\0' || Fail(out value, out length);
    }

    private static bool Fail(out char value, out int length)
    {
        value = '\0';
        length = 0;
        return false;
    }
}
