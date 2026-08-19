using System.IO.Abstractions;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// Coverage for issue #3244: the bounded vector scan used to truncate recall with no signal at
/// all, so a truncated scan and an exhaustive one were indistinguishable to every caller.
/// </summary>
/// <remarks>
/// The bound itself is correct cost control and is NOT under test here — these tests assert that
/// crossing it is observable, that the observation is distinguishable from "the corpus was fully
/// scanned and nothing else matched", and that an old lexically-plausible row can still be scored
/// despite falling outside the recency window.
/// </remarks>
public sealed class SqliteMemoryStoreVectorScanTruncationTests : IAsyncLifetime
{
    private const int Dimensions = 4;
    private static readonly EmbeddingIdentity Identity = new("stub-model", "fp-1", Dimensions);

    private string _tempDirectory = string.Empty;
    private string _dbPath = string.Empty;

    public Task InitializeAsync()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-scan-ceiling-tests", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDirectory, "memory.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, true); } catch (IOException) { }
        }

        return Task.CompletedTask;
    }

    private SqliteMemoryStore CreateStore(
        IMemoryEmbeddingService? embeddings,
        int? maxScanRows = null,
        ILogger<SqliteMemoryStore>? logger = null)
        => new(
            _dbPath,
            new FileSystem(),
            null,
            embeddings,
            maxScanRows is null ? null : new MemoryVectorSearchOptions { MaxScanRows = maxScanRows },
            logger);

    private static IMemoryEmbeddingService Embeddings(IReadOnlyDictionary<string, float[]> vectors)
        => new MemoryEmbeddingService(new StubEmbeddingGenerator(vectors, Dimensions), Identity);

    private static MemoryEntry Entry(string id, string content, DateTimeOffset createdAt)
        => new()
        {
            Id = id,
            AgentId = "agent",
            SourceType = "conversation",
            Content = content,
            CreatedAt = createdAt
        };

    // ---------------------------------------------------------------- AC1 / AC2

    [Fact]
    public async Task SearchWithReport_CorpusBelowCeiling_ReportsCompleteScan()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["alpha note"] = [1f, 0f, 0f, 0f],
            ["beta note"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 10);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(Entry("a", "alpha note", now));
        await store.InsertAsync(Entry("b", "beta note", now.AddMinutes(-1)));

        var result = await store.SearchWithReportAsync("query text", 10);

        Assert.Equal(MemoryVectorScanStatus.Complete, result.VectorScan.Status);
        Assert.False(result.VectorScan.IsPossiblyTruncated);
        Assert.Equal(2, result.VectorScan.RowsScanned);
        Assert.Equal(10, result.VectorScan.ScanCeiling);
        Assert.Equal(0, result.VectorScan.LexicalUnionRowsScanned);
    }

    [Fact]
    public async Task SearchWithReport_CorpusAboveCeiling_RaisesTruncationSignal()
    {
        var vectors = new Dictionary<string, float[]> { ["query text"] = [1f, 0f, 0f, 0f] };
        for (var i = 0; i < 6; i++)
            vectors[$"note {i}"] = [1f, 0f, 0f, 0f];

        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 3);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++)
            await store.InsertAsync(Entry($"m{i}", $"note {i}", now.AddMinutes(-i)));

        var result = await store.SearchWithReportAsync("query text", 10);

        Assert.Equal(MemoryVectorScanStatus.PossiblyTruncated, result.VectorScan.Status);
        Assert.True(result.VectorScan.IsPossiblyTruncated);
        Assert.Equal(3, result.VectorScan.RowsScanned);
        Assert.Equal(3, result.VectorScan.ScanCeiling);
    }

    [Fact]
    public async Task SearchWithReport_TruncationSignal_IsDistinctFromCompleteScanWithNoOtherMatch()
    {
        // AC2 stated as one assertion pair rather than two independent tests: the SAME store, the
        // SAME query, differing only in the ceiling, must produce different statuses. If both
        // reported Complete the signal would be worthless; if both reported PossiblyTruncated it
        // would be an alarm nobody could act on.
        var vectors = new Dictionary<string, float[]> { ["query text"] = [1f, 0f, 0f, 0f] };
        for (var i = 0; i < 4; i++)
            vectors[$"note {i}"] = [1f, 0f, 0f, 0f];

        var now = DateTimeOffset.UtcNow;
        await using (var seed = CreateStore(Embeddings(vectors), maxScanRows: 100))
        {
            await seed.InitializeAsync();
            for (var i = 0; i < 4; i++)
                await seed.InsertAsync(Entry($"m{i}", $"note {i}", now.AddMinutes(-i)));
        }

        await using var generous = CreateStore(Embeddings(vectors), maxScanRows: 100);
        var complete = await generous.SearchWithReportAsync("query text", 10);

        await using var tight = CreateStore(Embeddings(vectors), maxScanRows: 2);
        var truncated = await tight.SearchWithReportAsync("query text", 10);

        Assert.Equal(MemoryVectorScanStatus.Complete, complete.VectorScan.Status);
        Assert.Equal(MemoryVectorScanStatus.PossiblyTruncated, truncated.VectorScan.Status);
        Assert.NotEqual(complete.VectorScan.Status, truncated.VectorScan.Status);
    }

    [Fact]
    public async Task SearchWithReport_NoCeilingConfigured_ReportsCompleteWithNoCeiling()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["only note"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 0);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "only note", DateTimeOffset.UtcNow));

        var result = await store.SearchWithReportAsync("query text", 10);

        Assert.Equal(MemoryVectorScanStatus.Complete, result.VectorScan.Status);
        Assert.Null(result.VectorScan.ScanCeiling);
    }

    // ---------------------------------------------------------------- AC5

    [Fact]
    public async Task SearchWithReport_OldLexicalCandidate_IsScoredDespiteFallingOutsideCeiling()
    {
        // The row that matters is the OLDEST, so a pure recency-ordered scan of 2 rows can never
        // reach it. It is lexically plausible for the query, so AC5 requires it to be scored anyway.
        var vectors = new Dictionary<string, float[]>
        {
            ["kubernetes"] = [1f, 0f, 0f, 0f],
            ["recent filler one"] = [0f, 1f, 0f, 0f],
            ["recent filler two"] = [0f, 1f, 0f, 0f],
            ["recent filler three"] = [0f, 1f, 0f, 0f],
            ["ancient kubernetes upgrade note"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 3);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(Entry("old", "ancient kubernetes upgrade note", now.AddDays(-30)));
        await store.InsertAsync(Entry("f1", "recent filler one", now.AddMinutes(-3)));
        await store.InsertAsync(Entry("f2", "recent filler two", now.AddMinutes(-2)));
        await store.InsertAsync(Entry("f3", "recent filler three", now.AddMinutes(-1)));

        var result = await store.SearchWithReportAsync("kubernetes", 10);

        Assert.True(result.VectorScan.IsPossiblyTruncated);
        Assert.Equal(1, result.VectorScan.LexicalUnionRowsScanned);
        Assert.Contains(result.Entries, scored => scored.Entry.Id == "old");
    }

    [Fact]
    public async Task SearchWithReport_CompleteScan_RunsNoLexicalUnionPass()
    {
        // Sad path for the rescue pass: when nothing was truncated the union pass is pure waste and
        // must not run. Asserting 0 here is what stops the rescue becoming an unconditional second
        // query on the hot path.
        var vectors = new Dictionary<string, float[]>
        {
            ["kubernetes"] = [1f, 0f, 0f, 0f],
            ["kubernetes upgrade note"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 50);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "kubernetes upgrade note", DateTimeOffset.UtcNow.AddDays(-30)));

        var result = await store.SearchWithReportAsync("kubernetes", 10);

        Assert.Equal(MemoryVectorScanStatus.Complete, result.VectorScan.Status);
        Assert.Equal(0, result.VectorScan.LexicalUnionRowsScanned);
    }

    [Fact]
    public async Task SearchWithReport_LexicalUnionPass_StillHonoursSearchFilters()
    {
        // The rescue pass must not become a filter bypass: an old row in the wrong source type is
        // still out of scope, however lexically plausible it looks.
        var vectors = new Dictionary<string, float[]>
        {
            ["kubernetes"] = [1f, 0f, 0f, 0f],
            ["recent filler one"] = [0f, 1f, 0f, 0f],
            ["recent filler two"] = [0f, 1f, 0f, 0f],
            ["ancient kubernetes note"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 2);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(Entry("old", "ancient kubernetes note", now.AddDays(-30)) with { SourceType = "dreaming" });
        await store.InsertAsync(Entry("f1", "recent filler one", now.AddMinutes(-2)) with { SourceType = "manual" });
        await store.InsertAsync(Entry("f2", "recent filler two", now.AddMinutes(-1)) with { SourceType = "manual" });

        var result = await store.SearchWithReportAsync("kubernetes", 10, new MemorySearchFilter { SourceType = "manual" });

        Assert.DoesNotContain(result.Entries, scored => scored.Entry.Id == "old");
    }

    // ---------------------------------------------------------------- AC6

    [Fact]
    public async Task SearchWithReport_EmbeddingsDisabled_ReportsNotAttempted()
    {
        await using var store = CreateStore(embeddings: null, maxScanRows: 1);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "the deployment pipeline failed", DateTimeOffset.UtcNow));
        await store.InsertAsync(Entry("m2", "another deployment note", DateTimeOffset.UtcNow));

        var result = await store.SearchWithReportAsync("deployment", 10);

        // Not merely "not truncated": the scan never ran, so it paid no cost and claims no coverage.
        Assert.Equal(MemoryVectorScanStatus.NotAttempted, result.VectorScan.Status);
        Assert.False(result.VectorScan.IsPossiblyTruncated);
        Assert.Equal(0, result.VectorScan.RowsScanned);
        Assert.NotEmpty(result.Entries);
    }

    [Fact]
    public async Task SearchWithReport_BlankQuery_ReportsNotAttempted()
    {
        var vectors = new Dictionary<string, float[]> { ["   "] = [1f, 0f, 0f, 0f] };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 1);
        await store.InitializeAsync();

        var result = await store.SearchWithReportAsync("   ", 10);

        Assert.Empty(result.Entries);
        Assert.Equal(MemoryVectorScanStatus.NotAttempted, result.VectorScan.Status);
    }

    // ---------------------------------------------------------------- AC3

    [Fact]
    public async Task GetStats_ReportsEmbeddedRowCountAndCeiling_MatchingSeededStore()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["embedded one"] = [1f, 0f, 0f, 0f],
            ["embedded two"] = [1f, 0f, 0f, 0f],
            ["embedded three"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 2);
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(Entry("e1", "embedded one", now));
        await store.InsertAsync(Entry("e2", "embedded two", now.AddMinutes(-1)));
        await store.InsertAsync(Entry("e3", "embedded three", now.AddMinutes(-2)));

        var stats = await store.GetStatsAsync();

        Assert.Equal(3, stats.EntryCount);
        Assert.Equal(3, stats.EmbeddedEntryCount);
        Assert.Equal(2, stats.VectorScanCeiling);
        Assert.True(stats.ExceedsVectorScanCeiling);
    }

    [Fact]
    public async Task GetStats_ExcludesUnembeddedRows_FromEmbeddedCount()
    {
        // Sad path: EmbeddedEntryCount must count VECTORS, not rows. Counting rows would compare an
        // inflated number against the ceiling and raise the alarm on stores that cannot truncate.
        await using var store = CreateStore(embeddings: null, maxScanRows: 5);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "no vector here", DateTimeOffset.UtcNow));

        var stats = await store.GetStatsAsync();

        Assert.Equal(1, stats.EntryCount);
        Assert.Equal(0, stats.EmbeddedEntryCount);
        Assert.False(stats.ExceedsVectorScanCeiling);
    }

    [Fact]
    public async Task GetStats_NoCeilingConfigured_ReportsNullCeilingAndNeverExceeds()
    {
        var vectors = new Dictionary<string, float[]> { ["only note"] = [1f, 0f, 0f, 0f] };
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 0);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "only note", DateTimeOffset.UtcNow));

        var stats = await store.GetStatsAsync();

        Assert.Null(stats.VectorScanCeiling);
        Assert.False(stats.ExceedsVectorScanCeiling);
    }

    // ---------------------------------------------------------------- AC4

    [Fact]
    public async Task Initialize_EmbeddedRowsExceedCeiling_WarnsOncePerStoreOpen()
    {
        var vectors = new Dictionary<string, float[]>();
        for (var i = 0; i < 4; i++)
            vectors[$"note {i}"] = [1f, 0f, 0f, 0f];

        var now = DateTimeOffset.UtcNow;
        await using (var seed = CreateStore(Embeddings(vectors), maxScanRows: 100))
        {
            await seed.InitializeAsync();
            for (var i = 0; i < 4; i++)
                await seed.InsertAsync(Entry($"m{i}", $"note {i}", now.AddMinutes(-i)));
        }

        var logger = new RecordingLogger<SqliteMemoryStore>();
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 2, logger: logger);

        // Three independent triggers of initialization; the warning is a store-level fact, so it
        // must be emitted once, not once per query.
        await store.InitializeAsync();
        await store.SearchAsync("note 0", 10);
        await store.SearchAsync("note 1", 10);

        var warnings = logger.Warnings.Where(text => text.Contains("vector search", StringComparison.Ordinal)).ToList();
        Assert.Single(warnings);
        // Assert on the rendered numbers in context, not on bare digits: the db path carries a GUID
        // that would satisfy a naked Contains("2") and make this assertion vacuous.
        Assert.Contains("holds 4 embedded rows", warnings[0], StringComparison.Ordinal);
        Assert.Contains("scans at most 2", warnings[0], StringComparison.Ordinal);
        Assert.Contains("approximately 2 ", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_EmbeddedRowsBelowCeiling_EmitsNoWarning()
    {
        var vectors = new Dictionary<string, float[]> { ["only note"] = [1f, 0f, 0f, 0f] };
        var logger = new RecordingLogger<SqliteMemoryStore>();
        await using var store = CreateStore(Embeddings(vectors), maxScanRows: 50, logger: logger);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "only note", DateTimeOffset.UtcNow));

        Assert.Empty(logger.Warnings);
    }

    // ---------------------------------------------------------------- Explain()

    [Fact]
    public void Explain_DistinguishesAllThreeScanStates()
    {
        var notAttempted = MemoryVectorScanReport.NotAttempted.Explain();
        var complete = new MemoryVectorScanReport(MemoryVectorScanStatus.Complete, 12, 100, 0).Explain();
        var truncated = new MemoryVectorScanReport(MemoryVectorScanStatus.PossiblyTruncated, 100, 100, 3).Explain();

        Assert.Contains("did not run", notAttempted, StringComparison.Ordinal);
        Assert.Contains("all 12", complete, StringComparison.Ordinal);
        Assert.Contains("ceiling", truncated, StringComparison.Ordinal);
        Assert.Equal(3, new[] { notAttempted, complete, truncated }.Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>Captures warning-level log messages so the once-per-open contract can be asserted.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}
