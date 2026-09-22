using BotNexus.Memory.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using System.IO.Abstractions;

namespace BotNexus.Memory.Tests;

public sealed class SqliteMemoryStoreMutationTests
{
    [Fact]
    public async Task InsertAsync_NewRecord_StartsAtRevisionOneAndRoundTripsDurableFields()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        var entry = MemoryStoreTestContext.CreateEntry("durable", "agent-a", "durable content") with
        {
            Role = "curated",
            Category = "preference",
            TagsJson = "[\"one\",\"two\"]",
            CorrectsId = "mistake",
            SupersedesId = "older",
            SupersededById = "newer",
            OriginKind = "daily-note",
            OriginReference = "memory/2026-09-01.md#item-1",
            EmbeddingStatus = "pending"
        };

        var inserted = await context.Store.InsertAsync(entry);
        var loaded = await context.Store.GetByIdAsync(entry.Id);

        inserted.Revision.ShouldBe(1);
        loaded.ShouldNotBeNull();
        loaded!.Revision.ShouldBe(1);
        loaded.Role.ShouldBe("curated");
        loaded.Category.ShouldBe("preference");
        loaded.TagsJson.ShouldBe("[\"one\",\"two\"]");
        loaded.CorrectsId.ShouldBe("mistake");
        loaded.SupersedesId.ShouldBe("older");
        loaded.SupersededById.ShouldBe("newer");
        loaded.OriginKind.ShouldBe("daily-note");
        loaded.OriginReference.ShouldBe("memory/2026-09-01.md#item-1");
        loaded.EmbeddingStatus.ShouldBe("pending");
    }

    [Fact]
    public async Task UpdateAsync_ExpectedRevision_ReplacesContentAndFtsTerms()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("update", "agent-a", "olduniqueterm"));

        var result = await context.Store.UpdateAsync(
            "update",
            expectedRevision: 1,
            new MemoryUpdate { Content = "newuniqueterm", Category = "fact", TagsJson = "[\"fresh\"]" });

        result.Status.ShouldBe(MemoryMutationStatus.Applied);
        result.Entry.ShouldNotBeNull();
        result.Entry!.Revision.ShouldBe(2);
        result.Entry.Content.ShouldBe("newuniqueterm");
        result.Entry.Category.ShouldBe("fact");
        result.Entry.TagsJson.ShouldBe("[\"fresh\"]");
        (await context.Store.SearchAsync("olduniqueterm")).ShouldBeEmpty();
        (await context.Store.SearchAsync("newuniqueterm")).ShouldHaveSingleItem().Id.ShouldBe("update");
        (await CountFtsMatchesAsync(context.DbPath, "olduniqueterm")).ShouldBe(0);
        (await CountFtsMatchesAsync(context.DbPath, "newuniqueterm")).ShouldBe(1);
    }

    [Fact]
    public async Task ArchiveAsync_ExpectedRevision_ExcludesEntryFromLiveSearch()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("archive", "agent-a", "archiveuniqueterm"));

        var result = await context.Store.ArchiveAsync("archive", expectedRevision: 1);

        result.Status.ShouldBe(MemoryMutationStatus.Applied);
        result.Entry.ShouldNotBeNull();
        result.Entry!.Revision.ShouldBe(2);
        result.Entry.IsArchived.ShouldBeTrue();
        result.Entry.ArchivedAt.ShouldNotBeNull();
        (await context.Store.SearchAsync("archiveuniqueterm")).ShouldBeEmpty();
        (await CountFtsMatchesAsync(context.DbPath, "archiveuniqueterm")).ShouldBe(0);
    }

    [Fact]
    public async Task UpdateAsync_StaleRevision_DoesNotMutateRecordOrFts()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("stale", "agent-a", "originaluniqueterm"));
        _ = await context.Store.UpdateAsync("stale", 1, new MemoryUpdate { Content = "currentuniqueterm" });

        var stale = await context.Store.UpdateAsync("stale", 1, new MemoryUpdate { Content = "staleuniqueterm" });

        stale.Status.ShouldBe(MemoryMutationStatus.RevisionConflict);
        stale.Entry.ShouldNotBeNull();
        stale.Entry!.Revision.ShouldBe(2);
        stale.Entry.Content.ShouldBe("currentuniqueterm");
        (await context.Store.SearchAsync("staleuniqueterm")).ShouldBeEmpty();
        (await context.Store.SearchAsync("currentuniqueterm")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task UpdateAsync_ExplicitNull_ClearsNullableSemanticFieldsAndEmbeddingState()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("clear", "agent-a", "unchanged") with
        {
            Role = "role",
            Category = "category",
            TagsJson = "[\"tag\"]",
            CorrectsId = "corrects",
            SupersedesId = "supersedes",
            SupersededById = "superseded-by",
            Embedding = [1, 2, 3],
            EmbeddingStatus = "ready"
        });

        var result = await context.Store.UpdateAsync("clear", 1, new MemoryUpdate
        {
            Role = MemoryUpdateValue<string?>.Replace(null),
            Category = MemoryUpdateValue<string?>.Replace(null),
            TagsJson = MemoryUpdateValue<string?>.Replace(null),
            CorrectsId = MemoryUpdateValue<string?>.Replace(null),
            SupersedesId = MemoryUpdateValue<string?>.Replace(null),
            SupersededById = MemoryUpdateValue<string?>.Replace(null),
            EmbeddingStatus = MemoryUpdateValue<string?>.Replace(null)
        });

        result.Status.ShouldBe(MemoryMutationStatus.Applied);
        result.Entry.ShouldNotBeNull();
        result.Entry.Role.ShouldBeNull();
        result.Entry.Category.ShouldBeNull();
        result.Entry.TagsJson.ShouldBeNull();
        result.Entry.CorrectsId.ShouldBeNull();
        result.Entry.SupersedesId.ShouldBeNull();
        result.Entry.SupersededById.ShouldBeNull();
        result.Entry.Embedding.ShouldBe([1, 2, 3]);
        result.Entry.EmbeddingStatus.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateAsync_ContentChange_ClearsEmbeddingAndReadyStatus()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("content", "agent-a", "before") with
        {
            Embedding = [1, 2, 3],
            EmbeddingStatus = "ready"
        });

        var result = await context.Store.UpdateAsync("content", 1, new MemoryUpdate { Content = "after" });

        result.Status.ShouldBe(MemoryMutationStatus.Applied);
        result.Entry.ShouldNotBeNull();
        result.Entry.Embedding.ShouldBeNull();
        result.Entry.EmbeddingStatus.ShouldNotBe("ready");
    }

    [Fact]
    public async Task UpdateAsync_TwoIndependentStoresWithSameRevision_ReturnsAppliedAndRevisionConflict()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await using var secondStore = new SqliteMemoryStore(context.DbPath, new FileSystem());
        await secondStore.InitializeAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("race", "agent-a", "before"));

        var first = context.Store.UpdateAsync("race", 1, new MemoryUpdate { Content = "first" });
        var second = secondStore.UpdateAsync("race", 1, new MemoryUpdate { Content = "second" });
        var results = await Task.WhenAll(first, second);

        results.Count(result => result.Status == MemoryMutationStatus.Applied).ShouldBe(1);
        results.Count(result => result.Status == MemoryMutationStatus.RevisionConflict).ShouldBe(1);
        results.Single(result => result.Status == MemoryMutationStatus.RevisionConflict).Entry.ShouldNotBeNull();
    }

    [Fact]
    public async Task ArchiveAsync_AlreadyArchived_ReturnsAlreadyArchivedWithoutChangingTimestampOrRevision()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("archive-twice", "agent-a", "content"));
        var first = await context.Store.ArchiveAsync("archive-twice", 1);

        var second = await context.Store.ArchiveAsync("archive-twice", first.Entry!.Revision);

        second.Status.ShouldBe(MemoryMutationStatus.AlreadyArchived);
        second.Entry.ShouldNotBeNull();
        second.Entry.ArchivedAt.ShouldBe(first.Entry.ArchivedAt);
        second.Entry.Revision.ShouldBe(first.Entry.Revision);
    }

    [Fact]
    public async Task DeleteAsync_ExpectedRevision_RemovesRecordAndFtsEntry()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("delete", "agent-a", "deleteuniqueterm"));

        var result = await context.Store.DeleteAsync("delete", expectedRevision: 1);

        result.Status.ShouldBe(MemoryMutationStatus.Applied);
        (await context.Store.GetByIdAsync("delete")).ShouldBeNull();
        (await context.Store.SearchAsync("deleteuniqueterm")).ShouldBeEmpty();
        (await CountFtsMatchesAsync(context.DbPath, "deleteuniqueterm")).ShouldBe(0);
    }

    [Fact]
    public async Task DeleteAsync_StaleRevision_DoesNotDeleteRecord()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("keep", "agent-a", "keepuniqueterm"));
        _ = await context.Store.UpdateAsync("keep", 1, new MemoryUpdate { Content = "keepuniqueterm changed" });

        var result = await context.Store.DeleteAsync("keep", expectedRevision: 1);

        result.Status.ShouldBe(MemoryMutationStatus.RevisionConflict);
        result.Entry.ShouldNotBeNull();
        result.Entry!.Revision.ShouldBe(2);
        (await context.Store.GetByIdAsync("keep")).ShouldNotBeNull();
    }

    [Fact]
    public async Task InitializeAsync_PreExistingSchema_AddsNullableFieldsWithoutFabricatingMeaning()
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(directory, "memory.db");
        Directory.CreateDirectory(directory);
        await CreateOldSchemaAsync(dbPath);

        try
        {
            await using var store = new SqliteMemoryStore(dbPath, new FileSystem());
            await store.InitializeAsync();
            var loaded = await store.GetByIdAsync("legacy");

            loaded.ShouldNotBeNull();
            loaded!.Revision.ShouldBe(1);
            loaded.Role.ShouldBeNull();
            loaded.Category.ShouldBeNull();
            loaded.TagsJson.ShouldBeNull();
            loaded.Provenance.ShouldBeNull();
            loaded.NormalizedProvenance.ShouldBe(MemoryProvenance.Unknown);
            loaded.CorrectsId.ShouldBeNull();
            loaded.SupersedesId.ShouldBeNull();
            loaded.SupersededById.ShouldBeNull();
            loaded.OriginKind.ShouldBeNull();
            loaded.OriginReference.ShouldBeNull();
            loaded.EmbeddingStatus.ShouldBeNull();
            (await ReadSchemaVersionAsync(dbPath)).ShouldBe(2);
            (await CountFtsMatchesAsync(dbPath, "archivedlegacyterm")).ShouldBe(0);
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolFor(dbPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_TwoStoresUpgradeLegacySchemaConcurrently_CompletesOneAtomicTransition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(directory, "memory.db");
        Directory.CreateDirectory(directory);
        await CreateOldSchemaAsync(dbPath);

        try
        {
            await using var first = new SqliteMemoryStore(dbPath, new FileSystem());
            await using var second = new SqliteMemoryStore(dbPath, new FileSystem());
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var firstInitialization = InitializeAfterSignalAsync(first, start.Task);
            var secondInitialization = InitializeAfterSignalAsync(second, start.Task);
            start.SetResult();
            await Task.WhenAll(firstInitialization, secondInitialization);

            (await ReadSchemaVersionAsync(dbPath)).ShouldBe(2);
            (await CountFtsMatchesAsync(dbPath, "legacy")).ShouldBe(1);
            (await CountFtsMatchesAsync(dbPath, "archivedlegacyterm")).ShouldBe(0);

            var inserted = await first.InsertAsync(
                MemoryStoreTestContext.CreateEntry("after-upgrade", "agent-a", "afterupgradeuniqueterm"));
            inserted.Revision.ShouldBe(1);
            (await second.SearchAsync("afterupgradeuniqueterm")).ShouldHaveSingleItem().Id.ShouldBe("after-upgrade");
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolFor(dbPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task InitializeAfterSignalAsync(SqliteMemoryStore store, Task start)
    {
        await start;
        await store.InitializeAsync();
    }

    private static async Task<int> CountFtsMatchesAsync(string dbPath, string query)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memories_fts WHERE memories_fts MATCH $query";
        command.Parameters.AddWithValue("$query", query);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }


    private static async Task<int> ReadSchemaVersionAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task CreateOldSchemaAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE memories (
                rowid INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                agent_id TEXT NOT NULL,
                session_id TEXT NULL,
                turn_index INTEGER NULL,
                source_type TEXT NOT NULL,
                content TEXT NOT NULL,
                metadata_json TEXT NULL,
                embedding BLOB NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NULL,
                expires_at TEXT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0
            );
            CREATE VIRTUAL TABLE memories_fts USING fts5(content, content='memories', content_rowid='rowid');
            CREATE TRIGGER memories_ai AFTER INSERT ON memories BEGIN
                INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
            END;
            CREATE TRIGGER memories_ad AFTER DELETE ON memories BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
            END;
            CREATE TRIGGER memories_au AFTER UPDATE ON memories BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, content) VALUES('delete', old.rowid, old.content);
                INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
            END;
            INSERT INTO memories (id, agent_id, source_type, content, created_at)
            VALUES ('legacy', 'agent-a', 'conversation', 'legacy content', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO memories (id, agent_id, source_type, content, created_at, is_archived)
            VALUES ('legacy-archived', 'agent-a', 'conversation', 'archivedlegacyterm', '2026-01-01T00:00:00.0000000+00:00', 1);
            CREATE TABLE schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version(version) VALUES (1);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
