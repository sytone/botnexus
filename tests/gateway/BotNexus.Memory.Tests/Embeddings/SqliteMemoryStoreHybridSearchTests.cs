using System.IO.Abstractions;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests.Embeddings;

/// <summary>
/// End-to-end hybrid retrieval tests over a real SQLite store. These are the tests that
/// matter most: they assert that adding vectors did not change what the store does when
/// vectors are absent, and that scope/filter/decay semantics survive the fusion.
/// </summary>
public sealed class SqliteMemoryStoreHybridSearchTests : IAsyncLifetime
{
    private const int Dimensions = 4;
    private static readonly EmbeddingIdentity Identity = new("stub-model", "fp-1", Dimensions);

    private string _tempDirectory = string.Empty;
    private string _dbPath = string.Empty;

    public Task InitializeAsync()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-hybrid-tests", Guid.NewGuid().ToString("N"));
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

    private SqliteMemoryStore CreateStore(IMemoryEmbeddingService? embeddings)
        => new(_dbPath, new FileSystem(), null, embeddings);

    private static IMemoryEmbeddingService Embeddings(
        IReadOnlyDictionary<string, float[]> vectors,
        EmbeddingIdentity? identity = null,
        Exception? throwOnGenerate = null)
        => new MemoryEmbeddingService(
            new StubEmbeddingGenerator(vectors, Dimensions, throwOnGenerate),
            identity ?? Identity);

    private static MemoryEntry Entry(string id, string content, DateTimeOffset? createdAt = null, string sourceType = "conversation", string? sessionId = null, string? metadataJson = null)
        => new()
        {
            Id = id,
            AgentId = "agent",
            SessionId = sessionId,
            SourceType = sourceType,
            Content = content,
            MetadataJson = metadataJson,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Insert_StampsEmbedding_WithActiveModelIdentity()
    {
        var vectors = new Dictionary<string, float[]> { ["hello world"] = [1f, 0f, 0f, 0f] };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();

        await store.InsertAsync(Entry("m1", "hello world"));

        var stored = await store.GetByIdAsync("m1");
        Assert.NotNull(stored!.Embedding);
        Assert.True(EmbeddingBlob.TryDecode(stored.Embedding, out var identity, out var vector));
        Assert.Equal(Identity, identity);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, vector!);
    }

    [Fact]
    public async Task Insert_StoresNoEmbedding_WhenGeneratorAbsent()
    {
        await using var store = CreateStore(embeddings: null);
        await store.InitializeAsync();

        await store.InsertAsync(Entry("m1", "hello world"));

        var stored = await store.GetByIdAsync("m1");
        Assert.Null(stored!.Embedding);
    }

    [Fact]
    public async Task Insert_Succeeds_WhenEmbeddingGenerationThrows()
    {
        // Sad path: a broken or missing model must never block a memory write.
        var embeddings = Embeddings(new Dictionary<string, float[]>(), throwOnGenerate: new InvalidOperationException("model not loaded"));
        await using var store = CreateStore(embeddings);
        await store.InitializeAsync();

        await store.InsertAsync(Entry("m1", "hello world"));

        var stored = await store.GetByIdAsync("m1");
        Assert.NotNull(stored);
        Assert.Null(stored!.Embedding);
    }

    [Fact]
    public async Task Search_DegradesToLexicalOnly_WhenGeneratorAbsent()
    {
        await using var store = CreateStore(embeddings: null);
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "the deployment pipeline failed"));
        await store.InsertAsync(Entry("m2", "unrelated grocery list"));

        var results = await store.SearchAsync("deployment", 10);

        Assert.Single(results);
        Assert.Equal("m1", results[0].Id);
    }

    [Fact]
    public async Task Search_DegradesToLexicalOnly_WhenGenerationFailsAtQueryTime()
    {
        // Rows were embedded while the model worked; the model then broke. Search must still
        // return the BM25 answer rather than erroring or returning nothing.
        var vectors = new Dictionary<string, float[]> { ["the deployment pipeline failed"] = [1f, 0f, 0f, 0f] };
        await using (var writeStore = CreateStore(Embeddings(vectors)))
        {
            await writeStore.InitializeAsync();
            await writeStore.InsertAsync(Entry("m1", "the deployment pipeline failed"));
        }

        var broken = Embeddings(new Dictionary<string, float[]>(), throwOnGenerate: new InvalidOperationException("model unloaded"));
        await using var store = CreateStore(broken);
        await store.InitializeAsync();

        var results = await store.SearchAsync("deployment", 10);

        Assert.Single(results);
        Assert.Equal("m1", results[0].Id);
    }

    [Fact]
    public async Task Search_FindsParaphrase_ThatLexicalSearchCannotMatch()
    {
        // The whole point of the issue: no shared surface terms, same meaning.
        var vectors = new Dictionary<string, float[]>
        {
            ["the release rollout broke overnight"] = [1f, 0f, 0f, 0f],
            ["shipping a new build stopped working"] = [0.98f, 0.199f, 0f, 0f],
            ["reminder to buy oat milk"] = [0f, 0f, 1f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "shipping a new build stopped working"));
        await store.InsertAsync(Entry("m2", "reminder to buy oat milk"));

        var results = await store.SearchAsync("the release rollout broke overnight", 10);

        Assert.Contains(results, entry => entry.Id == "m1");
        Assert.Equal("m1", results[0].Id);
    }

    [Fact]
    public async Task Search_ExcludesVectors_StampedWithADifferentModelIdentity()
    {
        // Rows written by an older model build must not contribute similarity evidence.
        var oldIdentity = new EmbeddingIdentity("stub-model", "fp-OLD", Dimensions);
        var vectors = new Dictionary<string, float[]>
        {
            ["the release rollout broke overnight"] = [1f, 0f, 0f, 0f],
            ["shipping a new build stopped working"] = [1f, 0f, 0f, 0f]
        };

        await using (var oldStore = CreateStore(Embeddings(vectors, oldIdentity)))
        {
            await oldStore.InitializeAsync();
            await oldStore.InsertAsync(Entry("m1", "shipping a new build stopped working"));
        }

        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();

        var results = await store.SearchAsync("the release rollout broke overnight", 10);

        // Identity mismatch => no similarity evidence => the paraphrase is not recalled,
        // and crucially the mismatched vector was never compared.
        Assert.DoesNotContain(results, entry => entry.Id == "m1");
    }

    [Fact]
    public async Task Search_IgnoresCorruptEmbeddingBlobs()
    {
        var vectors = new Dictionary<string, float[]> { ["query text"] = [1f, 0f, 0f, 0f] };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();

        var entry = Entry("m1", "some stored content") with { Embedding = [0x00, 0x01, 0x02] };
        await store.InsertAsync(entry);

        var results = await store.SearchAsync("query text", 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_PreservesSourceTypeFilter_OnTheVectorPath()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["semantically close but wrong source"] = [1f, 0f, 0f, 0f],
            ["semantically close and right source"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("wrong", "semantically close but wrong source", sourceType: "dreaming"));
        await store.InsertAsync(Entry("right", "semantically close and right source", sourceType: "manual"));

        var results = await store.SearchAsync("query text", 10, new MemorySearchFilter { SourceType = "manual" });

        Assert.Single(results);
        Assert.Equal("right", results[0].Id);
    }

    [Fact]
    public async Task Search_PreservesSessionAndDateFilters_OnTheVectorPath()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["candidate one"] = [1f, 0f, 0f, 0f],
            ["candidate two"] = [1f, 0f, 0f, 0f],
            ["candidate three"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(Entry("in-session", "candidate one", now, sessionId: "s1"));
        await store.InsertAsync(Entry("other-session", "candidate two", now, sessionId: "s2"));
        await store.InsertAsync(Entry("too-old", "candidate three", now.AddDays(-400), sessionId: "s1"));

        var sessionResults = await store.SearchAsync("query text", 10, new MemorySearchFilter { SessionId = "s1" });
        Assert.DoesNotContain(sessionResults, entry => entry.Id == "other-session");

        var dateResults = await store.SearchAsync("query text", 10, new MemorySearchFilter { AfterDate = now.AddDays(-30) });
        Assert.DoesNotContain(dateResults, entry => entry.Id == "too-old");
    }

    [Fact]
    public async Task Search_PreservesTagFilter_OnTheVectorPath()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["tagged content"] = [1f, 0f, 0f, 0f],
            ["untagged content"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("tagged", "tagged content", metadataJson: """{"tags":["ops"]}"""));
        await store.InsertAsync(Entry("untagged", "untagged content", metadataJson: """{"tags":["personal"]}"""));

        var results = await store.SearchAsync("query text", 10, new MemorySearchFilter { Tags = ["ops"] });

        Assert.Single(results);
        Assert.Equal("tagged", results[0].Id);
    }

    [Fact]
    public async Task Search_ExcludesArchivedRows_OnTheVectorPath()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["archived content"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("archived", "archived content") with { IsArchived = true });

        var results = await store.SearchAsync("query text", 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_AppliesTemporalDecay_ToVectorOnlyMatches()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["fresh candidate"] = [1f, 0f, 0f, 0f],
            ["stale candidate"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.InsertAsync(Entry("stale", "stale candidate", now.AddDays(-365)));
        await store.InsertAsync(Entry("fresh", "fresh candidate", now));

        var results = await store.SearchAsync("query text", 10);

        Assert.Equal("fresh", results[0].Id);
    }

    [Fact]
    public async Task Search_KeepsExactLexicalMatchFirst_WhenVectorEvidenceIsWeak()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["kubernetes"] = [1f, 0f, 0f, 0f],
            ["kubernetes upgrade checklist"] = [0f, 1f, 0f, 0f],
            ["mildly related infrastructure note"] = [0.5f, 0f, 0.86f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("exact", "kubernetes upgrade checklist"));
        await store.InsertAsync(Entry("fuzzy", "mildly related infrastructure note"));

        var results = await store.SearchAsync("kubernetes", 10);

        Assert.Equal("exact", results[0].Id);
    }

    [Fact]
    public async Task Search_RespectsTopK_InHybridMode()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["query text"] = [1f, 0f, 0f, 0f],
            ["candidate a"] = [1f, 0f, 0f, 0f],
            ["candidate b"] = [0.9f, 0.43f, 0f, 0f],
            ["candidate c"] = [0.8f, 0.6f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("a", "candidate a"));
        await store.InsertAsync(Entry("b", "candidate b"));
        await store.InsertAsync(Entry("c", "candidate c"));

        var results = await store.SearchAsync("query text", 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Search_DoesNotDuplicate_RowsMatchedByBothSignals()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["deployment"] = [1f, 0f, 0f, 0f],
            ["the deployment pipeline failed"] = [1f, 0f, 0f, 0f]
        };
        await using var store = CreateStore(Embeddings(vectors));
        await store.InitializeAsync();
        await store.InsertAsync(Entry("m1", "the deployment pipeline failed"));

        var results = await store.SearchAsync("deployment", 10);

        Assert.Single(results);
        Assert.Equal("m1", results[0].Id);
    }
}
