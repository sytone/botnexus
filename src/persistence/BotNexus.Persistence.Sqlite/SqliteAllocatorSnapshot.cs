namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Non-atomic current and peak-since-last-reset byte readings from the current SQLite
/// native library allocator only. Not a census of all SQLite or process-native memory.
/// </summary>
/// <param name="IsAvailable">False when no raw provider has been installed; counters are then null.</param>
/// <param name="CurrentBytes">Current allocator bytes, or null when unavailable. Zero is valid.</param>
/// <param name="PeakBytes">Allocator peak bytes since the library's last reset, read without resetting; null when unavailable.</param>
public sealed record SqliteAllocatorSnapshot(bool IsAvailable, long? CurrentBytes, long? PeakBytes);
