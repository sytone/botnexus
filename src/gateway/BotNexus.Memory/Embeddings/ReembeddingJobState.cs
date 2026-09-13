namespace BotNexus.Memory.Embeddings;

/// <summary>Persisted operator-controlled lifecycle of a memory re-embedding job.</summary>
public enum ReembeddingJobState
{
    /// <summary>The job may claim and process rows.</summary>
    Running,
    /// <summary>The job retains its progress but may not claim rows.</summary>
    Paused,
    /// <summary>The job has been permanently stopped.</summary>
    Cancelled
}
