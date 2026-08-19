using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BotNexus.Persistence.Sqlite.Tests;

/// <summary>
/// Disposal-safety and handler-lifetime tests for the <c>busy_timeout</c> <c>StateChange</c>
/// subscription attached by <see cref="SqliteConnectionFactory"/> (#2977).
///
/// The original handler captured the <c>connection</c> local instead of using the event's
/// <c>sender</c>, and executed a command with no guard for a connection whose underlying
/// <c>SQLitePCL.sqlite3</c> handle had already gone away. Under parallel test load that surfaced as
/// <c>ObjectDisposedException: Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'</c>
/// thrown out of the event callback and onto the caller's <c>OpenAsync</c> stack, red-lighting the
/// core gate on PRs whose diff contained no C# at all.
///
/// These tests pin the two halves of the contract: the handler must not run against a connection
/// whose handle is gone, and it must never let a teardown-time <c>ObjectDisposedException</c> escape
/// the callback. They fail against the pre-fix handler.
/// </summary>
public sealed class SqliteConnectionFactoryDisposalTests : IDisposable
{
    private readonly string _dir;

    public SqliteConnectionFactoryDisposalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "botnexus-connfactory-disposal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_dir);
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering handle on Windows must not fail the test.
        }
    }

    private string DbPath => Path.Combine(_dir, "disposal.db");

    /// <summary>
    /// A connection that reports no underlying handle, standing in for one whose
    /// <c>SQLitePCL.sqlite3</c> handle has already been disposed, and that counts pragma-command
    /// creation so the test can prove the handler was skipped rather than merely quiet.
    /// </summary>
    private sealed class HandleGoneConnection(string connectionString) : SqliteConnection(connectionString)
    {
        public int CreateCommandCalls { get; private set; }

        public override sqlite3? Handle => null;

        // NB: SqliteConnection.CreateCommand is `new virtual`, so this MUST be an override - a
        // `new` method would be bypassed by the handler's SqliteConnection-typed call and the
        // counter assertion would pass vacuously.
        public override SqliteCommand CreateCommand()
        {
            CreateCommandCalls++;
            return base.CreateCommand();
        }
    }

    /// <summary>
    /// A connection whose command factory throws <see cref="ObjectDisposedException"/> exactly the
    /// way <c>SqliteCommand.PrepareAndEnumerateStatements</c> does once the native handle has been
    /// released underneath a live managed connection object.
    /// </summary>
    private sealed class DisposedHandleConnection(string connectionString) : SqliteConnection(connectionString)
    {
        public override SqliteCommand CreateCommand()
            => throw new ObjectDisposedException("SQLitePCL.sqlite3");
    }

    [Fact]
    public async Task Handler_does_not_run_when_connection_handle_is_gone()
    {
        await using var connection = new HandleGoneConnection($"Data Source={DbPath}");
        SqliteConnectionFactory.AttachBusyTimeout(connection);

        // Must not throw out of the Open transition...
        await connection.OpenAsync();

        // ...and must have skipped the pragma entirely rather than attempted and swallowed it.
        connection.CreateCommandCalls.ShouldBe(
            0,
            "The busy_timeout handler must not execute a command against a connection whose " +
            "underlying sqlite3 handle is gone. See #2977.");
    }

    [Fact]
    public async Task Handler_does_not_escape_ObjectDisposedException_from_the_callback()
    {
        await using var connection = new DisposedHandleConnection($"Data Source={DbPath}");
        SqliteConnectionFactory.AttachBusyTimeout(connection);

        // The pre-fix handler lets this ObjectDisposedException propagate out of OnStateChange and
        // onto the caller's OpenAsync stack - the exact shape observed in the core gate.
        await Should.NotThrowAsync(async () => await connection.OpenAsync());
    }

    [Fact]
    public void Handler_does_not_capture_the_connection_it_is_attached_to()
    {
        // AC4: no StateChange subscription may hold the connection it captures. The handler uses
        // the event's `sender` instead of a captured local, so a stale subscription can never drive
        // a command against a connection whose native handle is gone. (The subscription itself must
        // survive close/reopen - busy_timeout resets to 0 on every open - so the safety property is
        // "captures nothing", not "detaches".)
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        SqliteConnectionFactory.AttachBusyTimeout(connection);

        var handlers = FindStateChangeHandlers(connection);

        // Non-vacuity: if reflection found nothing, the assertion below would pass for the wrong
        // reason. The subscription definitely exists - AttachBusyTimeout just made it.
        handlers.Count.ShouldBe(
            1,
            "Expected exactly one StateChange subscription to inspect. If this fails the reflection " +
            "probe has drifted from Microsoft.Data.Sqlite's event storage and the capture assertion " +
            "below would be vacuous.");

        foreach (var handler in handlers)
        {
            var target = handler.Target;
            if (target is null)
            {
                continue; // A static handler captures nothing at all - trivially compliant.
            }

            foreach (var field in target.GetType()
                         .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                typeof(DbConnection).IsAssignableFrom(field.FieldType).ShouldBeFalse(
                    $"The busy_timeout StateChange handler captures a connection in closure field " +
                    $"'{field.Name}' of type '{field.FieldType.Name}'. The handler must use the " +
                    "event's `sender` instead, so the subscription never holds the connection it is " +
                    "attached to. See #2977 AC4.");

                field.GetValue(target).ShouldNotBeOfType<SqliteConnection>(
                    $"The busy_timeout StateChange handler captures a SqliteConnection in closure " +
                    $"field '{field.Name}'. Use the event's `sender`. See #2977 AC4.");
            }
        }
    }

    private static List<StateChangeEventHandler> FindStateChangeHandlers(SqliteConnection connection)
    {
        var found = new List<StateChangeEventHandler>();

        for (var type = connection.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.GetValue(connection) is StateChangeEventHandler handler)
                {
                    found.AddRange(handler.GetInvocationList().Cast<StateChangeEventHandler>());
                }
            }
        }

        return found;
    }
}
