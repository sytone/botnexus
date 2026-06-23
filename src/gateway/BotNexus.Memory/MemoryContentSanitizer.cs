using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace BotNexus.Memory;

/// <summary>
/// Strips LLM control / role-injection markup from raw transcript text <b>before</b> it is
/// persisted into the searchable memory store (and as defence-in-depth, when historical rows
/// are recalled).
/// </summary>
/// <remarks>
/// <para>
/// Session transcript turn pairs are auto-indexed verbatim by <see cref="MemoryIndexer"/> and
/// <see cref="MarkdownAgentMemory"/>. The user half of each turn is the <i>raw inbound message</i>,
/// which is attacker-controllable on any channel. Without sanitization, a message that embeds model
/// special tokens, tool-call directives, or role tags would be stored as trusted "memory" and
/// replayed back to the model on a later turn via <c>memory_search</c> or the memory-dreaming
/// consolidation prompt — a stored / delayed prompt-injection (memory-poisoning) vector (issue #1560).
/// </para>
/// <para>
/// This is the canonical filter shared by every memory writer so the indexer, the agent-memory
/// provider, and any future writer apply one consistent strip. It is the C# analogue of OpenClaw's
/// <c>sanitizeModelSpecialTokens</c> plus the tool-call / role-directive / media / <c>NO_REPLY</c>
/// stripping added in their <c>sanitizeSessionMemoryTranscriptText</c> hook.
/// </para>
/// <para>
/// It intentionally removes only injection-class markup — ordinary angle brackets, pipes, and prose
/// are preserved so legitimate conversational content survives recall.
/// </para>
/// </remarks>
public static class MemoryContentSanitizer
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
    /// <param name="content">Raw transcript text that may contain injection markup.</param>
    /// <returns>The sanitized text, safe to persist into the memory store.</returns>
    [return: NotNullIfNotNull(nameof(content))]
    public static string? Sanitize(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Fast-path: skip all regex work when no marker-introducing character class is present.
        if (!MightContainMarkup(content))
            return content;

        var text = content;
        text = SpecialTokenPattern.Replace(text, string.Empty);
        text = ToolCallBlockPattern.Replace(text, string.Empty);
        text = RoleBlockPattern.Replace(text, string.Empty);
        text = ToolCallStrayTagPattern.Replace(text, string.Empty);
        text = RoleStrayTagPattern.Replace(text, string.Empty);
        text = DsmlDirectivePattern.Replace(text, string.Empty);
        text = MediaPlaceholderPattern.Replace(text, string.Empty);
        text = NoReplyPattern.Replace(text, string.Empty);

        return text;
    }

    private static bool MightContainMarkup(string text)
    {
        // Any of: an angle bracket (tags / placeholders), a pipe or fullwidth pipe (DSML / special
        // tokens), or the literal NO_REPLY marker. Cheap pre-check before compiled regex passes.
        return text.IndexOf('<') >= 0
            || text.IndexOf('|') >= 0
            || text.IndexOf('\uFF5C') >= 0
            || text.Contains("NO_REPLY", StringComparison.Ordinal);
    }
}
