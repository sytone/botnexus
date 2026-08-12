using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace BotNexus.Domain.Text;

/// <summary>
/// Strips LLM control / role-injection markup from text that arrives from an untrusted source,
/// applied once at each boundary where such text enters the model's context or the durable store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two boundaries, one filter.</b> Untrusted text enters the system at more than one place:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The <b>memory-write</b> boundary (issue #1560). Session transcript turn pairs are auto-indexed
/// verbatim; the user half of each turn is the raw inbound message, attacker-controllable on any
/// channel. Unsanitized, model special tokens or role tags would be stored as trusted "memory" and
/// replayed to the model on a later turn via <c>memory_search</c> or the consolidation prompt — a
/// stored / delayed prompt-injection (memory-poisoning) vector.
/// </description></item>
/// <item><description>
/// The <b>web tool-output</b> boundary (issue #2813). A fetched page's content is fully controlled by
/// whoever owns the URL, and search snippets by whoever ranks for the query. That text is returned
/// as tool output directly into the turn, and lands in the transcript that the memory path then
/// persists — so the same hostile page reaches durable memory through a second, independent door.
/// </description></item>
/// </list>
/// <para>
/// <b>Why it lives in <c>BotNexus.Domain</c>.</b> The filter was originally scoped to the memory
/// path and named for it, which is precisely why the web tools were never wired to it: the sanitizer
/// looked like a memory concern rather than an "untrusted content entering context" concern. It now
/// sits beside <see cref="EscapedMarkupNormalizer"/> in the dependency-free domain leaf so every
/// boundary — memory writers, channel adapters, and the web tools in the extension layer — can
/// CONSUME the one definition. The alternative (a second sanitizer spelling under
/// <c>BotNexus.Extensions.WebTools</c>) would give "what a marker looks like" a second definition
/// that drifts, which is the exact defect class #2808 and this change exist to remove.
/// </para>
/// <para>
/// It is the C# analogue of OpenClaw's <c>sanitizeModelSpecialTokens</c> plus the tool-call /
/// role-directive / media / <c>NO_REPLY</c> stripping added in their
/// <c>sanitizeSessionMemoryTranscriptText</c> hook.
/// </para>
/// <para>
/// It intentionally removes only injection-class markup — ordinary angle brackets, pipes, and prose
/// are preserved so legitimate conversational content survives recall.
/// </para>
/// <para>
/// Every pattern below is written in LITERAL form only and is applied through
/// <see cref="EscapedMarkupNormalizer"/>, which decodes escape spellings into a scan buffer and
/// deletes the matching span from the original text (issue #2808). Do not add an escaped-form
/// twin of any pattern here: that would give "what a marker looks like" a second spelling, and a
/// duplicated definition of what is unsafe is exactly the defect this change removes. The
/// normalisation therefore lives in exactly one place and is consumed, never restated.
/// </para>
/// </remarks>
public static class UntrustedContentSanitizer
{
    // Special-token literals of the <|...|> family (im_start, im_end, endoftext, reserved_special_token_N,
    // fim_prefix, ...). Non-greedy, single line — these literals never span newlines.
    private static readonly Regex SpecialTokenPattern = new(
        @"<\|[^|>\r\n]*\|>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Tool-call / function-call directive blocks. Match an open tag through its matching close tag
    // (dotall) so the embedded JSON / nested invoke markup is removed wholesale; also handle a bare
    // open tag with no close.
    private static readonly Regex ToolCallBlockPattern = new(
        @"<(?:tool_call|function_calls|invoke|tool_use)\b[^>]*>.*?</(?:tool_call|function_calls|invoke|tool_use)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ToolCallStrayTagPattern = new(
        @"</?(?:tool_call|function_calls|invoke|tool_use|parameter)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // |DSML| directive markers, including the fullwidth-pipe (U+FF5C) evasion variant. Strips the
    // marker tokens themselves wherever they appear.
    private static readonly Regex DsmlDirectivePattern = new(
        "[|\uFF5C]DSML[|\uFF5C]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Role-directive blocks (<system>…</system>) and their bare open/close tags
    // (<assistant>, </user>, …). Block form first so inner content is removed, then any stray tag.
    private static readonly Regex RoleBlockPattern = new(
        @"<(system|assistant|user|tool)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RoleStrayTagPattern = new(
        @"</?(?:system|assistant|user|tool)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // <media:...> placeholders.
    private static readonly Regex MediaPlaceholderPattern = new(
        @"<media:[^>\r\n]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Standalone NO_REPLY marker — whole token only (word boundaries), so an incidental substring
    // like "no_reply_timeout" is preserved.
    private static readonly Regex NoReplyPattern = new(
        @"\bNO_REPLY\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="content"/> with all LLM control / role-injection markup removed.
    /// Null, empty, and markup-free input is returned unchanged (no allocation on the fast path).
    /// </summary>
    /// <param name="content">Untrusted text that may contain injection markup.</param>
    /// <returns>The sanitized text, safe to return as tool output or persist into the memory store.</returns>
    [return: NotNullIfNotNull(nameof(content))]
    public static string? Sanitize(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Fast-path: skip all regex work when no marker-introducing character class is present.
        if (!MightContainMarkup(content))
            return content;

        var text = content;
        text = EscapedMarkupNormalizer.ReplaceMatches(text, SpecialTokenPattern);
        text = EscapedMarkupNormalizer.ReplaceMatches(text, ToolCallBlockPattern);
        text = EscapedMarkupNormalizer.ReplaceMatches(text, RoleBlockPattern);
        text = EscapedMarkupNormalizer.ReplaceMatches(text, ToolCallStrayTagPattern);
        text = EscapedMarkupNormalizer.ReplaceMatches(text, RoleStrayTagPattern);
        text = EscapedMarkupNormalizer.ReplaceMatches(text, DsmlDirectivePattern);
        text = EscapedMarkupNormalizer.ReplaceMatches(text, MediaPlaceholderPattern);
        text = NoReplyPattern.Replace(text, string.Empty);

        return text;
    }

    private static bool MightContainMarkup(string text)
    {
        // Any of: an angle bracket (tags / placeholders), a pipe or fullwidth pipe (DSML / special
        // tokens), or the literal NO_REPLY marker. Cheap pre-check before compiled regex passes.
        // '\\' and '&' are included because an escaped marker (\u003c..., &lt;...) carries no
        // literal '<' at all - omitting them would reinstate the #2808 bypass in the fast path.
        return text.IndexOf('<') >= 0
            || text.IndexOf('\\') >= 0
            || text.IndexOf('&') >= 0
            || text.IndexOf('|') >= 0
            || text.IndexOf('\uFF5C') >= 0
            || text.Contains("NO_REPLY", StringComparison.Ordinal);
    }
}
