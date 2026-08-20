using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;

namespace BotNexus.Memory;

/// <summary>
/// The read-boundary gate for always-injected memory context: excludes non-first-party notes from
/// content that reaches the system prompt on every turn, and discloses that it did so (#3232 AC5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why injection is a filter where ranking is a weighting.</b> A search result is <i>pulled</i>
/// by an explicit query and rendered with its provenance line, so the model can see what it is
/// looking at and weigh it. Always-on context is <i>pushed</i> with no query, no ranking and no
/// opportunity to decline; it is indistinguishable from the agent's own standing instructions.
/// That is exactly the position an attacker wants their text to occupy, so this boundary excludes
/// rather than down-weights. The same content stays fully reachable through <c>memory_search</c>.
/// </para>
/// <para>
/// <b>Exclusion is disclosed, never silent.</b> An agent whose quarantined note vanished without
/// trace would conclude the note was never written and would keep re-deriving it. The disclosure
/// names the count and the reason and points at the tool that can still retrieve the content, so
/// the omission is recoverable by the agent itself rather than only by an operator reading logs.
/// </para>
/// <para>
/// <b>Capability is a second, orthogonal axis (#3468).</b> Provenance answers "may this content be
/// pushed"; the turn's tool policy answers "is this agent configured to have memory at all". An
/// agent deliberately spawned without memory tools - an archetype-restricted sub-agent, say - was
/// scoped that way on purpose, so injecting its daily notes anyway is a scoping leak across the
/// agent boundary, not a mere inefficiency. The same axis governs the disclosure wording: telling
/// an agent to "retrieve them with memory_search" when that tool is not registered induces a
/// guaranteed-failing call and a <c>Tool 'memory_search' is not registered</c> error that
/// misdirects diagnosis.
/// </para>
/// </remarks>
public static class MemoryInjectionGate
{
    /// <summary>
    /// Stable leading text of the exclusion disclosure. Tests and callers match on this prefix
    /// rather than the full sentence so the wording can be improved without breaking detection.
    /// </summary>
    public const string DisclosureMarker = "> [memory injection: ";

    /// <summary>
    /// Filters <paramref name="notes"/> down to the entries eligible for always-on injection.
    /// </summary>
    /// <param name="notes">The candidate daily notes, newest first.</param>
    /// <param name="memoryToolsAvailable">
    /// The turn's resolved memory-capability signal (#3468). <see langword="false"/> means the
    /// agent's effective tool set grants it no memory tools for this turn, in which case nothing
    /// is injected at all. Defaults to <see langword="true"/> so a caller that has not yet
    /// resolved the signal keeps the pre-#3468 provenance-only behaviour exactly.
    /// </param>
    /// <param name="memorySearchAvailable">
    /// Whether <c>memory_search</c> specifically is callable this turn. Only affects the wording
    /// of the exclusion disclosure: when <see langword="false"/> the disclosure states that the
    /// withheld notes are unreachable rather than naming a tool the agent cannot call.
    /// </param>
    /// <returns>
    /// The retained notes, with a disclosure appended to the newest retained note when anything
    /// was excluded, plus the excluded count for the caller's logging.
    /// </returns>
    public static (IReadOnlyList<AgentMemoryDailyNote> Notes, int ExcludedCount) Apply(
        IReadOnlyList<AgentMemoryDailyNote> notes,
        bool memoryToolsAvailable = true,
        bool memorySearchAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(notes);

        // Capability gate BEFORE provenance. An agent with no memory tools gets no content and no
        // disclosure: a disclosure is a recovery affordance, and there is nothing here for this
        // agent to recover with. Reporting zero excluded is honest at this seam -- ExcludedCount
        // means "withheld by the trust boundary", and the caller logs the capability decision
        // separately rather than conflating a scoping decision with a trust one.
        if (!memoryToolsAvailable)
            return ([], 0);

        var kept = new List<AgentMemoryDailyNote>(notes.Count);
        var excluded = 0;

        foreach (var note in notes)
        {
            // Content is consulted as well as the declared provenance: a markdown note carries the
            // #2519 quarantine marker inside its text, and that marker must not be outvotable by a
            // provenance value assigned somewhere further up the pipeline.
            if (MemoryTrust.IsFirstParty(MemoryTrust.DeriveFromContent(note.Provenance, note.Content)))
                kept.Add(note);
            else
                excluded++;
        }

        if (excluded == 0)
            return (notes, 0);

        var disclosure = BuildDisclosure(excluded, memorySearchAvailable);

        if (kept.Count == 0)
        {
            // Everything was excluded. The disclosure still has to reach the prompt, or the most
            // severe case would be the one that says nothing at all.
            return ([new AgentMemoryDailyNote(notes[0].Date, disclosure.TrimStart('\n'), MemoryProvenance.Agent)], excluded);
        }

        kept[0] = kept[0] with { Content = kept[0].Content + disclosure };
        return (kept, excluded);
    }

    /// <summary>
    /// Builds the disclosure with literal <c>\n</c> rather than <see cref="Environment.NewLine"/>,
    /// so the emitted context is byte-identical on Windows and Linux and the remote test container
    /// sees exactly what a developer machine does.
    /// </summary>
    private static string BuildDisclosure(int excludedCount, bool memorySearchAvailable)
        => $"\n\n{DisclosureMarker}{excludedCount} note(s) withheld from always-on context because their "
           + "provenance is not first-party; "
           + (memorySearchAvailable
               ? "retrieve them explicitly with memory_search if needed]"
               : "they are not retrievable in this turn because no memory retrieval tool is available to you]");
}
