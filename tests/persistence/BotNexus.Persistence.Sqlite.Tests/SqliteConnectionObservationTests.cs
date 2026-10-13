using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteConnectionObservationTests
{
    [Fact]
    public void Create_closed_connection_does_not_change_observation()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = SqliteConnectionFactory.Create("Data Source=:memory:");
        SqliteConnectionFactory.GetConnectionObservation().ShouldBe(before);
    }

    [Fact]
    public void Observation_counts_open_close_reopen_and_dispose_once()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = SqliteConnectionFactory.Create("Data Source=:memory:");
        connection.Open();
        connection.Open();
        AssertDelta(before, 1, 1, 0);
        var frozen = SqliteConnectionFactory.GetConnectionObservation();
        frozen.PeakObservedOpenConnections.ShouldBe(Math.Max(before.PeakObservedOpenConnections, before.CurrentObservedOpenConnections + 1));
        connection.Close();
        connection.Close();
        AssertDelta(before, 0, 1, 1);
        connection.Open();
        AssertDelta(before, 1, 2, 1);
        connection.Dispose();
        connection.Dispose();
        AssertDelta(before, 0, 2, 2);
        frozen.CurrentObservedOpenConnections.ShouldBe(before.CurrentObservedOpenConnections + 1);
        frozen.OpenTransitions.ShouldBe(before.OpenTransitions + 1);
    }

    [Fact]
    public async Task Observation_counts_async_open_close_and_dispose()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        await using var connection = SqliteConnectionFactory.Create("Data Source=:memory:");
        await connection.OpenAsync();
        AssertDelta(before, 1, 1, 0);
        await connection.CloseAsync();
        await connection.OpenAsync();
        await connection.DisposeAsync();
        AssertDelta(before, 0, 2, 2);
    }

    [Fact]
    public void AttachBusyTimeout_already_open_connection_enters_observation_once()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SqliteConnectionFactory.GetConnectionObservation().ShouldBe(before);
        connection.AttachBusyTimeout(1234);
        connection.AttachBusyTimeout(2345);
        AssertDelta(before, 1, 1, 0);
        connection.Close();
        AssertDelta(before, 0, 1, 1);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout;";
        Convert.ToInt64(pragma.ExecuteScalar()).ShouldBe(2345);
        connection.Close();
        AssertDelta(before, 0, 2, 2);
    }

    [Fact]
    public void AttachBusyTimeout_closed_duplicate_preserves_last_timeout_on_open_and_reopen()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.AttachBusyTimeout(1234);
        connection.AttachBusyTimeout(2345);
        for (var i = 0; i < 2; i++)
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout;";
            Convert.ToInt64(pragma.ExecuteScalar()).ShouldBe(2345);
            connection.Close();
        }
        AssertDelta(before, 0, 2, 2);
    }

    [Fact]
    public void AttachBusyTimeout_concurrent_attachment_observes_each_edge_once()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = new SqliteConnection("Data Source=:memory:");
        Parallel.For(0, 32, _ => connection.AttachBusyTimeout());
        connection.Open();
        AssertDelta(before, 1, 1, 0);
        connection.Close();
        AssertDelta(before, 0, 1, 1);
    }

    [Fact]
    public void Observation_failed_provider_open_without_open_event_is_not_counted()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".missing.db"),
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = SqliteConnectionFactory.Create(connectionString);
        Should.Throw<SqliteException>(() => connection.Open());
        connection.State.ShouldBe(ConnectionState.Closed);
        SqliteConnectionFactory.GetConnectionObservation().ShouldBe(before);
    }

    [Fact]
    public void Observation_failed_policy_open_is_counted_until_disposal()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = new FailingPolicyConnection();
        connection.AttachBusyTimeout();
        Should.Throw<InvalidOperationException>(() => connection.Open());
        connection.State.ShouldBe(ConnectionState.Open);
        AssertDelta(before, 1, 1, 0);
        connection.Dispose();
        AssertDelta(before, 0, 1, 1);
    }

    [Fact]
    public void Observation_invalid_handle_still_counts_managed_open_state()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = new MissingHandleConnection();
        connection.AttachBusyTimeout();
        connection.Open();
        connection.CreateCommandCalls.ShouldBe(0);
        AssertDelta(before, 1, 1, 0);
        connection.Dispose();
        AssertDelta(before, 0, 1, 1);
    }

    [Fact]
    public void Observation_parallel_connections_have_coherent_snapshots_and_peak()
    {
        const int count = 16;
        var before = SqliteConnectionFactory.GetConnectionObservation();
        var connections = Enumerable.Range(0, count)
            .Select(_ => SqliteConnectionFactory.Create("Data Source=:memory:")).ToArray();
        try
        {
            Parallel.ForEach(connections, connection =>
            {
                connection.Open();
                AssertCoherent(SqliteConnectionFactory.GetConnectionObservation());
            });
            AssertDelta(before, count, count, 0);
            SqliteConnectionFactory.GetConnectionObservation().PeakObservedOpenConnections
                .ShouldBe(Math.Max(before.PeakObservedOpenConnections, before.CurrentObservedOpenConnections + count));
            Parallel.ForEach(connections, connection =>
            {
                connection.Close();
                AssertCoherent(SqliteConnectionFactory.GetConnectionObservation());
            });
            AssertDelta(before, 0, count, count);
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Dispose();
            }
        }
    }

    [Fact]
    public void Observation_pooling_counts_follow_connection_string_at_each_open()
    {
        var before = SqliteConnectionFactory.GetConnectionObservation();
        using var connection = SqliteConnectionFactory.Create("Data Source=:memory:;Pooling=True");
        connection.Open();
        var pooled = SqliteConnectionFactory.GetConnectionObservation();
        pooled.PoolingEnabledObservedOpenConnections.ShouldBe(before.PoolingEnabledObservedOpenConnections + 1);
        pooled.PoolingDisabledObservedOpenConnections.ShouldBe(before.PoolingDisabledObservedOpenConnections);
        connection.Close();
        connection.ConnectionString = "Data Source=:memory:;Pooling=False";
        connection.Open();
        var unpooled = SqliteConnectionFactory.GetConnectionObservation();
        unpooled.PoolingEnabledObservedOpenConnections.ShouldBe(before.PoolingEnabledObservedOpenConnections);
        unpooled.PoolingDisabledObservedOpenConnections.ShouldBe(before.PoolingDisabledObservedOpenConnections + 1);
        connection.Dispose();
        AssertDelta(before, 0, 2, 2);
    }

    [Fact]
    public void Observation_does_not_keep_attached_connections_alive()
    {
        var weak = CreateUnrootedConnection();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        weak.TryGetTarget(out _).ShouldBeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<SqliteConnection> CreateUnrootedConnection()
    {
        var connection = SqliteConnectionFactory.Create("Data Source=:memory:");
        return new WeakReference<SqliteConnection>(connection);
    }

    private static void AssertDelta(SqliteConnectionObservation before, long current, long opens, long closes)
    {
        var after = SqliteConnectionFactory.GetConnectionObservation();
        after.CurrentObservedOpenConnections.ShouldBe(before.CurrentObservedOpenConnections + current);
        after.OpenTransitions.ShouldBe(before.OpenTransitions + opens);
        after.CloseTransitions.ShouldBe(before.CloseTransitions + closes);
        AssertCoherent(after);
    }

    private static void AssertCoherent(SqliteConnectionObservation snapshot)
    {
        snapshot.CurrentObservedOpenConnections.ShouldBe(snapshot.OpenTransitions - snapshot.CloseTransitions);
        snapshot.CurrentObservedOpenConnections.ShouldBe(snapshot.PoolingEnabledObservedOpenConnections + snapshot.PoolingDisabledObservedOpenConnections);
        snapshot.CurrentObservedOpenConnections.ShouldBeGreaterThanOrEqualTo(0);
        snapshot.PeakObservedOpenConnections.ShouldBeGreaterThanOrEqualTo(snapshot.CurrentObservedOpenConnections);
    }

    private sealed class FailingPolicyConnection() : SqliteConnection("Data Source=:memory:")
    {
        public override SqliteCommand CreateCommand() => throw new InvalidOperationException("Policy rejected open");
    }

    private sealed class MissingHandleConnection() : SqliteConnection("Data Source=:memory:")
    {
        public override sqlite3? Handle => null;
        public int CreateCommandCalls { get; private set; }
        public override SqliteCommand CreateCommand()
        {
            CreateCommandCalls++;
            return base.CreateCommand();
        }
    }
}
