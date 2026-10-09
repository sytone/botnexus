using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class SqliteSharedJournalModePolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "botnexus-shared-journal-mode-tests",
        Guid.NewGuid().ToString("N"));

    public SqliteSharedJournalModePolicyTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false, false, "wal")]
    [InlineData(false, true, "wal")]
    [InlineData(true, false, "delete")]
    [InlineData(true, true, "delete")]
    public async Task SharedDatabase_UsesFilesystemAwarePolicy_RegardlessOfInitializationOrder(
        bool isNetworkPath,
        bool initializeConversationFirst,
        string expectedMode)
    {
        var databasePath = Path.Combine(_directory, $"{Guid.NewGuid():N}.sqlite");
        var connectionString = $"Data Source={databasePath}";
        var detector = new Mock<INetworkPathDetector>(MockBehavior.Strict);
        detector.Setup(candidate => candidate.IsNetworkPath(databasePath)).Returns(isNetworkPath);
        var journalMode = new SqliteWalMaintenance(detector.Object);

        var conversations = new SqliteConversationStore(
            connectionString,
            NullLogger<SqliteConversationStore>.Instance,
            worldContext: null,
            journalModeMaintenance: journalMode);
        var sessions = new SqliteSessionStore(
            connectionString,
            NullLogger<SqliteSessionStore>.Instance,
            conversations,
            journalModeMaintenance: journalMode);

        if (initializeConversationFirst)
        {
            await conversations.ListAsync();
            await sessions.GetAsync(SessionId.From("missing"));
        }
        else
        {
            await sessions.GetAsync(SessionId.From("missing"));
            await conversations.ListAsync();
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var effectiveMode = (string?)await command.ExecuteScalarAsync();

        effectiveMode.ShouldBe(expectedMode);
        detector.Verify(candidate => candidate.IsNetworkPath(databasePath), Times.AtLeast(2));
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite may briefly retain a pooled file handle on Windows.
        }
    }
}
