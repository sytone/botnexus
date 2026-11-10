using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class TemporaryFileLogicalFootprintVfsTests(ITestOutputHelper output)
{
    [Fact]
    public void Accounting_OverlappingLifetimes_UsesMaximumOfSimultaneousSum()
    {
        var ledger = new TemporaryFileLogicalFootprintVfs.Accounting();
        ledger.Open(1);
        ledger.Open(2);
        ledger.ObserveSize(1, 100);
        ledger.ObserveSize(2, 200);
        ledger.Close(1);
        ledger.ObserveSize(2, 250);
        ledger.CurrentOpenTemporaryLogicalBytes.ShouldBe(250);
        ledger.PeakOpenTemporaryLogicalBytes.ShouldBe(300);
        ledger.Close(2);
        ledger.CurrentOpenTemporaryLogicalBytes.ShouldBe(0);
        ledger.PeakOpenTemporaryLogicalBytes.ShouldBe(300);
    }

    [Fact]
    public void Accounting_OverwriteSparseExtentAndTruncate_PreserveHighWater()
    {
        var ledger = new TemporaryFileLogicalFootprintVfs.Accounting();
        ledger.Open(1);
        ledger.ObserveSize(1, 100);
        ledger.ObserveSize(1, 100); // Overwriting bytes does not grow EOF.
        ledger.CurrentOpenTemporaryLogicalBytes.ShouldBe(100);
        ledger.ObserveSize(1, 8193); // Sparse write at offset 8192, length 1.
        ledger.CurrentOpenTemporaryLogicalBytes.ShouldBe(8193);
        ledger.ObserveSize(1, 10);
        ledger.CurrentOpenTemporaryLogicalBytes.ShouldBe(10);
        ledger.PeakOpenTemporaryLogicalBytes.ShouldBe(8193);
        Should.Throw<InvalidOperationException>(ledger.Reset);
        ledger.Close(1);
        ledger.Reset();
        ledger.PeakOpenTemporaryLogicalBytes.ShouldBe(0);
    }

    [Fact]
    public void Accounting_UnknownHandleOrFailure_InvalidatesInsteadOfReportingZero()
    {
        var ledger = new TemporaryFileLogicalFootprintVfs.Accounting();
        Should.Throw<InvalidOperationException>(() => ledger.ObserveSize(1, 10));
        Should.Throw<InvalidOperationException>(ledger.EnsureValid);
        Should.Throw<InvalidOperationException>(ledger.Reset);
        var failed = new TemporaryFileLogicalFootprintVfs.Accounting();
        failed.Invalidate("injected file-size failure");
        Should.Throw<InvalidOperationException>(failed.EnsureValid).Message.ShouldContain("injected");
        var abandoned = new TemporaryFileLogicalFootprintVfs.Accounting();
        abandoned.Open(1);
        abandoned.ObserveSize(1, 123);
        abandoned.Abandon(1);
        Should.Throw<InvalidOperationException>(() => _ = abandoned.CurrentOpenTemporaryLogicalBytes);
        Should.Throw<InvalidOperationException>(() => _ = abandoned.PeakOpenTemporaryLogicalBytes);
        Should.Throw<InvalidOperationException>(abandoned.Reset);
    }

    [Theory]
    [InlineData(0x00000200)] // TEMP_DB
    [InlineData(0x00000400)] // TRANSIENT_DB
    [InlineData(0x00001000)] // TEMP_JOURNAL
    [InlineData(0x00002000)] // SUBJOURNAL
    [InlineData(0x00000008)] // DELETEONCLOSE
    public void Classification_TemporaryKind_IsScratch(int flags) =>
        TemporaryFileLogicalFootprintVfs.IsScratch(flags).ShouldBeTrue();

    [Theory]
    [InlineData(0x00000100)] // MAIN_DB
    [InlineData(0x00000800)] // MAIN_JOURNAL
    [InlineData(0x00080000)] // WAL
    public void Classification_MainKinds_AreExcluded(int flags) =>
        TemporaryFileLogicalFootprintVfs.IsScratch(flags | 8).ShouldBeFalse();

    [Fact]
    public void NativeVfs_FailedMainOpen_LeavesNoFileOrScratchLifetime()
    {
        using var vfs = new TemporaryFileLogicalFootprintVfs();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db");
        Should.Throw<SqliteException>(() => vfs.OpenConnection(missing));
        vfs.ActiveConnectionCount.ShouldBe(0);
        vfs.OpenFileCount.ShouldBe(0);
        vfs.ScratchOpenCount.ShouldBe(0);
        vfs.EnsureValid();
    }

    [Fact]
    public void NativeVfs_TwoObservers_IsolateRegistrationsAndPreserveDefaultBeforeDuringAfter()
    {
        var baseline = TemporaryFileLogicalFootprintVfs.CaptureDefaultVfsPointer();
        using var first = new TemporaryFileLogicalFootprintVfs();
        using var second = new TemporaryFileLogicalFootprintVfs();
        TemporaryFileLogicalFootprintVfs.CaptureDefaultVfsPointer().ShouldBe(baseline);
        var firstLease = first.OpenConnection(":memory:");
        var secondLease = second.OpenConnection(":memory:");
        try
        {
            var firstPointer = first.ReadConnectionVfsPointer(firstLease.Connection);
            var secondPointer = second.ReadConnectionVfsPointer(secondLease.Connection);
            firstPointer.ShouldNotBe(baseline);
            secondPointer.ShouldNotBe(baseline);
            firstPointer.ShouldNotBe(secondPointer);
            first.ActiveConnectionCount.ShouldBe(1);
            second.ActiveConnectionCount.ShouldBe(1);
            firstLease.Dispose();
            first.Dispose();
            TemporaryFileLogicalFootprintVfs.CaptureDefaultVfsPointer().ShouldBe(baseline);
            second.ActiveConnectionCount.ShouldBe(1);
            second.VerifySameNativeLibrary(secondLease.Connection);
            using (var command = secondLease.Connection.CreateCommand())
            {
                command.CommandText = "SELECT 42";
                Convert.ToInt64(command.ExecuteScalar()).ShouldBe(42);
            }
        }
        finally
        {
            firstLease.Dispose();
            secondLease.Dispose();
        }
        second.Dispose();
        TemporaryFileLogicalFootprintVfs.CaptureDefaultVfsPointer().ShouldBe(baseline);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeVfs_LiveOwnedConnection_RefusesEarlyDisposalAndClosesIdempotently(bool memory)
    {
        var path = memory ? ":memory:" : Path.Combine(Path.GetTempPath(), $"vfs-lease-{Guid.NewGuid():N}.db");
        using var vfs = new TemporaryFileLogicalFootprintVfs();
        try
        {
            var originalDefault = vfs.DefaultVfsPointer;
            var lease = vfs.OpenConnection(path);
            try
            {
                vfs.ActiveConnectionCount.ShouldBe(1);
                if (memory) vfs.OpenFileCount.ShouldBe(0);
                Should.Throw<InvalidOperationException>(vfs.Dispose).Message.ShouldContain("active_connections=1");
                // Rejected disposal must leave callbacks/library usable, not just roots.
                using (var command = lease.Connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA temp_store=FILE; PRAGMA cache_size=-64; PRAGMA threads=0; CREATE TABLE p(id INTEGER PRIMARY KEY, value TEXT); WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<512) INSERT INTO p SELECT i,hex(zeroblob(8192)) FROM n;";
                    command.ExecuteNonQuery();
                    command.CommandText = "SELECT value FROM p ORDER BY -id";
                    using var reader = command.ExecuteReader();
                    var rows = 0;
                    while (reader.Read()) rows++;
                    rows.ShouldBe(512);
                    Should.Throw<InvalidOperationException>(lease.Dispose).Message.ShouldContain("reader");
                    vfs.ActiveConnectionCount.ShouldBe(1);
                }
                vfs.ScratchOpenCount.ShouldBeGreaterThan(0);
                vfs.DefaultVfsPointer.ShouldBe(originalDefault);
                using var ordinary = new SqliteConnection("Data Source=:memory:;Pooling=False");
                ordinary.Open();
                vfs.ReadConnectionVfsPointer(ordinary).ShouldBe(originalDefault);
                vfs.ReadConnectionVfsPointer(lease.Connection).ShouldNotBe(originalDefault);
            }
            finally { lease.Dispose(); }
            lease.Dispose();
            vfs.ActiveConnectionCount.ShouldBe(0);
            vfs.OpenFileCount.ShouldBe(0);
            Should.Throw<ObjectDisposedException>(() => _ = lease.Connection);
            vfs.Dispose();
            vfs.Dispose();
            Should.Throw<ObjectDisposedException>(() => vfs.OpenConnection(":memory:"));
        }
        finally { if (!memory) File.Delete(path); }
    }

    [Theory]
    [InlineData(29)] // WIN32_GET_HANDLE
    [InlineData(23)] // WIN32_SET_HANDLE
    [InlineData(25)] // ZIPVFS extension
    [InlineData(43)] // NULL_IO handle replacement
    [InlineData(45)] // FILESTAT can expose an OS handle/fd in optional native builds
    [InlineData(26)] // RBU extension
    [InlineData(1000)] // Unknown extension
    public void NativeVfs_ScratchHandleEscapeOrExtension_InvalidatesWithoutTouchingOutput(int operation)
    {
        TemporaryFileLogicalFootprintVfs? observer = null;
        var attempted = false;
        using var vfs = new TemporaryFileLogicalFootprintVfs(beforeScratchSize: () =>
        {
            if (attempted) return;
            attempted = true;
            var active = observer ?? throw new InvalidOperationException("Observer not assigned.");
            active.ProbeScratchPointers();
            active.ProbeScratchControl(operation).ShouldBe(10);
        });
        observer = vfs;
        using (var lease = vfs.OpenConnection(":memory:"))
        {
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "PRAGMA temp_store=FILE; PRAGMA cache_size=-64; PRAGMA threads=0; CREATE TABLE p(id INTEGER PRIMARY KEY, value TEXT); WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<512) INSERT INTO p SELECT i,hex(zeroblob(8192)) FROM n;";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT value FROM p ORDER BY -id";
            // File-control probe is contained; measurement validity must still reject it.
            using var reader = command.ExecuteReader();
            while (reader.Read()) { }
        }
        attempted.ShouldBeTrue();
        vfs.OpenFileCount.ShouldBe(0);
        vfs.ActiveConnectionCount.ShouldBe(0);
        Should.Throw<InvalidOperationException>(vfs.EnsureValid).Message.ShouldContain("unsupported scratch file-control");
        Should.Throw<InvalidOperationException>(vfs.Dispose).Message.ShouldContain("unsupported scratch file-control");
    }

    [Theory]
    [InlineData(1)] // Initial EOF observation inside xOpen, before native caller owns it.
    [InlineData(2)] // Later observation after a successful xOpen transfers ownership.
    public void NativeVfs_CallbackFailure_IsContainedAndStillClosesParent(int failAtObservation)
    {
        var observations = 0;
        var vfs = new TemporaryFileLogicalFootprintVfs(beforeScratchSize: () =>
        {
            if (++observations >= failAtObservation)
                throw new InvalidOperationException("injected native size observation failure");
        });
        Exception? originalFailure = null;
        try
        {
            using (var lease = vfs.OpenConnection(":memory:"))
            {
                var connection = lease.Connection;
                vfs.VerifySameNativeLibrary(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA temp_store=FILE; PRAGMA cache_size=-64; PRAGMA threads=0; CREATE TABLE p(id INTEGER PRIMARY KEY, value TEXT); WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<512) INSERT INTO p SELECT i,replace(hex(zeroblob(8192)),'0','x') FROM n;";
                command.ExecuteNonQuery();
                command.CommandText = "SELECT value FROM p ORDER BY -id";
                Should.Throw<SqliteException>(() =>
                {
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) { }
                });
            }
            var diagnostic = vfs.LifetimeDiagnostics;
            output.WriteLine($"failure_observation={failAtObservation}; {diagnostic}");
            observations.ShouldBeGreaterThanOrEqualTo(failAtObservation);
            vfs.OpenFileCount.ShouldBe(0, diagnostic);
            vfs.ScratchOpenCount.ShouldBeGreaterThan(0, diagnostic);
            vfs.ScratchCloseCount.ShouldBe(vfs.ScratchOpenCount, diagnostic);
            Should.Throw<InvalidOperationException>(vfs.EnsureValid).Message.ShouldContain("injected");
            Should.Throw<InvalidOperationException>(() => _ = vfs.PeakOpenTemporaryLogicalBytes);
            // Disposal validates too, but only AFTER safe native teardown.
            Should.Throw<InvalidOperationException>(vfs.Dispose).Message.ShouldContain("injected");
        }
        catch (Exception ex)
        {
            originalFailure = ex;
            throw;
        }
        finally
        {
            // Never replace an assertion/SQL failure with a secondary Dispose error.
            // Both are retained, together with raw lifetime state (validity-independent).
            try { vfs.Dispose(); }
            catch (Exception teardown) when (originalFailure is not null)
            {
                throw new AggregateException(vfs.LifetimeDiagnostics, originalFailure, teardown);
            }
        }
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    public void NativeVfs_PayloadSorter_ObservesSpillAndClosesEveryHandle(int count)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scratch-observer-{Guid.NewGuid():N}.db");
        using var vfs = new TemporaryFileLogicalFootprintVfs();
        try
        {
            using (var lease = vfs.OpenConnection(path))
            {
                var connection = lease.Connection;
                vfs.VerifySameNativeLibrary(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA temp_store=FILE; PRAGMA main.cache_size=-64; PRAGMA threads=0; CREATE TABLE payloads(id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
                    command.ExecuteNonQuery();
                    command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count) INSERT INTO payloads SELECT i,$payload FROM n";
                    command.Parameters.AddWithValue("$count", count);
                    command.Parameters.AddWithValue("$payload", new string('x', 16384));
                    command.ExecuteNonQuery();
                }
                using (var settings = connection.CreateCommand())
                {
                    settings.CommandText = "PRAGMA temp_store";
                    Convert.ToInt64(settings.ExecuteScalar()).ShouldBe(1);
                    settings.CommandText = "PRAGMA main.cache_size";
                    Convert.ToInt64(settings.ExecuteScalar()).ShouldBe(-64);
                    settings.CommandText = "PRAGMA threads";
                    Convert.ToInt64(settings.ExecuteScalar()).ShouldBe(0);
                }
                // Seeding writes main DB/journal only, never classified as scratch.
                vfs.ScratchOpenCount.ShouldBe(0);
                vfs.Reset();
                var handle = connection.Handle ?? throw new InvalidOperationException("Connection is not open.");
                SQLitePCL.raw.sqlite3_db_status(handle, 13, out _, out _, 1).ShouldBe(SQLitePCL.raw.SQLITE_OK);
                var rows = 0;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT value FROM payloads ORDER BY -id";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) rows++;
                }
                rows.ShouldBe(count);
                vfs.EnsureValid();
                vfs.ScratchOpenCount.ShouldBeGreaterThan(0);
                SQLitePCL.raw.sqlite3_db_status(handle, 13, out var writeBytes, out var highwater, 0).ShouldBe(SQLitePCL.raw.SQLITE_OK);
                highwater.ShouldBe(0);
                writeBytes.ShouldBeGreaterThan(17 * (4 * 16384 + 4096));
                vfs.PeakOpenTemporaryLogicalBytes.ShouldBeGreaterThan(17L * (4 * 16384 + 4096));
                vfs.CurrentOpenTemporaryLogicalBytes.ShouldBe(0);
                vfs.ScratchCloseCount.ShouldBe(vfs.ScratchOpenCount);
                output.WriteLine($"corpus={count}; version={SQLitePCL.raw.sqlite3_libversion_number()}; peak_open_temporary_logical_bytes={vfs.PeakOpenTemporaryLogicalBytes}; scratch_opens={vfs.ScratchOpenCount}; cumulative_temporary_write_bytes={writeBytes}; flags={string.Join(',', vfs.ScratchFlags)}");
            }
            vfs.OpenFileCount.ShouldBe(0);
            vfs.EnsureValid();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
