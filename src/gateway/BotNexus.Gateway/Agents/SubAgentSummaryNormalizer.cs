using System.Text;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Normalizes pathological "token-per-line" whitespace that some sub-agent/model
/// results embed into their final completion summary (e.g. a heading and prose
/// broken into a single token per line with mixed CRLF/LF sequences).
/// </summary>
/// <remarks>
/// The normalizer collapses runs of single-token lines back into their original
/// paragraph/heading while preserving intentional Markdown structure:
/// fenced code blocks are emitted verbatim, blank-line paragraph breaks are kept
/// (collapsed to at most one blank line), and normal multi-word Markdown lines
/// (list items, tables, prose) are left untouched because a line is only joined
/// onto the previous one when it is a single whitespace-free token fragment.
/// </remarks>
public static class SubAgentSummaryNormalizer
{
    /// <summary>
    /// Collapses pathological single-token-per-line whitespace while preserving
    /// real Markdown structure. Returns the input unchanged when it is null or
    /// empty.
    /// </summary>
    /// <param name="summary">The raw sub-agent result summary.</param>
    /// <returns>The normalized summary.</returns>
    public static string? Normalize(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
            return summary;

        // Normalize all line endings to LF for uniform processing.
        var normalizedEndings = summary.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedEndings.Split('\n');

        var output = new List<string>();
        // Index into output of the paragraph line currently being accumulated,
        // or -1 when there is no open line a token fragment could join.
        var openLineIndex = -1;
        var inFencedCode = false;
        var sawBlankSinceOpen = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();

            // Fenced code block boundaries and their contents are emitted verbatim.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                output.Add(rawLine);
                inFencedCode = !inFencedCode;
                openLineIndex = -1;
                sawBlankSinceOpen = false;
                continue;
            }

            if (inFencedCode)
            {
                output.Add(rawLine);
                continue;
            }

            if (trimmed.Length == 0)
            {
                // Collapse runs of blank lines to a single paragraph break.
                if (output.Count > 0 && output[^1].Length != 0)
                    output.Add(string.Empty);

                openLineIndex = -1;
                sawBlankSinceOpen = true;
                continue;
            }

            var isTokenFragment = IsSingleToken(trimmed);

            if (openLineIndex >= 0 && !sawBlankSinceOpen && isTokenFragment)
            {
                // Join this token fragment onto the open line, reconstructing the
                // pathologically split heading/paragraph with a single space.
                output[openLineIndex] = output[openLineIndex] + " " + trimmed;
                continue;
            }

            output.Add(trimmed);
            openLineIndex = output.Count - 1;
            sawBlankSinceOpen = false;
        }

        // Drop a single trailing blank line introduced by a trailing newline.
        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);

        var builder = new StringBuilder(normalizedEndings.Length);
        for (var i = 0; i < output.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append(output[i]);
        }

        var result = builder.ToString();

        // Preserve a single trailing newline if the original ended with one.
        if (normalizedEndings.EndsWith('\n') && result.Length > 0)
            result += "\n";

        return result;
    }

    /// <summary>
    /// A "single token" line contains no interior whitespace. Real Markdown lines
    /// (headings, list items, table rows, prose) contain spaces, so they are never
    /// treated as pathological fragments and are left on their own line.
    /// </summary>
    private static bool IsSingleToken(string trimmed)
    {
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
                return false;
        }

        return true;
    }
}
