namespace BotNexus.Memory.Embeddings;

/// <summary>Immutable input needed by a worker to generate one replacement embedding.</summary>
/// <param name="MemoryId">Identifier used to complete or fail this exact memory row.</param>
/// <param name="Content">Stored content to send to the embedding provider.</param>
/// <param name="FailureCount">Failures already recorded for this row and target.</param>
public sealed record ReembeddingItem(string MemoryId, string Content, int FailureCount);
