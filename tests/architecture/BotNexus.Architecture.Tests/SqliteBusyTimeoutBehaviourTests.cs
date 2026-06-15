using System.Data;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Behavioural verification that the <c>StateChange</c>-based per-open <c>busy_timeout</c> pattern
/// used by the fresh-connection SQLite stores (#1450) actually takes effect at runtime - i.e. a
/// connection produced by that pattern reports the configured <c>busy_timeout</c>, on every open,
/// not just on the first.
///
/// This complements <see cref="SqliteBusyTimeoutArchitectureTests"/> (which guards that the PRAGMA
/// is present in source) by proving the chosen mechanism is not a silent no-op. It mirrors the exact
/// shape stores use: <c>new SqliteConnection(cs)</c> with a <c>StateChange</c> handler that runs
/// <c>PRAGMA busy_timeout</c> when the connection transitions to Open.
/// </summary>
public sealed class SqliteBusyTimeoutBehaviourTests
{
    private const int BusyTimeoutMs = 5000;

    private static SqliteConnection CreateConnectionLikeStore(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
                pragma.ExecuteNonQuery();
            }
        };
        return connection;
    }

    private static async Task<long> ReadBusyTimeoutAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA busy_timeout;";
        var value = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(value);
    }

    [Fact]
    public async Task StateChangeHandler_AppliesBusyTimeout_OnOpen()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-busytimeout-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        try
        {
            await using var connection = CreateConnectionLikeStore(cs);
            await connection.OpenAsync(CancellationToken.None);

            var busyTimeout = await ReadBusyTimeoutAsync(connection, CancellationToken.None);
            busyTimeout.ShouldBe(BusyTimeoutMs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task StateChangeHandler_AppliesBusyTimeout_OnEveryFreshConnection()
    {
        // The whole point of #1450: busy_timeout is per-connection and resets to 0 on each open.
        // A fresh connection (as stores do per operation) must ALSO get the timeout applied, not
        // only the first/init connection.
        var dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-busytimeout-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        try
        {
            // First (init-like) connection.
            await using (var first = CreateConnectionLikeStore(cs))
            {
                await first.OpenAsync(CancellationToken.None);
                (await ReadBusyTimeoutAsync(first, CancellationToken.None)).ShouldBe(BusyTimeoutMs);
            }

            // Second, completely fresh connection (per-operation pattern). With pooling off this is a
            // brand-new connection whose busy_timeout would be 0 without the per-open handler.
            await using (var second = CreateConnectionLikeStore(cs))
            {
                await second.OpenAsync(CancellationToken.None);
                (await ReadBusyTimeoutAsync(second, CancellationToken.None)).ShouldBe(BusyTimeoutMs);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task FreshConnection_WithoutHandler_HasZeroBusyTimeout()
    {
        // Baseline / vacuity: prove the default really is 0, so the above assertions are meaningful
        // (i.e. the handler is what produces 5000, not some ambient default).
        var dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-busytimeout-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        try
        {
            await using var bare = new SqliteConnection(cs);
            await bare.OpenAsync(CancellationToken.None);

            var busyTimeout = await ReadBusyTimeoutAsync(bare, CancellationToken.None);
            busyTimeout.ShouldBe(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite file locks can linger briefly on Windows.
        }
    }
}
