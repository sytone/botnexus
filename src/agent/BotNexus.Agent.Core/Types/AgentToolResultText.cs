namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Extracts the human/forensics-readable text of an <see cref="AgentToolResult"/> (issue #2906).
/// </summary>
/// <remarks>
/// <para>
/// This is the single place the "what text does this tool result hold?" question is answered, so
/// the session-history writer, the hook dispatcher, and any future consumer cannot drift apart the
/// way they had before #2906. The observed damage that motivated it: a caller that reached for
/// <c>Content.FirstOrDefault()?.ToString()</c> got the record's compiler-generated ToString -
/// <c>AgentToolContent { Type = Text, Value = ... }</c> - and persisted THAT as the transcript
/// content instead of the value.
/// </para>
/// <para>
/// Only <see cref="AgentToolContentType.Text"/> blocks contribute. An image block's value is a
/// base64/data-URI payload that is worthless in a transcript and enormous in a database, so it is
/// skipped rather than concatenated.
/// </para>
/// </remarks>
public static class AgentToolResultText
{
    /// <summary>
    /// Extracts the concatenated text of a tool result.
    /// </summary>
    /// <param name="result">The tool result to read; may be <see langword="null"/>.</param>
    /// <returns>
    /// The joined text of every text block, or <see langword="null"/> when the result is null or
    /// carries no text block at all (which callers render as an explicit placeholder).
    /// </returns>
    public static string? Extract(AgentToolResult? result)
        => result is null ? null : Extract(result.Content);

    /// <summary>
    /// Extracts the concatenated text of a tool result's content blocks.
    /// </summary>
    /// <param name="content">The content blocks to read; may be <see langword="null"/>.</param>
    /// <returns>The joined text, or <see langword="null"/> when there is no text block.</returns>
    public static string? Extract(IReadOnlyList<AgentToolContent>? content)
    {
        if (content is null || content.Count == 0)
            return null;

        // Fast path: the overwhelmingly common single-text-block shape allocates nothing extra.
        if (content.Count == 1)
            return content[0].Type == AgentToolContentType.Text ? content[0].Value : null;

        List<string>? texts = null;
        foreach (var block in content)
        {
            if (block.Type != AgentToolContentType.Text)
                continue;

            texts ??= [];
            texts.Add(block.Value);
        }

        return texts is null ? null : string.Join("\n", texts);
    }
}
