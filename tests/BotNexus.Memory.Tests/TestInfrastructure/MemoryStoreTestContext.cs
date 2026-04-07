using BotNexus.Memory;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests.TestInfrastructure;

internal sealed class MemoryStoreTestContext : IAsyncDisposable
{
    private MemoryStoreTestContext(string tempDirectory, string dbPath, SqliteMemoryStore store)
    {
        TempDirectory = tempDirectory;
        DbPath = dbPath;
        Store = store;
    }

    public string TempDirectory { get; }
    public string DbPath { get; }
    public SqliteMemoryStore Store { get; }

    public static async Task<MemoryStoreTestContext> CreateAsync()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDirectory, "memory.db");
        var store = new SqliteMemoryStore(dbPath);
        await store.InitializeAsync();
        return new MemoryStoreTestContext(tempDirectory, dbPath, store);
    }

    public static MemoryEntry CreateEntry(
        string id,
        string agentId,
        string content,
        string sourceType = "conversation",
        string? sessionId = null,
        int? turnIndex = null,
        DateTimeOffset? createdAt = null,
        string? metadataJson = null)
    {
        return new MemoryEntry
        {
            Id = id,
            AgentId = agentId,
            SessionId = sessionId,
            TurnIndex = turnIndex,
            SourceType = sourceType,
            Content = content,
            MetadataJson = metadataJson,
            Embedding = null,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = null,
            ExpiresAt = null,
            IsArchived = false
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(TempDirectory))
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(TempDirectory, true);
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
