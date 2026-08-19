using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Persistence.Seam.Tests.Conversations;

/// <summary>
/// Real on-disk SQLite database for conversation seam tests.
/// </summary>
/// <remarks>
/// The guarantees under test (the conditional <c>ON CONFLICT … WHERE version = $expectedVersion</c>
/// upsert, the narrow <c>UPDATE</c> statements, the <c>INSERT OR IGNORE</c> participant merge) live
/// in SQL. A mock or an in-memory double would re-implement them and therefore could not regress
/// them, which is precisely how the original webhook pin regression escaped. <c>Pooling=False</c>
/// keeps each store instance on its own connection so a "fresh store" verification read genuinely
/// goes to disk.
/// </remarks>
internal sealed class SeamStoreFixture : IDisposable
{
    public SeamStoreFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"botnexus-seam-{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={DatabasePath};Pooling=False";
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    /// <summary>Creates a store instance with its own cache — call twice to get two independent readers.</summary>
    public SqliteConversationStore CreateStore()
        => new(ConnectionString, NullLogger<SqliteConversationStore>.Instance);

    public void Dispose()
    {
        if (File.Exists(DatabasePath))
            File.Delete(DatabasePath);
    }
}
