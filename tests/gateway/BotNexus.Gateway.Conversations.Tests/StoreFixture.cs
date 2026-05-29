using BotNexus.Domain;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>Shared test fixture that creates a temporary SQLite database for conversation store tests.</summary>
internal sealed class StoreFixture : IDisposable
{
    public StoreFixture()
    {
        DatabasePath = TempDb();
        ConnectionString = $"Data Source={DatabasePath};Pooling=False";
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public SqliteConversationStore CreateStore()
        => new(ConnectionString, NullLogger<SqliteConversationStore>.Instance);

    public SqliteConversationStore CreateStore(IWorldContext worldContext)
        => new(ConnectionString, NullLogger<SqliteConversationStore>.Instance, worldContext);

    public void Dispose()
    {
        if (File.Exists(DatabasePath))
            File.Delete(DatabasePath);
    }

    private static string TempDb()
        => Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}.db");
}

/// <summary>
/// Deterministic <see cref="IWorldContext"/> for tests. Mutable: tests can flip the world id
/// mid-execution to distinguish save-time stamping from read-time backfill (a fixed id would
/// let a missed save-stamp silently pass because the read-side backfill would refill it with
/// the same value).
/// </summary>
internal sealed class FakeWorldContext : IWorldContext
{
    public FakeWorldContext(string worldId = "test-world", string? name = null)
    {
        Current = new WorldIdentity { Id = worldId, Name = name ?? worldId };
    }

    public WorldIdentity Current { get; set; }

    public void Set(string worldId, string? name = null)
        => Current = new WorldIdentity { Id = worldId, Name = name ?? worldId };
}
