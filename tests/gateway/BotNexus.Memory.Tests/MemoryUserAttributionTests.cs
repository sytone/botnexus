using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Covers the additive <c>user_id</c> column: per-person attribution recorded from now on, ahead of
/// any filtering. The value of capturing it early is that the rows written before it exist can
/// never be attributed retrospectively, so the column has to start collecting before the filter is
/// wanted, not with it.
/// </summary>
public sealed class MemoryUserAttributionTests
{
    [Fact]
    public async Task UserId_RoundTrips()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("u1", "agent-a", "attributed content") with
            {
                UserId = "person-a"
            });

        var entry = await context.Store.GetByIdAsync("u1");

        entry.ShouldNotBeNull();
        entry!.UserId.ShouldBe("person-a");
    }

    [Fact]
    public async Task UserId_IsNullWhenUnattributed()
    {
        // Cron, compaction and dreaming writes have no human to attribute. Null is the honest
        // record; anything that later filters on this column has to decide what null means rather
        // than assume every row has an owner.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("u2", "agent-a", "cron output"));

        var entry = await context.Store.GetByIdAsync("u2");

        entry.ShouldNotBeNull();
        entry!.UserId.ShouldBeNull();
    }

    [Fact]
    public async Task Search_IsNotYetFilteredByUserId()
    {
        // Deliberate, and pinned so it cannot change by accident. Enforcement is a product
        // decision that has not been taken; if someone adds the predicate, this test should fail
        // and make them say so rather than silently changing what every agent can recall.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("a", "agent-a", "attributiontoken alpha") with { UserId = "person-a" });
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("b", "agent-a", "attributiontoken beta") with { UserId = "person-b" });
        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("c", "agent-a", "attributiontoken gamma"));

        var results = await context.Store.SearchAsync("attributiontoken", topK: 10);

        results.Select(r => r.Id).ShouldBe(["a", "b", "c"], ignoreOrder: true);
    }

    /// <summary>
    /// The realistic upgrade path for a deployed store: it already has the provenance trio from
    /// #2480 but predates <c>user_id</c>. It must open, gain the column, and keep its rows.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_OnPreUserIdDatabase_OpensAndUpgrades()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "pre-userid.db");

        try
        {
            await CreatePreUserIdDatabaseAsync(dbPath);

            var store = new SqliteMemoryStore(dbPath);
            await store.InitializeAsync();

            var legacy = await store.GetByIdAsync("legacy-1");
            legacy.ShouldNotBeNull();
            legacy!.Content.ShouldBe("written before user attribution existed");
            // Not invented after the fact: an unattributable row stays unattributed.
            legacy.UserId.ShouldBeNull();
            legacy.Provenance.ShouldBe("agent");

            await store.InsertAsync(
                MemoryStoreTestContext.CreateEntry("new-1", "agent-1", "written after the upgrade") with
                {
                    UserId = "person-a"
                });
            (await store.GetByIdAsync("new-1"))!.UserId.ShouldBe("person-a");

            // Idempotent: opening the same file again must not fail on a duplicate column.
            var second = new SqliteMemoryStore(dbPath);
            await second.InitializeAsync();
            (await second.GetByIdAsync("legacy-1")).ShouldNotBeNull();

            await store.DisposeAsync();
            await second.DisposeAsync();
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolFor(dbPath);
            if (Directory.Exists(tempDirectory))
            {
                // TestAwait.EventuallyAsync rather than a retry loop around Task.Delay: the wait is
                // for the OS to release the file handle, which is a condition to observe, not a
                // duration to guess at. Same helper MemoryStoreTestContext.DisposeAsync uses.
                await TestAwait.EventuallyAsync(
                    () =>
                    {
                        try
                        {
                            Directory.Delete(tempDirectory, recursive: true);
                            return true;
                        }
                        catch (IOException)
                        {
                            return false;
                        }
                    },
                    $"temporary memory store directory '{tempDirectory}' to be deletable",
                    timeout: TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>
    /// Builds the schema as it stood after #2480 and before <c>user_id</c> — provenance columns
    /// present, attribution absent — so the migration is exercised against a real intermediate
    /// file rather than a simulated one.
    /// </summary>
    private static async Task CreatePreUserIdDatabaseAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
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
                is_archived INTEGER NOT NULL DEFAULT 0,
                provenance TEXT NULL,
                origin_conversation_id TEXT NULL,
                origin_session_id TEXT NULL
            );

            INSERT INTO memories (id, agent_id, source_type, content, created_at, is_archived, provenance)
            VALUES ('legacy-1', 'agent-1', 'manual', 'written before user attribution existed',
                    '2026-01-01T00:00:00.0000000+00:00', 0, 'agent');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
