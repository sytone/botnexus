using SQLitePCL;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Bounded, read-only observations of the current SQLite native library's allocator.
/// These counters do not cover all SQLite mappings/page caches, count connections, or
/// measure all process-native allocations. They do not attribute the private-minus-GC gap.
/// </summary>
public static class SqliteAllocatorDiagnostics
{
    // SQLitePCLRaw 2.1.10 raw.get_Provider throws exactly System.Exception with this
    // message before calling the native binding when no provider has been installed.
    // Restrict the filter to that known condition: other binding defects must surface.
    private const string ProviderNotInitializedMessage =
        "You need to call SQLitePCL.raw.SetProvider().  If you are using a bundle package, this is done by calling SQLitePCL.Batteries.Init().";

    /// <summary>
    /// Reads current bytes and peak bytes since the library's last high-water reset,
    /// initializing/changing the provider, opening a database, releasing memory, or forcing GC.
    /// Returns unavailable with null counters if the raw provider has not been initialized.
    /// The two native reads are not atomic; zero is a valid available reading.
    /// </summary>
    public static SqliteAllocatorSnapshot Capture() =>
        Capture(raw.sqlite3_memory_used, raw.sqlite3_memory_highwater);

    // Per-call binding seam avoids altering global SQLite state in tests.
    internal static SqliteAllocatorSnapshot Capture(Func<long> readCurrent, Func<int, long> readPeak)
    {
        try
        {
            var current = readCurrent();
            var peak = readPeak(0); // Never reset the native library's high-water mark.
            return new SqliteAllocatorSnapshot(true, current, peak);
        }
        catch (Exception ex) when (ex.GetType() == typeof(Exception) &&
            string.Equals(ex.Message, ProviderNotInitializedMessage, StringComparison.Ordinal))
        {
            return new SqliteAllocatorSnapshot(false, null, null);
        }
    }
}
