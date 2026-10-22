using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Pins exclusive native ownership during concurrent checkout (#4864). The pinned provider publishes
/// activation before its weak owner (dotnet/efcore#39008); ordinary write assertions cannot distinguish
/// that pool race from application transaction misuse. Every owner stays strongly held and open here.
/// </summary>
public sealed class SqliteConnectionOwnershipTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentOpens_KeepDistinctLiveNativeHandles_AndIndependentTransactions(bool useFactory)
    {
        const int workerCount = 16;
        const int iterations = 10000;
        var directory = Path.Combine(Path.GetTempPath(), "bn-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "config.db")}";
        var connections = new SqliteConnection?[workerCount];
        var errors = new ConcurrentQueue<Exception>();
        var duplicate = false;
        var completedWaves = 0;
        string? duplicateEvidence = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var barrier = new Barrier(workerCount + 1);
        using var pool = new SqliteConnection(connectionString);
        var workers = Enumerable.Range(0, workerCount).Select(index => Task.Factory.StartNew(() =>
        {
            for (var wave = 0; wave < iterations; wave++)
            {
                barrier.SignalAndWait(deadline.Token);
                try
                {
                    var connection = useFactory
                        ? SqliteConnectionFactory.Create(connectionString)
                        : new SqliteConnection(connectionString);
                    connections[index] = connection;
                    connection.Open();
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }

                try
                {
                    barrier.SignalAndWait(deadline.Token);
                    // No owner closes until the coordinator has inspected all live handles.
                    barrier.SignalAndWait(deadline.Token);
                }
                finally
                {
                    try { connections[index]?.Dispose(); }
                    catch (Exception exception) { errors.Enqueue(exception); }
                }

                barrier.SignalAndWait(deadline.Token);
                if (duplicate || !errors.IsEmpty)
                {
                    break;
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var completed = false;
        try
        {
            output.WriteLine($"Provider={typeof(SqliteConnection).Assembly.FullName}; factory={useFactory}; workers={workerCount}; requestedWaves={iterations}");
            for (var wave = 0; wave < iterations; wave++)
            {
                // Scoped pool cleanup, only between waves with every previous owner closed.
                // This is independent checkout coverage, not a retry of failed opens or writes.
                SqliteConnection.ClearPool(pool);
                barrier.SignalAndWait(deadline.Token);
                barrier.SignalAndWait(deadline.Token);
                var owners = connections.Select((connection, index) => new
                {
                    Index = index,
                    Connection = connection,
                    State = connection?.State,
                    Handle = connection?.Handle?.DangerousGetHandle(),
                }).ToArray();
                foreach (var owner in owners)
                {
                    if (owner.State != ConnectionState.Open || owner.Handle is null || owner.Handle == IntPtr.Zero)
                    {
                        errors.Enqueue(new InvalidOperationException($"Wave {wave}, owner {owner.Index}: state={owner.State}, handle={owner.Handle}"));
                    }
                }

                var shared = owners.Where(owner => owner.Handle.HasValue)
                    .GroupBy(owner => owner.Handle).FirstOrDefault(group => group.Count() > 1);
                if (shared is not null)
                {
                    duplicate = true;
                    var pair = shared.Take(2).ToArray();
                    duplicateEvidence = $"Wave={wave}; nativeHandle={shared.Key}; owners={string.Join(",", shared.Select(owner => owner.Index))}; allOwnersHeldOpen=true";
                    output.WriteLine(duplicateEvidence);
                    if (pair[0].Connection is { } first && pair[1].Connection is { } second)
                    {
                        // Deferred read-only BEGIN avoids database writer contention: a nested-BEGIN
                        // error here identifies shared native transaction state, not SQLITE_BUSY.
                        try
                        {
                            using var firstTransaction = first.BeginTransaction(IsolationLevel.Serializable, deferred: true);
                            try
                            {
                                using var secondTransaction = second.BeginTransaction(IsolationLevel.Serializable, deferred: true);
                                secondTransaction.Rollback();
                                output.WriteLine("Second BEGIN unexpectedly succeeded on the duplicate handle.");
                            }
                            catch (SqliteException exception)
                            {
                                output.WriteLine($"SecondBeginErrorCode={exception.SqliteErrorCode}; SecondBeginError={exception.Message}");
                            }
                            firstTransaction.Rollback();
                        }
                        catch (Exception exception)
                        {
                            errors.Enqueue(exception);
                        }
                    }
                }
                completedWaves++;
                barrier.SignalAndWait(deadline.Token);
                barrier.SignalAndWait(deadline.Token);
                if (duplicate || !errors.IsEmpty)
                {
                    break;
                }
            }
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                await deadline.CancelAsync();
            }
            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                SqliteConnection.ClearPool(pool);
                Directory.Delete(directory, recursive: true);
            }
        }

        output.WriteLine($"CompletedWaves={completedWaves}; opens={completedWaves * workerCount}; duplicate={duplicate}; errors={errors.Count}");
        foreach (var exception in errors)
        {
            output.WriteLine(exception.ToString());
        }
        duplicate.ShouldBeFalse(duplicateEvidence ?? "Each open logical connection must exclusively own its native handle.");
        errors.ShouldBeEmpty();
        completedWaves.ShouldBe(iterations);
        // Stress is probabilistic: a passing run does not exclude the activation interleaving.
    }
}
