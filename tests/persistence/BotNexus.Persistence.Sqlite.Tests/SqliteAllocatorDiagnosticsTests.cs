using BotNexus.Persistence.Sqlite;
using SQLitePCL;

namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteAllocatorDiagnosticsTests
{
    private const string ProviderNotInitializedMessage =
        "You need to call SQLitePCL.raw.SetProvider().  If you are using a bundle package, this is done by calling SQLitePCL.Batteries.Init().";

    [Fact]
    public void Capture_NativeLibrary_ReturnsAvailableNonnegativeCounters()
    {
        // Use an instance binding to the installed library, without installing/changing
        // the process-global raw provider, initializing SQLite, or opening a database.
        ISQLite3Provider provider = new SQLite3Provider_e_sqlite3();
        var snapshot = SqliteAllocatorDiagnostics.Capture(
            provider.sqlite3_memory_used, provider.sqlite3_memory_highwater);

        snapshot.IsAvailable.ShouldBeTrue();
        snapshot.CurrentBytes.ShouldNotBeNull();
        snapshot.CurrentBytes.GetValueOrDefault().ShouldBeGreaterThanOrEqualTo(0L);
        snapshot.PeakBytes.ShouldNotBeNull();
        snapshot.PeakBytes.GetValueOrDefault().ShouldBeGreaterThanOrEqualTo(0L);
        // Separate native reads are not atomic: do not compare current to peak.
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(4294967297L, 8589934593L)]
    [InlineData(8589934593L, 4294967297L)]
    public void Capture_BoundCounters_PreservesInt64AndNeverResetsPeak(long current, long peak)
    {
        var calls = new List<string>();
        var snapshot = SqliteAllocatorDiagnostics.Capture(
            () => { calls.Add("current"); return current; },
            reset => { reset.ShouldBe(0); calls.Add("peak"); return peak; });

        snapshot.IsAvailable.ShouldBeTrue();
        snapshot.CurrentBytes.ShouldBe(current);
        snapshot.PeakBytes.ShouldBe(peak);
        calls.ShouldBe(new[] { "current", "peak" });
    }

    [Fact]
    public void Capture_UninitializedProvider_ReturnsUnavailableWithoutReadingPeak()
    {
        var peakCalled = false;
        var snapshot = SqliteAllocatorDiagnostics.Capture(
            () => throw new Exception(ProviderNotInitializedMessage),
            _ => { peakCalled = true; return 0L; });

        snapshot.IsAvailable.ShouldBeFalse();
        snapshot.CurrentBytes.ShouldBeNull();
        snapshot.PeakBytes.ShouldBeNull();
        peakCalled.ShouldBeFalse();
    }

    [Fact]
    public void Capture_ProviderUnavailableDuringPeak_DiscardsPartialReading()
    {
        var snapshot = SqliteAllocatorDiagnostics.Capture(
            () => 42L, _ => throw new Exception(ProviderNotInitializedMessage));

        snapshot.IsAvailable.ShouldBeFalse();
        snapshot.CurrentBytes.ShouldBeNull();
        snapshot.PeakBytes.ShouldBeNull();
    }

    [Fact]
    public void Capture_GenuineBindingDefect_Propagates()
    {
        var defect = new InvalidOperationException(ProviderNotInitializedMessage);
        Should.Throw<InvalidOperationException>(() => SqliteAllocatorDiagnostics.Capture(
            () => throw defect, _ => 0L)).ShouldBeSameAs(defect);

        var otherDefect = new Exception("unexpected binding failure");
        Should.Throw<Exception>(() => SqliteAllocatorDiagnostics.Capture(
            () => 0L, _ => throw otherDefect)).ShouldBeSameAs(otherDefect);
    }
}
