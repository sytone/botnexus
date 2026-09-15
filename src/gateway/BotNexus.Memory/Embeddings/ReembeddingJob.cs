namespace BotNexus.Memory.Embeddings;

/// <summary>Durable state and current corpus progress for a memory re-embedding job.</summary>
/// <param name="JobId">Stable identifier retained when the same target is ensured again.</param>
/// <param name="TargetIdentity">Embedding identity every live row must eventually carry.</param>
/// <param name="State">Persisted operator-controlled lifecycle state.</param>
/// <param name="TotalCount">Current number of live rows in the memory store.</param>
/// <param name="CoveredCount">Current live rows carrying a decodable matching embedding.</param>
/// <param name="PendingCount">Current live rows requiring a replacement embedding.</param>
/// <param name="FailedCount">Pending rows that have failed at least once for this job.</param>
/// <param name="LastError">Most recently recorded bounded provider error, when any.</param>
public sealed record ReembeddingJob(string JobId, EmbeddingIdentity TargetIdentity, ReembeddingJobState State,
    int TotalCount, int CoveredCount, int PendingCount, int FailedCount, string? LastError);
