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

    public void Dispose()
    {
        if (File.Exists(DatabasePath))
            File.Delete(DatabasePath);
    }

    private static string TempDb()
        => Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}.db");
}
