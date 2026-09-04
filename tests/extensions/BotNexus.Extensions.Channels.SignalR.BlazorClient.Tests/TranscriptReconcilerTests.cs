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
    /// not duplicate the row already rendered from the live SignalR ToolStart/ToolEnd pair even
    /// though the REST copy carries the stripped result as its content.
    /// </summary>
    [Fact]
    public void Reconcile_ToolRowsAreDedupedByToolCallId()
    {
        var at = new DateTimeOffset(2026, 9, 4, 10, 7, 0, TimeSpan.Zero);
        var local = new List<ChatMessage>
        {
            new("assistant", "live text", at) { ToolName = "read", ToolCallId = "tc-1", IsToolCall = true }
        };
        var server = new List<ChatMessage>
        {
            new("assistant", "rest text", at) { ToolName = "read", ToolCallId = "tc-1", IsToolCall = true }
        };

        var result = TranscriptReconciler.Reconcile(local, server);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("live text");
    }
}
