namespace BotNexus.Domain.Text;

/// <summary>
/// The single definition of BotNexus's cheap, script-aware token estimate (#3655).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> The estimate was previously a bare <c>chars / 4</c> written out
/// four separate times - in the compaction estimator, in <c>MemoryPromptBudget</c>, in the
/// <c>/context</c> report and in the skills report. Four independent copies of a heuristic is four
/// places to fix when the heuristic is wrong, and it was wrong: <c>chars / 4</c> encodes an
/// English-text assumption. Under a BPE tokenizer Latin text averages roughly four characters per
/// token, but a CJK character is typically a token on its own. A CJK-heavy session therefore
/// reported about a quarter of its real context consumption, and the estimate-based compaction
/// trigger did not fire until the session was already far past its threshold.
/// </para>
/// <para>
/// <b>What this is not.</b> This is not a tokenizer and must never be mistaken for one. It weights
/// CJK code points at ~1 token and everything else at ~1/4 token, which is deliberately crude: the
/// consumers are budget guards and operator-facing reports, both of which need an estimate in the
/// right order of magnitude rather than an exact count. The authoritative number, where one is
/// available at all, is the provider's own reported prompt-token count.
/// </para>
/// <para>
/// <b>Why weighted units rather than tokens.</b> Callers that aggregate over many fragments sum
/// <see cref="WeightedCharUnits(string?)"/> (quarter-token units) and convert once with
/// <see cref="TokensFromUnits"/>. Rounding each fragment to whole tokens first would lose up to
/// three quarter-tokens per fragment, which over a long transcript is a systematic under-count of
/// exactly the kind this type was created to remove.
/// </para>
/// </remarks>
public static class TokenEstimator
{
    /// <summary>
    /// Characters per token for non-CJK text - the historical ratio, retained because it is a good
    /// approximation for Latin script and because callers that only hold a character count (JSON
    /// tool schemas, character budgets) have no text to inspect.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Weighted units contributed by a CJK code point. Equal to <see cref="CharsPerToken"/> so that
    /// one CJK character costs one whole token after conversion.
    /// </summary>
    private const int CjkUnits = CharsPerToken;

    /// <summary>
    /// Weighted quarter-token units for <paramref name="text"/>: <see cref="CjkUnits"/> per CJK code
    /// point and 1 per other UTF-16 code unit. Sum these across fragments and convert once.
    /// </summary>
    /// <param name="text">Text to size. Null and empty both cost zero.</param>
    /// <returns>Weighted units, never negative.</returns>
    public static long WeightedCharUnits(string? text)
        => string.IsNullOrEmpty(text) ? 0L : WeightedCharUnits(text.AsSpan());

    /// <summary>
    /// Span overload of <see cref="WeightedCharUnits(string?)"/>, so hot paths can avoid a
    /// substring allocation.
    /// </summary>
    /// <param name="text">Text to size.</param>
    /// <returns>Weighted units, never negative.</returns>
    public static long WeightedCharUnits(ReadOnlySpan<char> text)
    {
        var units = 0L;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Surrogate pairs are combined before classification: CJK Extension B and later live in
            // the astral planes, and classifying the halves separately would charge a single Han
            // ideograph as two non-CJK characters - the exact under-count this type removes.
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                units += IsCjk(char.ConvertToUtf32(c, text[i + 1])) ? CjkUnits : 1;
                i++;
                continue;
            }

            units += IsCjk(c) ? CjkUnits : 1;
        }

        return units;
    }

    /// <summary>
    /// Converts weighted units to whole tokens. Truncating rather than rounding keeps this
    /// identical to the historical <c>chars / 4</c> for pure-Latin input, so the common case does
    /// not shift by a token when this type is adopted.
    /// </summary>
    /// <param name="units">Weighted units, typically summed from <see cref="WeightedCharUnits(string?)"/>.</param>
    /// <returns>An estimated token count, clamped to <see cref="int.MaxValue"/>.</returns>
    public static int TokensFromUnits(long units)
        => units <= 0 ? 0 : (int)Math.Min(units / CharsPerToken, int.MaxValue);

    /// <summary>
    /// Script-aware token estimate for a single piece of text.
    /// </summary>
    /// <param name="text">Text to size. Null and empty both estimate zero.</param>
    /// <returns>An estimated token count.</returns>
    public static int EstimateTokens(string? text) => TokensFromUnits(WeightedCharUnits(text));

    /// <summary>
    /// Script-blind fallback for callers that hold only a character count and cannot inspect the
    /// text - character budgets, and JSON tool schemas whose structural bytes are ASCII by
    /// construction. Equivalent to the historical <c>chars / 4</c>, and named so that its blindness
    /// is visible at the call site rather than implied.
    /// </summary>
    /// <param name="chars">Character count. Non-positive values estimate zero.</param>
    /// <returns>An estimated token count.</returns>
    public static int EstimateTokensFromCharCount(long chars)
        => chars <= 0 ? 0 : (int)Math.Min(chars / CharsPerToken, int.MaxValue);

    /// <summary>
    /// Whether <paramref name="codePoint"/> falls in a range where a character is usually its own
    /// token: Han, Hiragana, Katakana, Hangul, and the CJK symbol/compatibility/fullwidth blocks.
    /// </summary>
    /// <remarks>
    /// The ranges are deliberately coarse. A few non-ideographic code points (fullwidth Latin, for
    /// instance) are swept in; over-charging a handful of characters is the safe direction for a
    /// budget guard, whereas under-charging is the defect being fixed.
    /// </remarks>
    /// <param name="codePoint">A Unicode code point.</param>
    /// <returns><c>true</c> when the code point should be weighted as a whole token.</returns>
    public static bool IsCjk(int codePoint) => codePoint switch
    {
        >= 0x1100 and <= 0x11FF => true,   // Hangul Jamo
        >= 0x2E80 and <= 0x303F => true,   // CJK radicals, Kangxi, CJK symbols and punctuation
        >= 0x3040 and <= 0x33FF => true,   // Hiragana, Katakana, Bopomofo, Hangul Compat Jamo, CJK compat
        >= 0x3400 and <= 0x4DBF => true,   // CJK Unified Ideographs Extension A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK Unified Ideographs
        >= 0xA000 and <= 0xA4CF => true,   // Yi syllables and radicals
        >= 0xAC00 and <= 0xD7AF => true,   // Hangul syllables and Jamo Extended-B
        >= 0xF900 and <= 0xFAFF => true,   // CJK Compatibility Ideographs
        >= 0xFE30 and <= 0xFE4F => true,   // CJK Compatibility Forms
        >= 0xFF00 and <= 0xFF60 => true,   // Halfwidth and Fullwidth Forms (fullwidth block)
        >= 0xFFE0 and <= 0xFFE6 => true,   // Fullwidth currency and sign forms
        >= 0x20000 and <= 0x3FFFF => true, // CJK Unified Ideographs Extension B and later
        _ => false
    };
}
