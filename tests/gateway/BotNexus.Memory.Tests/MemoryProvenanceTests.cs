using BotNexus.Memory;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Covers the provenance metadata added in #2480: the closed vocabulary and its fail-safe
/// normalisation, round-tripping the additive columns, and - the explicit acceptance point -
/// that a DB created before provenance existed still opens and reads back successfully.
/// </summary>
public sealed class MemoryProvenanceTests
{
    [Theory]
    [InlineData("agent", MemoryProvenance.Agent)]
    [InlineData("USER", MemoryProvenance.User)]
    [InlineData("  tool  ", MemoryProvenance.Tool)]
    [InlineData("External-Untrusted", MemoryProvenance.ExternalUntrusted)]
    public void Normalize_KnownValue_ReturnsCanonicalForm(string input, string expected)
        => MemoryProvenance.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trusted")]
    [InlineData("agent; drop table")]
    public void Normalize_UnknownOrMalformedValue_FailsSafeToUnknown(string? input)
        => MemoryProvenance.Normalize(input).ShouldBe(MemoryProvenance.Unknown);

    [Theory]
    [InlineData(MemoryProvenance.Agent, true)]
    [InlineData(MemoryProvenance.User, true)]
    [InlineData(MemoryProvenance.Tool, true)]
    [InlineData(MemoryProvenance.ExternalUntrusted, false)]
    [InlineData(MemoryProvenance.Unknown, false)]
    [InlineData("nonsense", false)]
    [InlineData(null, false)]
    public void IsFirstParty_TreatsUntrustedAndUnknownAsNotFirstParty(string? provenance, bool expected)
        => MemoryProvenance.IsFirstParty(provenance).ShouldBe(expected);

    [Fact]
    public void NormalizedProvenance_OnEntryWithNoProvenance_IsUnknown()
    {
        var entry = MemoryStoreTestContext.CreateEntry("id-1", "agent-1", "content");

        entry.Provenance.ShouldBeNull();
        entry.NormalizedProvenance.ShouldBe(MemoryProvenance.Unknown);
    }

    [Fact]
    public async Task InsertAsync_RoundTripsProvenanceAndOriginIds()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var entry = MemoryStoreTestContext.CreateEntry("prov-1", "agent-1", "an issue body summary") with
        {
            Provenance = MemoryProvenance.ExternalUntrusted,
            OriginConversationId = "conv-42",
            OriginSessionId = "sess-42"
        };
        await context.Store.InsertAsync(entry);

        var stored = await context.Store.GetByIdAsync("prov-1");

        stored.ShouldNotBeNull();
        stored!.Provenance.ShouldBe(MemoryProvenance.ExternalUntrusted);
        stored.NormalizedProvenance.ShouldBe(MemoryProvenance.ExternalUntrusted);
        stored.OriginConversationId.ShouldBe("conv-42");
        stored.OriginSessionId.ShouldBe("sess-42");
        MemoryProvenance.IsFirstParty(stored.Provenance).ShouldBeFalse();
    }

    [Fact]
    public async Task InsertAsync_WithMalformedProvenance_PersistsTheFailSafeUnknown()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        var entry = MemoryStoreTestContext.CreateEntry("prov-2", "agent-1", "content") with
        {
            Provenance = "definitely-trusted-honest"
        };
        await context.Store.InsertAsync(entry);

        var stored = await context.Store.GetByIdAsync("prov-2");

        stored.ShouldNotBeNull();
        // The invented value must not survive to a later trust decision.
        stored!.Provenance.ShouldBe(MemoryProvenance.Unknown);
        MemoryProvenance.IsFirstParty(stored.Provenance).ShouldBeFalse();
    }

    [Fact]
    public async Task SearchAsync_SurfacesProvenanceOnRecall()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();

        await context.Store.InsertAsync(
            MemoryStoreTestContext.CreateEntry("prov-3", "agent-1", "quantum widget calibration") with
            {
                Provenance = MemoryProvenance.ExternalUntrusted
            });

        var results = await context.Store.SearchAsync("quantum widget", 5);

        results.ShouldHaveSingleItem();
        results[0].NormalizedProvenance.ShouldBe(MemoryProvenance.ExternalUntrusted);
    }

    /// <summary>
    /// The explicit acceptance point of #2480: a store file created before the provenance columns
    /// existed must open, upgrade lazily, and read its legacy rows back - never be rejected.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_OnPreProvenanceDatabase_OpensAndBackfillsLazily()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "legacy-memory.db");

        try
        {
            await CreatePreProvenanceDatabaseAsync(dbPath);

            var store = new SqliteMemoryStore(dbPath);
            await store.InitializeAsync();

            var legacy = await store.GetByIdAsync("legacy-1");
            legacy.ShouldNotBeNull();
            legacy!.Content.ShouldBe("written before provenance existed");
            // Not backfilled to a trusted value: NULL is the honest record, and it reads as unknown.
            legacy.Provenance.ShouldBeNull();
            legacy.NormalizedProvenance.ShouldBe(MemoryProvenance.Unknown);
            MemoryProvenance.IsFirstParty(legacy.Provenance).ShouldBeFalse();

            // The upgraded schema must accept new provenance-bearing writes.
            await store.InsertAsync(
                MemoryStoreTestContext.CreateEntry("new-1", "agent-1", "written after the upgrade") with
                {
                    Provenance = MemoryProvenance.Agent
                });
            var upgraded = await store.GetByIdAsync("new-1");
            upgraded.ShouldNotBeNull();
            upgraded!.Provenance.ShouldBe(MemoryProvenance.Agent);

            // Idempotent: a second initialize of the same file must not fail on duplicate columns.
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
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await Task.Delay(50);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds the exact pre-#2480 <c>memories</c> schema - no provenance columns at all - so the
    /// migration test exercises a real legacy file rather than a simulated one.
    /// </summary>
    private static async Task CreatePreProvenanceDatabaseAsync(string dbPath)
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
                is_archived INTEGER NOT NULL DEFAULT 0
            );

            INSERT INTO memories (id, agent_id, source_type, content, created_at, is_archived)
            VALUES ('legacy-1', 'agent-1', 'conversation', 'written before provenance existed', '2024-01-01T00:00:00.0000000+00:00', 0);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
