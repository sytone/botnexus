using System.Text.Json;

namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Renders a conversation's persisted todo state (the <c>TodoJson</c> payload written by the
/// <c>todo</c> tool, #1466) as a compact checklist for the system prompt (#1464 step 3).
/// </summary>
/// <remarks>
/// Re-injecting the todo list verbatim every turn makes the plan a durable spine that summarization
/// (compaction) cannot blur, and turns the model's job each turn into "advance ONE item from
/// <c>[ ]</c> to <c>[x]</c>". The checklist boxes mirror the tool's own status vocabulary:
/// <c>[ ]</c> pending, <c>[~]</c> in_progress, <c>[x]</c> done, <c>[-]</c> cancelled.
/// </remarks>
public static class TodoPromptFormatter
{
    /// <summary>Heading rendered above the checklist.</summary>
    public const string SectionHeading = "## Conversation Todo";

    /// <summary>Heading rendered above items carried over from earlier runs (#2984).</summary>
    public const string PriorRunHeading = "## Previously reported (earlier runs)";

    /// <summary>
    /// Renders the persisted <paramref name="todoJson"/> as checklist lines (heading + one line per item),
    /// or an empty list when there are no items / the payload is null or malformed.
    /// </summary>
    /// <param name="todoJson">The raw <c>TodoJson</c> payload from the conversation, or <c>null</c>.</param>
    /// <returns>Prompt lines for the todo section, or an empty list when nothing should be rendered.</returns>
    public static IReadOnlyList<string> BuildSection(string? todoJson)
        => BuildSection(todoJson, runStartedAt: null);

    /// <summary>
    /// Renders the checklist for a recurring run, separating <em>agenda</em> from <em>minutes</em> (#2984).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recurring cron conversation is a standup: the same participant meeting on a schedule about a
    /// continuous body of work - what is done, any issues, what is next. Continuity is the point, so the
    /// checklist deliberately survives session continuation and must NOT be reset per run.
    /// </para>
    /// <para>
    /// What broke (2026-08-11) is narrower: items completed by a PREVIOUS run are that meeting's
    /// <em>minutes</em>, yet they were re-injected verbatim into the forward-looking <em>agenda</em> as live
    /// <c>[x]</c> entries. A run then read "everything is done, one item left", made zero tool calls, and
    /// reported the previous run's outcomes as its own. Given what it was shown that was the correct read -
    /// the prompt was the defect, not the model.
    /// </para>
    /// <para>
    /// So terminal items (<c>done</c>/<c>cancelled</c>) last touched before <paramref name="runStartedAt"/>
    /// are demoted out of the agenda and restated as prior-run context. Open items
    /// (<c>pending</c>/<c>in_progress</c>) ALWAYS carry forward untouched - they are precisely "what is
    /// next", and a standup that forgot them would be useless.
    /// </para>
    /// </remarks>
    /// <param name="todoJson">The raw <c>TodoJson</c> payload from the conversation, or <c>null</c>.</param>
    /// <param name="runStartedAt">
    /// Start of the current run. Items in a terminal status whose <c>updatedAt</c> precedes this instant were
    /// completed by an earlier run. <c>null</c> (every non-recurring caller) preserves the original rendering
    /// exactly, so this overload cannot change interactive behaviour.
    /// </param>
    /// <returns>Prompt lines for the todo section, or an empty list when nothing should be rendered.</returns>
    public static IReadOnlyList<string> BuildSection(string? todoJson, DateTimeOffset? runStartedAt)
    {
        var items = ParseItems(todoJson);
        if (items.Count == 0)
            return [];

        // No run boundary supplied => the caller is not a recurring run; render exactly as before.
        IReadOnlyList<TodoEntry> agenda = items;
        IReadOnlyList<TodoEntry> minutes = [];
        if (runStartedAt is { } boundary)
        {
            agenda = items.Where(item => !IsPriorRunMinute(item, boundary)).ToList();
            minutes = items.Where(item => IsPriorRunMinute(item, boundary)).ToList();
        }

        var lines = new List<string>(items.Count + 4);

        if (agenda.Count > 0)
        {
            lines.Add(SectionHeading);
            lines.Add("Advance ONE item per turn; only a tool result this turn may flip an item to [x] done -- narration cannot.");
            lines.AddRange(agenda.Select(static item => $"- {Box(item.Status)} {item.Text}"));
        }

        if (minutes.Count > 0)
        {
            // Deliberately NOT checklist boxes. An [x] in the agenda reads as "this run did it"; these
            // lines must read as "an earlier run reported it", which is a different claim entirely.
            lines.Add(PriorRunHeading);
            lines.Add("Reported by EARLIER runs, not by this one. Context only -- you have done none of it this run, so it is not evidence for any claim you make now.");
            lines.AddRange(minutes.Select(static item => $"- {item.Text}"));
        }

        return lines;
    }

    /// <summary>
    /// True when an item is an earlier run's minute: terminal status AND last updated before this run began.
    /// An item with no usable <c>updatedAt</c> is treated as current, so a malformed or legacy payload keeps
    /// its existing rendering rather than being silently hidden.
    /// </summary>
    private static bool IsPriorRunMinute(TodoEntry item, DateTimeOffset runStartedAt)
        => IsTerminal(item.Status) && item.UpdatedAt is { } updated && updated < runStartedAt;

    /// <summary>Terminal statuses: the item is finished and cannot be advanced further.</summary>
    private static bool IsTerminal(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "done" or "cancelled" => true,
        _ => false,
    };

    /// <summary>Maps a status string to its checklist box, defaulting to pending for unknown values.</summary>
    private static string Box(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "done" => "[x]",
        "in_progress" => "[~]",
        "cancelled" => "[-]",
        _ => "[ ]",
    };

    /// <summary>
    /// Parses the items out of the persisted payload. Tolerates a null/blank/malformed payload by
    /// returning an empty list -- the prompt is built on a hot path and must never throw on bad state.
    /// </summary>
    private static IReadOnlyList<TodoEntry> ParseItems(string? todoJson)
    {
        if (string.IsNullOrWhiteSpace(todoJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(todoJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("items", out var itemsEl)
                || itemsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var entries = new List<TodoEntry>(itemsEl.GetArrayLength());
            foreach (var item in itemsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var text = item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var status = item.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                    ? statusEl.GetString()
                    : null;

                // #2984: updatedAt places the item relative to the current run boundary. Absent or
                // unparseable => null => treated as current (see IsPriorRunMinute).
                var updatedAt = item.TryGetProperty("updatedAt", out var updatedEl)
                    && updatedEl.ValueKind == JsonValueKind.String
                    && updatedEl.TryGetDateTimeOffset(out var parsedUpdated)
                        ? parsedUpdated
                        : (DateTimeOffset?)null;

                entries.Add(new TodoEntry(text!, status, updatedAt));
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private readonly record struct TodoEntry(string Text, string? Status, DateTimeOffset? UpdatedAt);
}
