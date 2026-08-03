using BotNexus.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Issue #2740: FTS5 treats a space-separated MATCH expression as implicit AND, so a
/// natural-language query only matched rows containing *every* term. These tests pin the
/// explicit MATCH construction that replaces that inherited default.
/// </summary>
public sealed class SqliteMemoryStoreFtsMatchTests
{
    private const string AgentId = "agent-2740";

    /// <summary>The six queries from the issue's Evidence table, all of which returned 0 rows.</summary>
    public static TheoryData<string> EvidenceQueries =>
    [
        "OpenClaw model architecture overview",
        "debug platform sqlite sessions compaction gateway troubleshooting",
        "canvas tool issues problems workaround",
        "gateway freeze investigation PR 1076 1078 config reload debounce liveness watchdog",
        "service bus channel Teams bot BotNexus",
        "BotNexus OpenClaw platform agent",
    ];

    // AC1
    [Fact]
    public async Task SearchAsync_WhenNoRowContainsEveryTerm_ReturnsRankedResults_Ac1()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", AgentId, "the gateway restarted after a config reload"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", AgentId, "sqlite compaction notes for the sessions store"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m3", AgentId, "troubleshooting the debug platform harness"));

        var results = await context.Store.SearchAsync(
            "debug platform sqlite sessions compaction gateway troubleshooting", 10);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Id == "m1");
        Assert.Contains(results, r => r.Id == "m2");
        Assert.Contains(results, r => r.Id == "m3");
    }

    // AC2
    [Theory]
    [MemberData(nameof(EvidenceQueries))]
    public async Task SearchAsync_EvidenceTableQuery_ReturnsNonEmptyResults_Ac2(string query)
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await SeedTermDistributionAsync(context, query);

        var results = await context.Store.SearchAsync(query, 10);

        Assert.NotEmpty(results);
    }

    // AC3
    [Fact]
    public async Task SearchAsync_RowMatchingAllTermsOutranksRowMatchingOne_Ac3()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var createdAt = DateTimeOffset.UtcNow;

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "one-term", AgentId, "an unrelated note that only mentions canvas", createdAt: createdAt));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "all-terms", AgentId, "canvas render html tab notes", createdAt: createdAt));

        var results = await context.Store.SearchAsync("canvas render html tab", 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("all-terms", results[0].Id);
        Assert.Equal("one-term", results[1].Id);
    }

    // AC4 - short exact query that already worked must keep the same top result.
    [Fact]
    public async Task SearchAsync_ShortExactQuery_KeepsSameTopResult_Ac4()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var createdAt = DateTimeOffset.UtcNow;

        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
            "exact", AgentId, "gh auth sytone account switch botnexus", createdAt: createdAt));
        for (var i = 0; i < 5; i++)
        {
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"noise{i}", AgentId, "account notes about an unrelated switch", createdAt: createdAt));
        }

        var results = await context.Store.SearchAsync("gh auth sytone account switch botnexus", 10);

        Assert.NotEmpty(results);
        Assert.Equal("exact", results[0].Id);
    }

    // AC5 - an empty corpus and an impossible conjunction must be distinguishable.
    [Fact]
    public async Task ExplainSearchAsync_EmptyCorpus_ReportsEmptyCorpus_Ac5()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var diagnostics = await context.Store.ExplainSearchAsync("gateway sqlite compaction");

        Assert.True(diagnostics.CorpusIsEmpty);
        Assert.Equal(0, diagnostics.LiveRowCount);
        Assert.All(diagnostics.TermHits, hit => Assert.Equal(0, hit.RowCount));
        Assert.False(diagnostics.ConjunctionImpossible);
        Assert.Contains("empty", diagnostics.Explain(), StringComparison.OrdinalIgnoreCase);
    }

    // AC5
    [Fact]
    public async Task ExplainSearchAsync_ImpossibleConjunction_IsDistinguishableFromEmptyCorpus_Ac5()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("a", AgentId, "gateway notes"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("b", AgentId, "sqlite notes"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("c", AgentId, "compaction notes"));

        var diagnostics = await context.Store.ExplainSearchAsync("gateway sqlite compaction");

        Assert.False(diagnostics.CorpusIsEmpty);
        Assert.Equal(3, diagnostics.LiveRowCount);
        Assert.True(diagnostics.ConjunctionImpossible);
        Assert.Equal(0, diagnostics.ConjunctionRowCount);
        Assert.Equal(3, diagnostics.MatchedRowCount);
        Assert.All(diagnostics.TermHits, hit => Assert.Equal(1, hit.RowCount));
        Assert.Contains("conjunction", diagnostics.Explain(), StringComparison.OrdinalIgnoreCase);
    }

    // AC6 support - the MATCH expression is built explicitly, not inherited from the FTS default.
    [Fact]
    public void BuildFtsMatchExpression_JoinsTermsExplicitly_NotWithBareSpaces_Ac6()
    {
        var or = SqliteMemoryStore.BuildFtsMatchExpression("gateway sqlite compaction", requireAllTerms: false);
        var and = SqliteMemoryStore.BuildFtsMatchExpression("gateway sqlite compaction", requireAllTerms: true);

        Assert.Equal("\"gateway\" OR \"sqlite\" OR \"compaction\"", or);
        Assert.Equal("\"gateway\" AND \"sqlite\" AND \"compaction\"", and);
    }

    /// <summary>
    /// Seeds one row per query term so every term is individually well represented while no
    /// single row carries all of them - the exact distribution described in the issue.
    /// </summary>
    private static async Task SeedTermDistributionAsync(MemoryStoreTestContext context, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < terms.Length; i++)
        {
            var partner = terms[(i + 1) % terms.Length];
            await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry(
                $"seed-{i}", AgentId, $"note about {terms[i]} and {partner}"));
        }
    }
}
