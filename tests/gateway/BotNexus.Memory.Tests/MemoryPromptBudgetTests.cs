using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Pins issue #2871: <see cref="AgentMemoryPromptRequest.MaxTokenBudget"/> was declared, documented
/// and read by nothing. These tests fix the enforcement contract so the cap cannot silently lapse
/// back into decoration.
/// </summary>
/// <remarks>
/// <para>
/// The non-vacuity anchor is deliberate. Asserting only "a budgeted result is returned" would have
/// passed against the pre-fix code, because the pre-fix code also returned notes - it just returned
/// all of them. Every assertion below is therefore keyed on a property that is <b>false</b> when
/// the budget is ignored: a shortened length, a present disclosure marker, or an omission count.
/// </para>
/// <para>
/// Budgets are expressed in tokens and converted at <see cref="MemoryPromptBudget.CharsPerToken"/>,
/// so the character arithmetic in each case is derived from the constant rather than hard-coded to
/// 4. If the estimator ratio ever changes, these tests move with it instead of silently pinning a
/// stale ratio.
/// </para>
/// </remarks>
public sealed class MemoryPromptBudgetTests
{
    private const int Cpt = MemoryPromptBudget.CharsPerToken;

    private static AgentMemoryDailyNote Note(string day, int length, char fill = 'a')
        => new(DateOnly.Parse(day), new string(fill, length));

    /// <summary>AC3 (under budget): a fitting input is returned completely unchanged.</summary>
    [Fact]
    public void Apply_UnderBudget_ReturnsContentUnchangedAndUndisclosed_Ac3()
    {
        var notes = new[] { Note("2026-08-13", 40), Note("2026-08-12", 40) };

        var result = MemoryPromptBudget.Apply(notes, maxTokenBudget: 100);

        result.WasTrimmed.ShouldBeFalse();
        result.Notes.Count.ShouldBe(2);
        result.Notes[0].Content.Length.ShouldBe(40);
        result.Notes[1].Content.Length.ShouldBe(40);
        result.Notes.ShouldAllBe(note => !note.Content.Contains(MemoryPromptBudget.DisclosureMarker));
        result.ApproximateTokenCount.ShouldBe(80 / Cpt);
    }

    /// <summary>
    /// AC3 (exactly at budget): the boundary is inclusive. An input whose length is exactly the cap
    /// must not be trimmed - an off-by-one here would trim every maximally-packed context and
    /// append a disclosure to content that was already legal.
    /// </summary>
    [Fact]
    public void Apply_ExactlyAtBudget_IsNotTrimmed_Ac3()
    {
        const int budget = 25;
        var notes = new[] { Note("2026-08-13", budget * Cpt) };

        var result = MemoryPromptBudget.Apply(notes, budget);

        result.WasTrimmed.ShouldBeFalse();
        result.Notes.Single().Content.Length.ShouldBe(budget * Cpt);
        result.Notes.Single().Content.ShouldNotContain(MemoryPromptBudget.DisclosureMarker);
        result.ApproximateTokenCount.ShouldBe(budget);
    }

    /// <summary>
    /// AC2 + AC3 (over budget): content is shortened AND the shortening is disclosed. This is the
    /// primary non-vacuity anchor for AC4 - it fails if <c>MemoryPromptBudget.Apply</c> stops being
    /// called or is reverted to a pass-through.
    /// </summary>
    [Fact]
    public void Apply_OverBudget_TrimsAndDiscloses_Ac2()
    {
        const int budget = 50;
        var notes = new[] { Note("2026-08-13", 400), Note("2026-08-12", 400) };

        var result = MemoryPromptBudget.Apply(notes, budget);

        result.WasTrimmed.ShouldBeTrue();

        var rendered = string.Concat(result.Notes.Select(note => note.Content));
        rendered.Contains(MemoryPromptBudget.DisclosureMarker, StringComparison.Ordinal)
            .ShouldBeTrue("trimming must never be silent");
        rendered.ShouldContain(budget.ToString());

        // The emitted total, disclosure included, stays inside the cap.
        rendered.Length.ShouldBeLessThanOrEqualTo(budget * Cpt);

        // And it is genuinely shorter than the input, not merely annotated.
        rendered.Length.ShouldBeLessThan(800);
    }

    /// <summary>
    /// AC2 (trimming order): newest-first notes are kept whole for as long as the budget allows and
    /// older notes are dropped - never the reverse. Asserting the retained fill character proves
    /// which note survived, which a length-only assertion could not.
    /// </summary>
    [Fact]
    public void Apply_OverBudget_KeepsNewestFirstAndOmitsOldest_Ac2()
    {
        var notes = new[]
        {
            Note("2026-08-13", 60, 'n'),
            Note("2026-08-12", 60, 'o'),
            Note("2026-08-11", 60, 'x')
        };

        var result = MemoryPromptBudget.Apply(notes, maxTokenBudget: 25);

        var rendered = string.Concat(result.Notes.Select(note => note.Content));
        rendered.ShouldContain("n");
        rendered.ShouldNotContain("x", Case.Sensitive);
        result.Notes[0].Date.ShouldBe(new DateOnly(2026, 8, 13));
        rendered.ShouldContain("older daily note(s) omitted");
    }

    /// <summary>
    /// Determinism: the same input and budget produce a byte-identical result across repeated
    /// invocations. AC2 requires deterministic trimming, and a determinism claim asserted in prose
    /// but not in a test is just a claim.
    /// </summary>
    [Fact]
    public void Apply_IsDeterministic_Ac2()
    {
        var notes = new[] { Note("2026-08-13", 500), Note("2026-08-12", 500) };

        var first = MemoryPromptBudget.Apply(notes, 30);
        var second = MemoryPromptBudget.Apply(notes, 30);

        string.Concat(first.Notes.Select(n => n.Content))
            .ShouldBe(string.Concat(second.Notes.Select(n => n.Content)));
    }

    /// <summary>
    /// Surrogate safety: the cut reuses the unified grapheme-safe policy (#2924/#2883), so an
    /// astral-plane character is never split into a lone surrogate. The budget is chosen to land
    /// the boundary mid-pair, which is exactly where a naive <c>[..n]</c> slice would fail.
    /// </summary>
    [Fact]
    public void Apply_OverBudget_NeverSplitsASurrogatePair()
    {
        // Each emoji is 2 UTF-16 code units, so an odd char budget forces a mid-pair boundary.
        var content = string.Concat(Enumerable.Repeat("\U0001F52C", 60));
        var notes = new[] { new AgentMemoryDailyNote(new DateOnly(2026, 8, 13), content) };

        var result = MemoryPromptBudget.Apply(notes, maxTokenBudget: 7);

        result.WasTrimmed.ShouldBeTrue();
        foreach (var note in result.Notes)
        {
            for (var i = 0; i < note.Content.Length; i++)
            {
                if (char.IsHighSurrogate(note.Content[i]))
                {
                    (i + 1 < note.Content.Length && char.IsLowSurrogate(note.Content[i + 1]))
                        .ShouldBeTrue("a high surrogate must be followed by its low surrogate");
                    i++;
                }
                else
                {
                    char.IsLowSurrogate(note.Content[i]).ShouldBeFalse("no orphaned low surrogate");
                }
            }
        }
    }

    /// <summary>
    /// A non-positive budget means explicitly unbounded, not "trim everything". Getting this
    /// backwards would empty every context whose caller passed 0.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Apply_NonPositiveBudget_IsUnbounded(int budget)
    {
        var notes = new[] { Note("2026-08-13", 10_000) };

        var result = MemoryPromptBudget.Apply(notes, budget);

        result.WasTrimmed.ShouldBeFalse();
        result.Notes.Single().Content.Length.ShouldBe(10_000);
    }

    /// <summary>
    /// <c>int.MaxValue</c> must not wrap when converted to a character budget. In int arithmetic
    /// <c>int.MaxValue * 4</c> is negative, which would trim everything at the widest possible cap.
    /// </summary>
    [Fact]
    public void Apply_MaxValueBudget_DoesNotOverflowIntoTrimming()
    {
        var notes = new[] { Note("2026-08-13", 5_000) };

        var result = MemoryPromptBudget.Apply(notes, int.MaxValue);

        result.WasTrimmed.ShouldBeFalse();
        result.Notes.Single().Content.Length.ShouldBe(5_000);
    }

    /// <summary>
    /// The most severe case must still speak. When the budget cannot fit any content at all, the
    /// disclosure itself is emitted rather than an empty context - otherwise the worst trimming
    /// outcome would be the only silent one.
    /// </summary>
    [Fact]
    public void Apply_BudgetTooSmallForAnyContent_StillDiscloses_Ac2()
    {
        var notes = new[] { Note("2026-08-13", 4_000), Note("2026-08-12", 4_000) };

        var result = MemoryPromptBudget.Apply(notes, maxTokenBudget: 1);

        result.WasTrimmed.ShouldBeTrue();
        result.Notes.ShouldNotBeEmpty();
        string.Concat(result.Notes.Select(n => n.Content))
            .ShouldContain(MemoryPromptBudget.DisclosureMarker);
    }

    /// <summary>Empty input is not a trimming event.</summary>
    [Fact]
    public void Apply_NoNotes_ReturnsEmptyAndUntrimmed()
    {
        var result = MemoryPromptBudget.Apply([], maxTokenBudget: 4000);

        result.WasTrimmed.ShouldBeFalse();
        result.Notes.ShouldBeEmpty();
        result.ApproximateTokenCount.ShouldBe(0);
    }
}
