using BotNexus.Domain.Text;
using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Memory;

/// <summary>
/// Applies <see cref="AgentMemoryPromptRequest.MaxTokenBudget"/> to assembled memory content.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the budget was declared on the contract and read by nobody (#2871). A cap
/// that is advertised but not applied is worse than no cap: it invites a caller to rely on a
/// bound that does not exist, and the failure mode - an over-large system prompt - surfaces far
/// from memory as a context-window error with no diagnostic pointing back here.
/// </para>
/// <para>
/// <b>Trimming order.</b> Daily notes arrive newest-first and are consumed in that order, so the
/// most recent context is retained whole for as long as the budget allows. The single note that
/// straddles the boundary is truncated; every older note past the boundary is omitted entirely.
/// Nothing is reordered, re-ranked or summarised - the transform is a pure prefix-keep, which is
/// what makes it reproducible for a given input and budget.
/// </para>
/// <para>
/// <b>Never silent.</b> Whenever any content is dropped or shortened, a disclosure line is
/// appended to the returned context naming the budget and the number of omitted notes. The
/// disclosure's own length is reserved out of the budget before trimming, so the disclosed result
/// still fits the cap rather than overshooting it by the width of its own confession.
/// </para>
/// <para>
/// <b>Surrogate safety.</b> The cut delegates to <see cref="TextTruncation.SafeTruncate"/>, the
/// unified grapheme-safe boundary policy (#2924/#2883), rather than slicing with <c>[..n]</c>.
/// Memory content is markdown written by agents and users and routinely contains astral-plane
/// characters; a naive slice would persist a lone surrogate into the prompt.
/// </para>
/// </remarks>
public static class MemoryPromptBudget
{
    /// <summary>
    /// Characters per token in the rough estimate used throughout memory assembly. This mirrors
    /// the estimator that already produced <see cref="AgentMemoryContext.ApproximateTokenCount"/>,
    /// so the budget is enforced in the same units the context reports.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Stable leading text of the disclosure line. Tests and callers match on this rather than on
    /// the full formatted sentence, so wording can be improved without breaking detection.
    /// </summary>
    public const string DisclosureMarker = "> [memory trimmed to fit the";

    /// <summary>
    /// Applies <paramref name="maxTokenBudget"/> to <paramref name="notes"/> (newest first).
    /// </summary>
    /// <param name="notes">Daily notes ordered newest first.</param>
    /// <param name="maxTokenBudget">
    /// The approximate token cap. Values of zero or below mean <b>explicitly unbounded</b> and are
    /// documented as such on the contract, so an unbounded request is a deliberate caller choice
    /// rather than an ignored parameter.
    /// </param>
    /// <returns>The budgeted notes, their approximate token count, and whether trimming occurred.</returns>
    public static BudgetedMemoryContent Apply(IReadOnlyList<AgentMemoryDailyNote> notes, int maxTokenBudget)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var totalChars = notes.Sum(static note => note.Content?.Length ?? 0);

        // long arithmetic: a caller may legitimately pass int.MaxValue to mean "no practical cap",
        // and multiplying that by CharsPerToken in int would wrap negative and trim everything.
        var charBudget = (long)maxTokenBudget * CharsPerToken;

        if (maxTokenBudget <= 0 || totalChars <= charBudget)
        {
            return new BudgetedMemoryContent(notes, totalChars / CharsPerToken, WasTrimmed: false);
        }

        // Reserve the widest disclosure this input could produce before spending any budget on
        // content, so the emitted result including the disclosure is within the cap.
        var reservation = BuildDisclosure(maxTokenBudget, notes.Count).Length;
        var remaining = Math.Max(0, charBudget - reservation);

        var kept = new List<AgentMemoryDailyNote>(notes.Count);
        var omitted = 0;

        foreach (var note in notes)
        {
            var content = note.Content ?? string.Empty;

            if (remaining <= 0)
            {
                omitted++;
                continue;
            }

            if (content.Length <= remaining)
            {
                kept.Add(note);
                remaining -= content.Length;
                continue;
            }

            var truncated = TextTruncation.SafeTruncate(content, (int)remaining, suffix: string.Empty) ?? string.Empty;
            remaining = 0;

            if (truncated.Length == 0)
            {
                // The boundary fell inside the first grapheme cluster; emitting an empty note
                // would be indistinguishable from having no note, so it counts as omitted.
                omitted++;
                continue;
            }

            kept.Add(note with { Content = truncated });
        }

        var disclosure = BuildDisclosure(maxTokenBudget, omitted);

        if (kept.Count == 0)
        {
            // Everything was dropped. The disclosure still has to reach the prompt, otherwise the
            // most severe trimming case would be the one that says nothing at all.
            kept.Add(new AgentMemoryDailyNote(notes[0].Date, disclosure.TrimStart('\n')));
        }
        else
        {
            var last = kept[^1];
            kept[^1] = last with { Content = last.Content + disclosure };
        }

        var keptChars = kept.Sum(static note => note.Content.Length);
        return new BudgetedMemoryContent(kept, keptChars / CharsPerToken, WasTrimmed: true);
    }

    /// <summary>
    /// Builds the disclosure. Uses literal <c>\n</c> rather than <see cref="Environment.NewLine"/>
    /// so the output is byte-identical on Windows and Linux - determinism is an acceptance
    /// criterion, and a platform-dependent separator would make the trimmed length differ between
    /// the developer machine and the remote test container.
    /// </summary>
    private static string BuildDisclosure(int maxTokenBudget, int omittedNotes)
        => omittedNotes > 0
            ? $"\n\n{DisclosureMarker} {maxTokenBudget}-token memory budget; {omittedNotes} older daily note(s) omitted]"
            : $"\n\n{DisclosureMarker} {maxTokenBudget}-token memory budget]";
}

/// <summary>
/// The outcome of applying a memory prompt budget.
/// </summary>
/// <param name="Notes">The retained notes, newest first, with any disclosure already appended.</param>
/// <param name="ApproximateTokenCount">Token estimate of what is actually returned, not of the input.</param>
/// <param name="WasTrimmed">True when content was shortened or omitted.</param>
public sealed record BudgetedMemoryContent(
    IReadOnlyList<AgentMemoryDailyNote> Notes,
    int ApproximateTokenCount,
    bool WasTrimmed);
