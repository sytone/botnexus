using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The SINGLE home of the todo status vocabulary table (#3455, AC3). <c>TodoPanel</c>'s own suite
/// keeps only rendering assertions — it deliberately does not carry a second copy of this table.
/// </summary>
public sealed class TodoItemStatusProjectionTests
{
    [Theory]
    [InlineData("pending", TodoItemStatus.Pending)]
    [InlineData("in_progress", TodoItemStatus.InProgress)]
    [InlineData("done", TodoItemStatus.Done)]
    [InlineData("cancelled", TodoItemStatus.Cancelled)]
    public void Parse_maps_every_wire_value(string wire, TodoItemStatus expected)
        => TodoItemStatusProjection.Parse(wire).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("blocked")]              // a plausible FUTURE server-side value
    [InlineData("in-progress")]          // near-miss spelling
    [InlineData("\u0001garbage\u0002")]  // outright junk
    public void Parse_is_total_and_falls_back_to_pending(string? value)
        => TodoItemStatusProjection.Parse(value).ShouldBe(TodoItemStatus.Pending);

    /// <summary>
    /// #3455: the one INTENTIONAL behavioural delta. The pre-extraction comparisons in
    /// <c>TodoPanel.razor</c> were ordinal and case-sensitive, silently relying on the gateway
    /// normalising case first. That invariant is now stated, not assumed — asserted here rather
    /// than acquired silently.
    /// </summary>
    [Theory]
    [InlineData("DONE", TodoItemStatus.Done)]
    [InlineData("Done", TodoItemStatus.Done)]
    [InlineData("In_Progress", TodoItemStatus.InProgress)]
    [InlineData("IN_PROGRESS", TodoItemStatus.InProgress)]
    [InlineData("Cancelled", TodoItemStatus.Cancelled)]
    [InlineData("PENDING", TodoItemStatus.Pending)]
    [InlineData("  Done  ", TodoItemStatus.Done)]
    public void Parse_is_case_insensitive_and_trims(string wire, TodoItemStatus expected)
        => TodoItemStatusProjection.Parse(wire).ShouldBe(expected);

    /// <summary>
    /// Pending must be the enum's zero value so a default-initialised status agrees with the
    /// server's own <c>_ =&gt; "pending"</c> fallback in <c>TodoTool</c>.
    /// </summary>
    [Fact]
    public void Pending_is_the_default_enum_value()
        => default(TodoItemStatus).ShouldBe(TodoItemStatus.Pending);

    [Theory]
    [InlineData(TodoItemStatus.Pending, "pending")]
    [InlineData(TodoItemStatus.InProgress, "in_progress")]
    [InlineData(TodoItemStatus.Done, "done")]
    [InlineData(TodoItemStatus.Cancelled, "cancelled")]
    public void Wire_round_trips_through_Parse(TodoItemStatus status, string expectedWire)
    {
        TodoItemStatusProjection.Wire(status).ShouldBe(expectedWire);
        TodoItemStatusProjection.Parse(expectedWire).ShouldBe(status);
    }

    [Theory]
    [InlineData(TodoItemStatus.Pending, "\u2610")]
    [InlineData(TodoItemStatus.InProgress, "\u25D0")]
    [InlineData(TodoItemStatus.Done, "\u2611")]
    [InlineData(TodoItemStatus.Cancelled, "\u2612")]
    public void Glyph_table_is_pinned(TodoItemStatus status, string expected)
        => TodoItemStatusProjection.Glyph(status).ShouldBe(expected);

    [Theory]
    [InlineData(TodoItemStatus.Pending, "Pending")]
    [InlineData(TodoItemStatus.InProgress, "In progress")]
    [InlineData(TodoItemStatus.Done, "Done")]
    [InlineData(TodoItemStatus.Cancelled, "Cancelled")]
    public void Label_table_is_pinned(TodoItemStatus status, string expected)
        => TodoItemStatusProjection.Label(status).ShouldBe(expected);

    /// <summary>
    /// AC3: an unrecognised wire value must reach the Pending glyph and label, not an empty
    /// string — the panel renders something sane for a status this client build has never seen.
    /// </summary>
    [Fact]
    public void Unrecognised_value_projects_the_pending_glyph_and_label()
    {
        var parsed = TodoItemStatusProjection.Parse("deferred");
        TodoItemStatusProjection.Glyph(parsed).ShouldBe("\u2610");
        TodoItemStatusProjection.Label(parsed).ShouldBe("Pending");
        TodoItemStatusProjection.Wire(parsed).ShouldBe("pending");
    }

    /// <summary>
    /// Non-vacuity guard: every declared enum member must have a distinct wire value, glyph and
    /// label. Adding a member without extending the tables fails here rather than silently
    /// rendering as Pending.
    /// </summary>
    [Fact]
    public void Every_declared_status_has_a_distinct_projection()
    {
        var all = Enum.GetValues<TodoItemStatus>();
        all.Length.ShouldBe(4);
        all.Select(TodoItemStatusProjection.Wire).Distinct().Count().ShouldBe(all.Length);
        all.Select(TodoItemStatusProjection.Glyph).Distinct().Count().ShouldBe(all.Length);
        all.Select(TodoItemStatusProjection.Label).Distinct().Count().ShouldBe(all.Length);
    }
}
