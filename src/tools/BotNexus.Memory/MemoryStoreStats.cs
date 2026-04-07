namespace BotNexus.Memory;

public sealed record MemoryStoreStats(int EntryCount, long DatabaseSizeBytes, DateTimeOffset? LastIndexedAt);
