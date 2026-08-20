namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// The canonical client-side vocabulary for a todo item's status (#3455).
/// </summary>
/// <remarks>
/// <para>
/// The wire values are declared server-side by <c>BotNexus.Gateway.Tools.TodoTool</c> as the JSON
/// schema enum <c>["pending", "in_progress", "done", "cancelled"]</c>. Before #3455 the portal
/// re-derived that vocabulary inline in <c>TodoPanel.razor</c> across four separate switch blocks,
/// so the contract existed in two unrelated places with no compile-time link.
/// </para>
/// <para>
/// <see cref="Pending"/> is deliberately FIRST so the enum's <c>default</c> value matches the
/// server's own <c>_ =&gt; "pending"</c> fallback in <c>TodoTool</c>. A zero-initialised
/// <see cref="TodoItemStatus"/> and an unparseable wire value therefore agree.
/// </para>
/// </remarks>
public enum TodoItemStatus
{
    /// <summary>Wire value <c>pending</c>. Also the unknown/empty/null fallback.</summary>
    Pending,

    /// <summary>Wire value <c>in_progress</c>.</summary>
    InProgress,

    /// <summary>Wire value <c>done</c>.</summary>
    Done,

    /// <summary>Wire value <c>cancelled</c>.</summary>
    Cancelled,
}

/// <summary>
/// Parses and projects <see cref="TodoItemStatus"/> for display, owning the whole todo status
/// vocabulary in one place so no view re-derives it (#3455, epic #2452).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why parsing is tolerant.</b> Same reason <see cref="ConversationOrigin"/> documents: a
/// <em>deployed client can be older than the gateway it talks to</em>. The portal is a separately
/// downloaded deployment unit, so a gateway that starts emitting a new status value would reach a
/// client build that has never heard of it. Parsing is therefore TOTAL — unknown, empty and null
/// values degrade to <see cref="TodoItemStatus.Pending"/> rather than throwing, so a server-side
/// vocabulary addition renders conservatively instead of breaking the panel.
/// </para>
/// <para>
/// <b>Case-insensitivity is intentional (#3455).</b> The pre-extraction comparisons in
/// <c>TodoPanel.razor</c> were ordinal and case-sensitive, silently relying on
/// <c>TodoTool</c> normalising case before persisting. That invariant is now stated rather than
/// assumed: <see cref="Parse"/> trims and lower-cases, so <c>"DONE"</c> and <c>"Done"</c> parse
/// like <c>"done"</c>. Asserted explicitly in the test suite.
/// </para>
/// </remarks>
public static class TodoItemStatusProjection
{
    /// <summary>
    /// Parses a raw wire status. Total and case-insensitive: unknown, empty, whitespace and null
    /// values return <see cref="TodoItemStatus.Pending"/>, matching the server's default-value
    /// contract exactly.
    /// </summary>
    /// <param name="value">The raw wire value, e.g. <c>"in_progress"</c>. Case-insensitive.</param>
    /// <returns>The parsed status, or <see cref="TodoItemStatus.Pending"/> when unrecognised.</returns>
    public static TodoItemStatus Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "in_progress" => TodoItemStatus.InProgress,
            "done" => TodoItemStatus.Done,
            "cancelled" => TodoItemStatus.Cancelled,
            _ => TodoItemStatus.Pending,
        };

    /// <summary>
    /// The canonical lower-case wire value for a status. Used for CSS class suffixes and the
    /// <c>data-status</c> attribute so the rendered markup keeps speaking the server's vocabulary.
    /// </summary>
    public static string Wire(TodoItemStatus status) => status switch
    {
        TodoItemStatus.InProgress => "in_progress",
        TodoItemStatus.Done => "done",
        TodoItemStatus.Cancelled => "cancelled",
        _ => "pending",
    };

    /// <summary>The single-character box glyph rendered beside the item text.</summary>
    public static string Glyph(TodoItemStatus status) => status switch
    {
        TodoItemStatus.Done => "\u2611",       // ballot box with check
        TodoItemStatus.InProgress => "\u25D0", // half-filled circle
        TodoItemStatus.Cancelled => "\u2612",  // ballot box with X
        _ => "\u2610",                         // empty ballot box
    };

    /// <summary>The human-readable label rendered as the item's status badge.</summary>
    public static string Label(TodoItemStatus status) => status switch
    {
        TodoItemStatus.InProgress => "In progress",
        TodoItemStatus.Done => "Done",
        TodoItemStatus.Cancelled => "Cancelled",
        _ => "Pending",
    };
}
