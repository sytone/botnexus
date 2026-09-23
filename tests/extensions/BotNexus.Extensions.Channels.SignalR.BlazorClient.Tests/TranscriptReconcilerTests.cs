using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3846: the pure reconciliation half of the refresh transcript repair. The reconciler merges a
/// freshly fetched server page into the locally displayed timeline. It is deliberately
/// insert-only: a refresh must never delete a row the client already has, because older pages
/// paged in by scroll-up are outside the server page the refresh fetched.
/// </summary>
public sealed class TranscriptReconcilerTests
{
    private static ChatMessage Msg(string role, string content, int minute) =>
        new(role, content, new DateTimeOffset(2026, 9, 4, 10, minute, 0, TimeSpan.Zero));

    /// <summary>
    /// Clause 2: reconciling a complete transcript is a no-op - identical count, identical
    /// ordering, no duplicates. This is the case that runs on every refresh of a healthy
    /// conversation, so a false insert here would corrupt every transcript in the product.
    /// </summary>
    [Fact]
    public void Reconcile_CompleteTranscript_IsIdempotent()
    {
        var local = new List<ChatMessage> { Msg("user", "one", 1), Msg("assistant", "two", 2), Msg("user", "three", 3) };
        var server = new List<ChatMessage> { Msg("user", "one", 1), Msg("assistant", "two", 2), Msg("user", "three", 3) };

        var result = TranscriptReconciler.Reconcile(local, server);

        result.Count.ShouldBe(3);
        result.Select(m => m.Content).ShouldBe(["one", "two", "three"]);

        // Running it a second time over its own output must also change nothing.
        var again = TranscriptReconciler.Reconcile(result, server);
        again.Select(m => m.Content).ShouldBe(["one", "two", "three"]);
    }

    /// <summary>
    /// Clause 3: a message the server has but the client lost mid-transcript is restored at its
    /// CHRONOLOGICAL position, not appended to the end. Appending would technically restore the
    /// content while rendering the conversation nonsensical.
    /// </summary>
    [Fact]
    public void Reconcile_MessageMissingFromTheMiddle_IsInsertedInChronologicalPosition()
    {
        var local = new List<ChatMessage> { Msg("user", "one", 1), Msg("user", "three", 3) };
        var server = new List<ChatMessage> { Msg("user", "one", 1), Msg("assistant", "two", 2), Msg("user", "three", 3) };

        var result = TranscriptReconciler.Reconcile(local, server);

        result.Select(m => m.Content).ShouldBe(["one", "two", "three"]);
    }

    /// <summary>
    /// A message dropped from the tail is appended, and one dropped from the head is prepended.
    /// </summary>
    [Fact]
    public void Reconcile_RestoresMissingHeadAndTailRows()
    {
        var local = new List<ChatMessage> { Msg("assistant", "two", 2) };
        var server = new List<ChatMessage> { Msg("user", "one", 1), Msg("assistant", "two", 2), Msg("user", "three", 3) };

        var result = TranscriptReconciler.Reconcile(local, server);

        result.Select(m => m.Content).ShouldBe(["one", "two", "three"]);
    }

    /// <summary>
    /// Insert-only: rows the client holds from an older page that the server's most-recent page
    /// does not contain must survive the reconcile untouched.
    /// </summary>
    [Fact]
    public void Reconcile_NeverDropsLocalRowsAbsentFromTheServerPage()
    {
        var local = new List<ChatMessage> { Msg("user", "older-page", 0), Msg("user", "one", 1) };
        var server = new List<ChatMessage> { Msg("user", "one", 1), Msg("assistant", "two", 2) };

        var result = TranscriptReconciler.Reconcile(local, server);

        result.Select(m => m.Content).ShouldBe(["older-page", "one", "two"]);
    }

    /// <summary>
    /// Two genuinely distinct rows that share a timestamp and role (a burst) must both survive,
    /// and must not be collapsed into one by the identity key.
    /// </summary>
    [Fact]
    public void Reconcile_KeepsDistinctRowsSharingATimestamp()
    {
        var local = new List<ChatMessage> { Msg("assistant", "a", 5) };
        var server = new List<ChatMessage> { Msg("assistant", "a", 5), Msg("assistant", "b", 5) };

        var result = TranscriptReconciler.Reconcile(local, server);

        result.Count.ShouldBe(2);
        result.Select(m => m.Content).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Reconcile_MissingEqualTimestampPredecessor_PreservesServerOrder()
    {
        var at = DateTimeOffset.Parse("2026-09-04T10:05:00Z");
        var first = new ChatMessage("user", "first", at) { ServerEntryId = "entry-1" };
        var second = new ChatMessage("assistant", "second", at) { ServerEntryId = "entry-2" };

        var result = TranscriptReconciler.Reconcile([second], [first, second]);

        result.ShouldBe([first, second]);
    }

    [Fact]
    public void Reconcile_MissingEqualTimestampMiddle_PreservesServerOrderAndPagedLocalRows()
    {
        var at = DateTimeOffset.Parse("2026-09-04T10:05:00Z");
        var older = Msg("user", "older-page", 4);
        var first = new ChatMessage("user", "first", at) { ServerEntryId = "entry-1" };
        var middle = new ChatMessage("assistant", "middle", at) { ServerEntryId = "entry-2" };
        var last = new ChatMessage("user", "last", at) { ServerEntryId = "entry-3" };

        var result = TranscriptReconciler.Reconcile([older, first, last], [first, middle, last]);

        result.ShouldBe([older, first, middle, last]);
        TranscriptReconciler.Reconcile(result, [first, middle, last]).ShouldBe(result);
    }

    [Fact]
    public void Reconcile_MultipleEqualTimestampHoles_PreservesServerOrder()
    {
        var at = DateTimeOffset.Parse("2026-09-04T10:05:00Z");
        var first = new ChatMessage("user", "first", at) { ServerEntryId = "entry-1" };
        var second = new ChatMessage("assistant", "second", at) { ServerEntryId = "entry-2" };
        var third = new ChatMessage("assistant", "third", at) { ServerEntryId = "entry-3" };
        var last = new ChatMessage("user", "last", at) { ServerEntryId = "entry-4" };

        var result = TranscriptReconciler.Reconcile([last], [first, second, third, last]);

        result.ShouldBe([first, second, third, last]);
        result.ShouldBeUnique();
        TranscriptReconciler.Reconcile(result, [first, second, third, last]).ShouldBe(result);
    }

    /// <summary>
    /// An empty local timeline is fully seeded from the server page, in order.
    /// </summary>
    [Fact]
    public void Reconcile_EmptyLocal_TakesTheWholeServerPage()
    {
        var server = new List<ChatMessage> { Msg("user", "one", 1), Msg("assistant", "two", 2) };

        var result = TranscriptReconciler.Reconcile([], server);

        result.Select(m => m.Content).ShouldBe(["one", "two"]);
    }

    /// <summary>
    /// An empty server page leaves the local timeline exactly as it was - the sad path where the
    /// server returns nothing must not blank the user's screen.
    /// </summary>
    [Fact]
    public void Reconcile_EmptyServerPage_LeavesLocalUntouched()
    {
        var local = new List<ChatMessage> { Msg("user", "one", 1) };

        var result = TranscriptReconciler.Reconcile(local, []);

        result.Select(m => m.Content).ShouldBe(["one"]);
    }

    /// <summary>
    /// Tool rows are keyed by their tool-call id, so the same tool call arriving from REST does
    /// not duplicate the row already rendered from the live SignalR ToolStart/ToolEnd pair. A
    /// terminal REST row repairs only an incomplete local ToolStart placeholder; a locally
    /// completed ToolEnd row remains newer live state and therefore wins over a stale page.
    /// </summary>
    [Fact]
    public void Reconcile_TerminalServerToolRepairsIncompleteLocalRowWithoutChangingIdentity()
    {
        var at = new DateTimeOffset(2026, 9, 4, 10, 7, 0, TimeSpan.Zero);
        var local = new ChatMessage("Tool", "⏳ Calling read…", at)
        {
            Id = "local-row",
            ToolName = "read",
            ToolCallId = "tc-1",
            IsToolCall = true
        };
        var server = new ChatMessage("Tool", "rest text", at)
        {
            ServerEntryId = "s-1#1",
            ToolName = "read",
            ToolCallId = "tc-1",
            ToolResult = "rest text",
            IsToolCall = true
        };

        var result = TranscriptReconciler.Reconcile([local], [server]);

        var repaired = result.ShouldHaveSingleItem();
        repaired.Id.ShouldBe("local-row");
        repaired.ServerEntryId.ShouldBe("s-1#1");
        repaired.Content.ShouldBe("rest text");
        repaired.ToolResult.ShouldBe("rest text");
        TranscriptReconciler.CountMissing([local], [server]).ShouldBe(0);
    }

    [Fact]
    public void Reconcile_CompletedLocalToolIsNotOverwrittenByStaleServerPage()
    {
        var at = new DateTimeOffset(2026, 9, 4, 10, 7, 0, TimeSpan.Zero);
        var local = new ChatMessage("Tool", "✅ read completed", at)
        {
            ToolName = "read",
            ToolCallId = "tc-1",
            ToolResult = "newer live result",
            IsToolCall = true
        };
        var staleServer = new ChatMessage("Tool", "stale result", at)
        {
            ToolName = "read",
            ToolCallId = "tc-1",
            ToolResult = "stale result",
            IsToolCall = true
        };

        TranscriptReconciler.Reconcile([local], [staleServer]).ShouldBe([local]);
    }

    [Fact]
    public void Reconcile_LiveAndPersistedMessageWithDifferentTimestamps_DoesNotDuplicate()
    {
        var live = new ChatMessage("Assistant", "same response", DateTimeOffset.Parse("2026-09-04T10:08:09Z"));
        var persisted = new ChatMessage("Assistant", "same response", DateTimeOffset.Parse("2026-09-04T10:08:00Z"))
        {
            ServerEntryId = "sess-1#1"
        };

        var result = TranscriptReconciler.Reconcile([live], [persisted]);

        result.ShouldBe([live]);
        TranscriptReconciler.CountMissing([live], [persisted]).ShouldBe(0);
    }

    [Fact]
    public void Reconcile_RepeatedSameContent_PreservesServerMultiplicityAndIsIdempotent()
    {
        var live = new ChatMessage("Assistant", "repeat", DateTimeOffset.Parse("2026-09-04T10:09:09Z"));
        var first = new ChatMessage("Assistant", "repeat", DateTimeOffset.Parse("2026-09-04T10:09:00Z"))
        {
            ServerEntryId = "sess-1#1"
        };
        var second = new ChatMessage("Assistant", "repeat", DateTimeOffset.Parse("2026-09-04T10:10:00Z"))
        {
            ServerEntryId = "sess-1#2"
        };

        var result = TranscriptReconciler.Reconcile([live], [first, second]);

        result.Count.ShouldBe(2);
        result[0].ShouldBeSameAs(live);
        result[1].ShouldBeSameAs(second);
        TranscriptReconciler.CountMissing([live], [first, second]).ShouldBe(1);
        TranscriptReconciler.Reconcile(result, [first, second]).ShouldBe(result);
    }

    [Fact]
    public void CountMissing_DuplicateServerIdentity_CountsOnlyOneInsert()
    {
        var serverRow = new ChatMessage("Assistant", "restored", DateTimeOffset.Parse("2026-09-04T10:11:00Z"))
        {
            ServerEntryId = "sess-1#3"
        };

        TranscriptReconciler.CountMissing([], [serverRow, serverRow]).ShouldBe(1);
        TranscriptReconciler.Reconcile([], [serverRow, serverRow]).ShouldBe([serverRow]);
    }

    [Fact]
    public void Reconcile_AuthoritativeCompletion_updates_running_lifecycle_without_changing_identity()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-16T20:41:03Z");
        var completedAt = startedAt.AddSeconds(14);
        var live = new ChatMessage("Tool", "calling", startedAt)
        {
            Id = "live-id", IsToolCall = true, ToolName = "read", ToolCallId = "call-refresh",
            ToolStartedAt = startedAt
        };
        var persisted = new ChatMessage("Tool", "ok", startedAt)
        {
            ServerEntryId = "entry-result", IsToolCall = true, ToolName = "read", ToolCallId = "call-refresh",
            ToolResult = "ok", ToolStartedAt = startedAt, ToolCompletedAt = completedAt,
            ToolDuration = TimeSpan.FromSeconds(14)
        };

        var result = TranscriptReconciler.Reconcile([live], [persisted]).ShouldHaveSingleItem();

        result.Id.ShouldBe("live-id");
        result.ToolStartedAt.ShouldBe(startedAt);
        result.ToolCompletedAt.ShouldBe(completedAt);
        result.ToolDuration.ShouldBe(TimeSpan.FromSeconds(14));
    }

}
