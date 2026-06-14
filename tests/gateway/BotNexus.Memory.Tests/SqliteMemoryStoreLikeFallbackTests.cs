using BotNexus.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Covers the bounded LIKE fallback (#1388): the recency window and row-ceiling that keep
/// the degraded-mode scan finite, and the shared <c>AppendFilters</c> path that the
/// fallback now uses (so it cannot diverge from the FTS filter SQL). The fallback is
/// exercised directly via the internal seam because forcing an FTS error from the public
/// <c>SearchAsync</c> path is non-deterministic.
/// </summary>
public sealed class SqliteMemoryStoreLikeFallbackTests
{
    private const double Lambda = 0d; // disable recency decay so scoring doesn't reorder results

    [Fact]
    public async Task LikeFallback_AppliesRecencyWindow_ExcludesOlderMemories()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "recent", agentId: "a", content: "shared keyword recent", createdAt: now.AddDays(-1)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "old", agentId: "a", content: "shared keyword old", createdAt: now.AddDays(-400)));

        // 30-day window: only the recent memory is eligible.
        var options = new MemoryLikeFallbackOptions { RecencyWindowDays = 30d, MaxScanRows = 1000 };
        var results = await context.Store.SearchWithLikeFallbackAsync("keyword", 10, filter: null, Lambda, options, CancellationToken.None);

        results.Select(r => r.Id).ShouldBe(["recent"]);
    }

    [Fact]
    public async Task LikeFallback_NullRecencyWindow_IncludesOlderMemories()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "recent", agentId: "a", content: "shared keyword recent", createdAt: now.AddDays(-1)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "old", agentId: "a", content: "shared keyword old", createdAt: now.AddDays(-400)));

        // Disabled window (null) → full history eligible.
        var options = new MemoryLikeFallbackOptions { RecencyWindowDays = null, MaxScanRows = null };
        var results = await context.Store.SearchWithLikeFallbackAsync("keyword", 10, filter: null, Lambda, options, CancellationToken.None);

        results.Select(r => r.Id).OrderBy(x => x).ShouldBe(["old", "recent"]);
    }

    [Fact]
    public async Task LikeFallback_AppliesRowCeiling_CapsCandidateScan()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        // 10 matching memories, all recent. With MaxScanRows=3 the candidate scan stops at 3.
        for (var i = 0; i < 10; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                id: $"m{i:00}", agentId: "a", content: $"capword body {i}", createdAt: now.AddMinutes(-i)));
        }

        var options = new MemoryLikeFallbackOptions { RecencyWindowDays = null, MaxScanRows = 3 };
        var results = await context.Store.SearchWithLikeFallbackAsync("capword", 50, filter: null, Lambda, options, CancellationToken.None);

        // Scan is ORDER BY created_at DESC LIMIT 3 → the three newest (m00, m01, m02).
        results.Count.ShouldBe(3);
        results.Select(r => r.Id).OrderBy(x => x).ShouldBe(["m00", "m01", "m02"]);
    }

    [Fact]
    public async Task LikeFallback_RespectsSharedFilters_SourceTypeAndSession()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "match", agentId: "a", content: "filterword body", sourceType: "manual", sessionId: "s-1", createdAt: now));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "wrong-source", agentId: "a", content: "filterword body", sourceType: "conversation", sessionId: "s-1", createdAt: now));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "wrong-session", agentId: "a", content: "filterword body", sourceType: "manual", sessionId: "s-2", createdAt: now));

        var filter = new MemorySearchFilter { SourceType = "manual", SessionId = "s-1" };
        var results = await context.Store.SearchWithLikeFallbackAsync("filterword", 10, filter, Lambda, MemoryLikeFallbackOptions.Default, CancellationToken.None);

        results.Select(r => r.Id).ShouldBe(["match"]);
    }

    [Fact]
    public async Task LikeFallback_RespectsTagFilter_ViaSharedAppendFilters()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "tagged", agentId: "a", content: "tagword body", createdAt: now,
            metadataJson: """{"tags":["billing"]}"""));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            id: "untagged", agentId: "a", content: "tagword body", createdAt: now,
            metadataJson: """{"tags":["other"]}"""));

        var filter = new MemorySearchFilter { Tags = ["billing"] };
        var results = await context.Store.SearchWithLikeFallbackAsync("tagword", 10, filter, Lambda, MemoryLikeFallbackOptions.Default, CancellationToken.None);

        results.Select(r => r.Id).ShouldBe(["tagged"]);
    }

    [Fact]
    public void DefaultOptions_HaveBoundedWindowAndCeiling()
    {
        // Guardrail: the default fallback must be bounded (a regression that nulled these
        // would silently reintroduce the unbounded full scan).
        MemoryLikeFallbackOptions.Default.RecencyWindowDays.ShouldBe(365d);
        MemoryLikeFallbackOptions.Default.MaxScanRows.ShouldBe(5000);
    }
}
