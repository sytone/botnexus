using BotNexus.Memory.Tests.TestInfrastructure;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Covers <c>expires_at</c> enforcement. The column was persisted, round-tripped and rendered from
/// the day it was added but appeared in no <c>WHERE</c> clause, so a row written with a TTL stayed
/// searchable forever. These tests pin the predicate at every retrieval path independently, because
/// the way the defect survived was one path filtering while another did not.
/// </summary>
public sealed class SqliteMemoryStoreExpiryTests
{
    [Fact]
    public async Task Search_ExpiredEntry_IsNotReturned()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var expired = MemoryStoreTestContext.CreateEntry("expired-1", "agent-a", "perishable token") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await context.Store.InsertAsync(expired);

        var results = await context.Store.SearchAsync("perishable");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_UnexpiredEntry_IsStillReturned()
    {
        // The sad path for the predicate itself: a TTL that has not elapsed must not hide the row,
        // or "enforce expiry" would just mean "drop everything carrying an expiry".
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var live = MemoryStoreTestContext.CreateEntry("live-1", "agent-a", "perishable token") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        await context.Store.InsertAsync(live);

        var results = await context.Store.SearchAsync("perishable");

        results.ShouldHaveSingleItem().Id.ShouldBe("live-1");
    }

    [Fact]
    public async Task Search_EntryWithNoExpiry_IsUnaffected()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("forever-1", "agent-a", "perishable token"));

        var results = await context.Store.SearchAsync("perishable");

        results.ShouldHaveSingleItem().Id.ShouldBe("forever-1");
    }

    [Fact]
    public async Task Search_MixedCorpus_ReturnsOnlyLiveEntries()
    {
        // Contents differ beyond the shared query token on purpose: the ranker collapses
        // near-duplicates, and short identical content collapses on exact match, so three rows
        // reading "mixedtoken" would return one row no matter what the expiry predicate did.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("keep-null", "agent-a", "mixedtoken alpha"));
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("keep-future", "agent-a", "mixedtoken beta") with { ExpiresAt = now.AddDays(1) });
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("drop-past", "agent-a", "mixedtoken gamma") with { ExpiresAt = now.AddDays(-1) });

        var results = await context.Store.SearchAsync("mixedtoken");

        results.Select(entry => entry.Id).ShouldBe(["keep-null", "keep-future"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_ExpiryWithNonUtcOffset_IsComparedAsAnInstant()
    {
        // Timestamps are stored in round-trip ("O") format, which carries an offset. A string
        // comparison would order "…T00:30:00+02:00" after "…T23:00:00+00:00" on text alone and get
        // the answer backwards; julianday normalises both sides to an instant. This entry expired
        // an hour ago but is written with a +05:30 offset, so it only disappears if the comparison
        // is chronological rather than lexicographic.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var expiredInIndia = DateTimeOffset.UtcNow.AddHours(-1).ToOffset(TimeSpan.FromMinutes(330));
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("offset-1", "agent-a", "offsettoken") with { ExpiresAt = expiredInIndia });

        var results = await context.Store.SearchAsync("offsettoken");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetById_ExpiredEntry_IsStillAddressable()
    {
        // Direct addressing deliberately sits outside the liveness predicate, exactly as it always
        // has for archived rows. Without this an expired entry would be unreachable by search AND
        // unreachable by id, which is the state that makes it impossible to delete.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("expired-2", "agent-a", "content") with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });

        var entry = await context.Store.GetByIdAsync("expired-2");

        entry.ShouldNotBeNull();
        entry!.Id.ShouldBe("expired-2");
    }

    [Fact]
    public async Task GetBySession_ExpiredEntry_IsStillListed()
    {
        // Same boundary as GetById, and load-bearing for a different reason: MemoryIndexer and
        // MarkdownAgentMemory reconcile a session against what they previously wrote. Hiding
        // expired rows here would make the indexer re-insert duplicates of them.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("expired-3", "agent-a", "content", sessionId: "session-x") with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });

        var entries = await context.Store.GetBySessionAsync("session-x");

        entries.ShouldHaveSingleItem().Id.ShouldBe("expired-3");
    }

    [Fact]
    public async Task ExplainSearch_ExpiredEntry_IsNotCountedAsLive()
    {
        // The diagnostics counter has to agree with the search it explains. A live-row count that
        // included expired rows would report a corpus the search cannot reach, which is the exact
        // class of "the number disagrees with the behaviour" defect the scan report exists to kill.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("live-2", "agent-a", "explaintoken alpha"));
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("expired-4", "agent-a", "explaintoken beta") with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
            });

        var diagnostics = await context.Store.ExplainSearchAsync("explaintoken");

        diagnostics.LiveRowCount.ShouldBe(1);
    }

    [Fact]
    public async Task LikeFallback_ExpiredEntry_IsNotReturned()
    {
        // The degraded path is a separate SQL statement from the FTS path. It filtered archived
        // rows independently and would have needed expiry added independently too — which is
        // precisely how the two halves of a liveness rule drift apart.
        // Distinct content again, so a regression that stopped filtering expiry would surface as
        // two rows rather than being masked by near-duplicate collapsing back down to one.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("expired-5", "agent-a", "fallbacktoken alpha") with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("live-3", "agent-a", "fallbacktoken beta"));

        // Driven through the internal seam for the same reason the other fallback tests are:
        // forcing an FTS error from the public path is non-deterministic. Lambda 0 disables
        // recency decay so ordering cannot mask a missing row.
        var results = await context.Store.SearchWithLikeFallbackAsync(
            "fallbacktoken", 10, filter: null, lambda: 0d, MemoryLikeFallbackOptions.Default, CancellationToken.None);

        results.ShouldHaveSingleItem().Id.ShouldBe("live-3");
    }
}
