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

    /// <summary>
    /// Renders the persisted <paramref name="todoJson"/> as checklist lines (heading + one line per item),
    /// or an empty list when there are no items / the payload is null or malformed.
    /// </summary>
    /// <param name="todoJson">The raw <c>TodoJson</c> payload from the conversation, or <c>null</c>.</param>
    /// <returns>Prompt lines for the todo section, or an empty list when nothing should be rendered.</returns>
    public static IReadOnlyList<string> BuildSection(string? todoJson)
    {
        var items = ParseItems(todoJson);
        if (items.Count == 0)
            return [];

        var lines = new List<string>(items.Count + 2)
        {
            SectionHeading,
            "Advance ONE item per turn; only a tool result this turn may flip an item to [x] done -- narration cannot.",
        };
        lines.AddRange(items.Select(static item => $"- {Box(item.Status)} {item.Text}"));
        return lines;
    }

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

                entries.Add(new TodoEntry(text!, status));
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private readonly record struct TodoEntry(string Text, string? Status);
}
