using BotNexus.Memory.Tests.TestInfrastructure;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Pins the session-scoped delete contract added for issue #2956. Before this existed the
/// store could only delete one row by id or truncate every row, so deleting a session left
/// its memory rows searchable forever.
/// </summary>
public sealed class SqliteMemoryStoreDeleteBySessionTests
{
    [Fact]
    public async Task DeleteBySessionAsync_RemovesOnlyThatSessionsRows_AndReturnsCount()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "doomed one", sessionId: "s-doomed"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", "agent-a", "doomed two", sessionId: "s-doomed"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m3", "agent-a", "survivor", sessionId: "s-keep"));

        var deleted = await context.Store.DeleteBySessionAsync("s-doomed");

        deleted.ShouldBe(2);

        var doomed = await context.Store.GetBySessionAsync("s-doomed");
        doomed.ShouldBeEmpty("every memory row scoped to the deleted session must be gone");

        var kept = await context.Store.GetBySessionAsync("s-keep");
        kept.Count.ShouldBe(1, "an unrelated session's rows must be untouched");
    }

    [Fact]
    public async Task DeleteBySessionAsync_RemovesRowsFromTheSearchIndex()
    {
        // The whole point of #2956: the rows must stop being *searchable*, not merely stop
        // being listed by GetBySessionAsync. The FTS mirror is maintained by trigger, so this
        // asserts the delete actually flows through it.
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "m1", "agent-a", "extraordinarily distinctive phrase", sessionId: "s-doomed"));

        var before = await context.Store.SearchAsync("extraordinarily");
        before.ShouldNotBeEmpty("precondition: the row must be searchable before deletion");

        await context.Store.DeleteBySessionAsync("s-doomed");

        var after = await context.Store.SearchAsync("extraordinarily");
        after.ShouldBeEmpty("a deleted session's content must no longer surface in memory search");
    }

    [Fact]
    public async Task DeleteBySessionAsync_NeverTouchesRowsWithNullSessionId()
    {
        // Clause 5 of #2956. Non-session memories (memory_save, learning extractions,
        // shared-store promotions) carry session_id IS NULL and are not session-scoped
        // content; a session delete must never reach them.
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "session bound", sessionId: "s-doomed"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", "agent-a", "manually saved note", sessionId: null));

        await context.Store.DeleteBySessionAsync("s-doomed");

        var survivor = await context.Store.GetByIdAsync("m2");
        survivor.ShouldNotBeNull("a memory row with a NULL session_id must survive any session-scoped delete");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeleteBySessionAsync_WithBlankSessionId_DeletesNothing(string sessionId)
    {
        // Sad path: a blank id must never degenerate into a broad delete. In particular it
        // must not match the NULL-session rows.
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "session bound", sessionId: "s1"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", "agent-a", "unscoped", sessionId: null));

        var deleted = await context.Store.DeleteBySessionAsync(sessionId);

        deleted.ShouldBe(0);
        (await context.Store.GetByIdAsync("m1")).ShouldNotBeNull();
        (await context.Store.GetByIdAsync("m2")).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteBySessionAsync_WithUnknownSession_IsIdempotentNoOp()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "kept", sessionId: "s1"));

        var deleted = await context.Store.DeleteBySessionAsync("never-existed");

        deleted.ShouldBe(0);
        (await context.Store.GetByIdAsync("m1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ListSessionIdsAsync_ReturnsDistinctNonNullSessionIds()
    {
        // The reconciliation scan reads this. NULL rows must not appear, or the reconciler
        // would try to resolve a non-existent session and prune unscoped memories.
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "a", sessionId: "s1"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", "agent-a", "b", sessionId: "s1"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m3", "agent-a", "c", sessionId: "s2"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m4", "agent-a", "d", sessionId: null));

        var ids = await context.Store.ListSessionIdsAsync();

        ids.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["s1", "s2"]);
    }
}
