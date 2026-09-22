using System.IO.Abstractions;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests.Embeddings;

public sealed class SqliteMemoryStoreReembeddingTests : IAsyncLifetime
{
    private static readonly EmbeddingIdentity CurrentIdentity = new("current-model", "fp-current", 4);
    private static readonly EmbeddingIdentity OldIdentity = new("old-model", "fp-old", 4);

    private string _tempDirectory = string.Empty;
    private string _dbPath = string.Empty;

    public Task InitializeAsync()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "botnexus-reembedding-tests",
            Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDirectory, "memory.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureReembeddingJob_SameIdentity_PersistsSingleResumableJob()
    {
        var first = await WithStoreAsync(store => store.EnsureReembeddingJobAsync(CurrentIdentity));
        var second = await WithStoreAsync(store => store.EnsureReembeddingJobAsync(CurrentIdentity));
        var afterRestart = await WithStoreAsync(store => store.GetReembeddingJobAsync());

        second.JobId.ShouldBe(first.JobId);
        afterRestart.ShouldNotBeNull();
        afterRestart.JobId.ShouldBe(first.JobId);
        afterRestart.TargetIdentity.ShouldBe(CurrentIdentity);
        afterRestart.State.ShouldBe(ReembeddingJobState.Running);
    }

    [Fact]
    public async Task EnsureReembeddingJob_ClassifiesOnlyLiveNonMatchingRowsAsPending()
    {
        await WithStoreAsync(async store =>
        {
            await store.InsertAsync(Entry("null", createdAt: AtMinute(1)));
            await store.InsertAsync(Entry("corrupt", createdAt: AtMinute(2)) with { Embedding = [0x01, 0x02] });
            await store.InsertAsync(Entry("mismatched", createdAt: AtMinute(3)) with
            {
                Embedding = EmbeddingBlob.Encode(OldIdentity, [1f, 0f, 0f, 0f])
            });
            await store.InsertAsync(Entry("covered", createdAt: AtMinute(4)) with
            {
                Embedding = EmbeddingBlob.Encode(CurrentIdentity, [0f, 1f, 0f, 0f])
            });
            await store.InsertAsync(Entry("archived", createdAt: AtMinute(5)) with
            {
                IsArchived = true,
                Embedding = null
            });

            var job = await store.EnsureReembeddingJobAsync(CurrentIdentity);

            job.TotalCount.ShouldBe(4);
            job.CoveredCount.ShouldBe(1);
            job.PendingCount.ShouldBe(3);
            job.FailedCount.ShouldBe(0);
        });
    }

    [Fact]
    public async Task ClaimReembeddingBatch_BoundsAndOrdersClaimsDeterministically()
    {
        await WithStoreAsync(async store =>
        {
            await store.InsertAsync(Entry("third", createdAt: AtMinute(3)));
            await store.InsertAsync(Entry("first", createdAt: AtMinute(1)));
            await store.InsertAsync(Entry("second", createdAt: AtMinute(2)));
            var job = await store.EnsureReembeddingJobAsync(CurrentIdentity);

            var firstClaim = await store.ClaimReembeddingBatchAsync(job.JobId, batchSize: 2);
            var secondClaim = await store.ClaimReembeddingBatchAsync(job.JobId, batchSize: 2);

            firstClaim.Select(item => item.MemoryId).ShouldBe(["first", "second"]);
            firstClaim.Select(item => item.Content).ShouldBe(["content-first", "content-second"]);
            secondClaim.Select(item => item.MemoryId).ShouldBe(["third"]);
        });
    }

    [Fact]
    public async Task CompleteReembeddingItem_UpdatesOnlyEmbeddingAndProgress()
    {
        var createdAt = AtMinute(1);
        var updatedAt = AtMinute(2);
        var original = Entry("preserve", createdAt) with
        {
            AgentId = "agent-preserved",
            SessionId = "session-preserved",
            TurnIndex = 17,
            SourceType = "manual",
            Content = "content that must not change",
            MetadataJson = """{"tags":["keep"]}""",
            UpdatedAt = updatedAt,
            ExpiresAt = AtMinute(30),
            Provenance = MemoryProvenance.User,
            OriginConversationId = "conversation-origin",
            OriginSessionId = "session-origin"
        };
        var replacement = EmbeddingBlob.Encode(CurrentIdentity, [0f, 0f, 1f, 0f]);

        await WithStoreAsync(async store =>
        {
            await store.InsertAsync(original);
            var job = await store.EnsureReembeddingJobAsync(CurrentIdentity);
            var claimed = await store.ClaimReembeddingBatchAsync(job.JobId, batchSize: 1);

            await store.CompleteReembeddingItemAsync(job.JobId, claimed[0].MemoryId, claimed[0].Revision, replacement);

            var actual = await store.GetByIdAsync(original.Id);
            actual.ShouldNotBeNull();
            actual.Id.ShouldBe(original.Id);
            actual.AgentId.ShouldBe(original.AgentId);
            actual.SessionId.ShouldBe(original.SessionId);
            actual.TurnIndex.ShouldBe(original.TurnIndex);
            actual.SourceType.ShouldBe(original.SourceType);
            actual.Content.ShouldBe(original.Content);
            actual.MetadataJson.ShouldBe(original.MetadataJson);
            actual.Embedding.ShouldBe(replacement);
            actual.CreatedAt.ShouldBe(original.CreatedAt);
            actual.UpdatedAt.ShouldBe(original.UpdatedAt);
            actual.ExpiresAt.ShouldBe(original.ExpiresAt);
            actual.IsArchived.ShouldBe(original.IsArchived);
            actual.Provenance.ShouldBe(original.Provenance);
            actual.OriginConversationId.ShouldBe(original.OriginConversationId);
            actual.OriginSessionId.ShouldBe(original.OriginSessionId);

            var progress = await store.GetReembeddingJobAsync();
            progress.ShouldNotBeNull();
            progress.CoveredCount.ShouldBe(1);
            progress.PendingCount.ShouldBe(0);
            progress.FailedCount.ShouldBe(0);
        });
    }

    [Fact]
    public async Task CompleteReembeddingItem_ContentChangedAfterClaim_DoesNotAttachStaleVector()
    {
        var replacement = EmbeddingBlob.Encode(CurrentIdentity, [0f, 0f, 1f, 0f]);

        await WithStoreAsync(async store =>
        {
            await store.InsertAsync(Entry("changed"));
            var job = await store.EnsureReembeddingJobAsync(CurrentIdentity);
            var claim = (await store.ClaimReembeddingBatchAsync(job.JobId, batchSize: 1)).ShouldHaveSingleItem();
            claim.Revision.ShouldBe(1);
            _ = await store.UpdateAsync("changed", 1, new MemoryUpdate { Content = "new content" });

            await store.CompleteReembeddingItemAsync(job.JobId, claim.MemoryId, claim.Revision, replacement);

            var actual = await store.GetByIdAsync("changed");
            actual.ShouldNotBeNull();
            actual.Content.ShouldBe("new content");
            actual.Embedding.ShouldBeNull();
            actual.EmbeddingStatus.ShouldNotBe("ready");
        });
    }

    [Fact]
    public async Task FailReembeddingItem_PersistsFailureAndMakesRowRetryableAfterRestart()
    {
        string jobId = string.Empty;
        await WithStoreAsync(async store =>
        {
            await store.InsertAsync(Entry("retry-me", AtMinute(1)));
            var job = await store.EnsureReembeddingJobAsync(CurrentIdentity);
            jobId = job.JobId;
            var claimed = await store.ClaimReembeddingBatchAsync(job.JobId, batchSize: 1);

            await store.FailReembeddingItemAsync(job.JobId, claimed[0].MemoryId, "provider unavailable");
        });

        await WithStoreAsync(async store =>
        {
            var persisted = await store.GetReembeddingJobAsync();
            persisted.ShouldNotBeNull();
            persisted.JobId.ShouldBe(jobId);
            persisted.FailedCount.ShouldBe(1);
            persisted.LastError.ShouldBe("provider unavailable");

            var retry = await store.ClaimReembeddingBatchAsync(jobId, batchSize: 1);
            retry.ShouldHaveSingleItem().MemoryId.ShouldBe("retry-me");
            retry[0].FailureCount.ShouldBe(1);
        });
    }

    [Fact]
    public async Task PauseResumeCancel_ArePersistedAndIdempotent()
    {
        var job = await WithStoreAsync(store => store.EnsureReembeddingJobAsync(CurrentIdentity));

        await WithStoreAsync(async store =>
        {
            await store.PauseReembeddingJobAsync(job.JobId);
            await store.PauseReembeddingJobAsync(job.JobId);
        });

        var paused = await WithStoreAsync(store => store.GetReembeddingJobAsync());
        paused.ShouldNotBeNull();
        paused.State.ShouldBe(ReembeddingJobState.Paused);

        await WithStoreAsync(async store =>
        {
            await store.ResumeReembeddingJobAsync(job.JobId);
            await store.ResumeReembeddingJobAsync(job.JobId);
        });

        var resumed = await WithStoreAsync(store => store.GetReembeddingJobAsync());
        resumed.ShouldNotBeNull();
        resumed.State.ShouldBe(ReembeddingJobState.Running);

        await WithStoreAsync(async store =>
        {
            await store.CancelReembeddingJobAsync(job.JobId);
            await store.CancelReembeddingJobAsync(job.JobId);
        });

        var cancelled = await WithStoreAsync(store => store.GetReembeddingJobAsync());
        cancelled.ShouldNotBeNull();
        cancelled.State.ShouldBe(ReembeddingJobState.Cancelled);
    }

    private async Task<T> WithStoreAsync<T>(Func<SqliteMemoryStore, Task<T>> action)
    {
        await using var store = new SqliteMemoryStore(_dbPath, new FileSystem());
        await store.InitializeAsync();
        return await action(store);
    }

    private async Task WithStoreAsync(Func<SqliteMemoryStore, Task> action)
    {
        await using var store = new SqliteMemoryStore(_dbPath, new FileSystem());
        await store.InitializeAsync();
        await action(store);
    }

    private static DateTimeOffset AtMinute(int minute)
        => new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);

    private static MemoryEntry Entry(string id, DateTimeOffset? createdAt = null)
        => new()
        {
            Id = id,
            AgentId = "agent",
            SourceType = "conversation",
            Content = $"content-{id}",
            CreatedAt = createdAt ?? AtMinute(0)
        };
}
