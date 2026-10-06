using BotNexus.Memory.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests;

public sealed class MemoryTemporalDecayStoreTests
{
    [Fact]
    public async Task SearchScoredAsync_DisabledPolicyGivesEqualAgeAdjustedScores()
    {
        await using var context = await CreateAsync(new MemoryTemporalDecayPolicy(false, 7d));
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "old", "agent", "decaycontract unique old", createdAt: now.AddDays(-180)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "recent", "agent", "decaycontract unique recent", createdAt: now.AddMinutes(-1)));

        var results = await context.Store.SearchScoredAsync("decaycontract", 2);

        results.Count.ShouldBe(2);
        results[0].Score.ShouldBe(results[1].Score, tolerance: 1e-10);
        var report = await context.Store.SearchWithReportAsync("decaycontract", 2);
        report.TemporalDecay.ShouldBe(new MemoryTemporalDecayPolicy(false, 7d));
    }

    [Fact]
    public async Task SearchScoredAsync_ConfiguredHalfLifeHalvesScoreAtBoundary()
    {
        const double halfLifeDays = 14d;
        await using var context = await CreateAsync(new MemoryTemporalDecayPolicy(true, halfLifeDays));
        var now = DateTimeOffset.UtcNow;
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "boundary", "agent", "halfboundary unique boundary", createdAt: now.AddDays(-halfLifeDays)));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "fresh", "agent", "halfboundary unique fresh", createdAt: now));

        var results = await context.Store.SearchScoredAsync("halfboundary", 2);
        var scores = results.ToDictionary(result => result.Entry.Id, result => result.Score);

        (scores["boundary"] / scores["fresh"]).ShouldBe(0.5d, tolerance: 0.002d);
        var report = await context.Store.SearchWithReportAsync("halfboundary", 2);
        report.TemporalDecay.ShouldBe(new MemoryTemporalDecayPolicy(true, halfLifeDays));
    }

    private static async Task<MemoryStoreTestContext> CreateAsync(MemoryTemporalDecayPolicy policy)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "memory.db");
        var store = new SqliteMemoryStore(dbPath, new FileSystem(), temporalDecayPolicy: () => policy);
        await store.InitializeAsync();
        return new MemoryStoreTestContext(tempDirectory, dbPath, store);
    }
}
