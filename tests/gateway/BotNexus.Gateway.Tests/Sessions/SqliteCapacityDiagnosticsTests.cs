using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SqliteCapacityDiagnosticsTests
{
    [Fact]
    public void Capture_PageLimitFailure_ObservesFailingConnectionWithoutReadingPayload()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE fixture(value BLOB); PRAGMA max_page_count=2; PRAGMA temp_store=MEMORY";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO fixture VALUES (zeroblob(16384))";
        var exception = Should.Throw<SqliteException>(() => command.ExecuteNonQuery());
        exception.SqliteErrorCode.ShouldBe(13);
        var observation = SqliteCapacityDiagnostics.Capture(connection, _ => throw new IOException("private path"));
        observation.PageCount.ShouldBe(2);
        observation.MaxPageCount.ShouldBe(2);
        observation.PageLimitObserved.ShouldBeTrue();
        (observation.PageSize > 0).ShouldBeTrue();
        observation.TempStore.ShouldBe(2);
        observation.MainVolumeAvailableBytes.ShouldBeNull();
        observation.TempVolumeAvailableBytes.ShouldBeNull();
        observation.MainVolumeFullObserved.ShouldBeFalse();
        observation.TempVolumeFullObserved.ShouldBeFalse();
    }

    [Fact]
    public void Capture_UnavailableConnection_DoesNotThrowOrInventConstraints()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var observation = SqliteCapacityDiagnostics.Capture(connection);
        observation.PageCount.ShouldBeNull();
        observation.MaxPageCount.ShouldBeNull();
        observation.TempStore.ShouldBeNull();
        observation.PageLimitObserved.ShouldBeFalse();
        observation.MainVolumeFullObserved.ShouldBeFalse();
        observation.TempVolumeFullObserved.ShouldBeFalse();
    }

    [Fact]
    public void ObserveFailure_PreservesExceptionAndSkipsNonCapacityFailures()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var original = new SqliteException("sensitive", 13);
        SqliteException? caught = null;
        try
        {
            try { throw original; }
            catch (SqliteException ex) when (SqliteCapacityDiagnostics.ObserveFailure(connection, ex)) { throw; }
        }
        catch (SqliteException ex) { caught = ex; }
        ReferenceEquals(caught, original).ShouldBeTrue();
        SqliteCapacityObservation.FromFailure(original).ShouldNotBeNull();
        var other = new SqliteException("other", 5);
        SqliteCapacityDiagnostics.ObserveFailure(connection, other).ShouldBeFalse();
        SqliteCapacityObservation.FromFailure(other).ShouldBeNull();
    }

    [Fact]
    public void Capture_FileDatabaseAndUnavailableVolume_PreservesMetadataAndUnknownTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capacity-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE fixture(value BLOB); PRAGMA temp_store=FILE";
            command.ExecuteNonQuery();
            var calls = 0;
            var observation = SqliteCapacityDiagnostics.Capture(connection, _ =>
            {
                calls++;
                throw new IOException("sensitive path");
            });
            calls.ShouldBe(1);
            (observation.PageCount > 0).ShouldBeTrue();
            observation.TempStore.ShouldBe(1);
            observation.MainVolumeAvailableBytes.ShouldBeNull();
            observation.TempVolumeAvailableBytes.ShouldBeNull();
            observation.PageLimitObserved.ShouldBeFalse();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0L, 100L, true, false)]
    [InlineData(100L, 0L, false, true)]
    [InlineData(100L, 100L, false, false)]
    public void Observations_SeparateMainAndTempConstraintsWithoutClaimingCause(
        long mainBytes, long tempBytes, bool mainFull, bool tempFull)
    {
        var observation = new SqliteCapacityObservation(2, 100, 4096, 0, 1, mainBytes, tempBytes);
        observation.PageLimitObserved.ShouldBeFalse();
        observation.MainVolumeFullObserved.ShouldBe(mainFull);
        observation.TempVolumeFullObserved.ShouldBe(tempFull);
    }
}
