using BotNexus.Memory.Models;

namespace BotNexus.Memory;

public interface IMemoryStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default);
}
