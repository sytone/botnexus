namespace BotNexus.Gateway.Providers;

/// <summary>
/// Bounds background re-embedding so it yields capacity to interactive gateway work.
/// </summary>
public sealed class MemoryReembeddingOptions
{
    /// <summary>Hard ceiling that prevents configuration from turning one pass into an unbounded scan.</summary>
    public const int MaxAgentBatchSize = 64;

    /// <summary>Hard ceiling that preserves the worker's low-priority, sequential character.</summary>
    public const int MaxItemBatchSize = 16;
    /// <summary>Maximum registered agents inspected during one scheduler pass.</summary>
    public int AgentBatchSize { get; set; } = 8;

    /// <summary>Maximum durable items claimed from one store during one pass.</summary>
    public int ItemBatchSize { get; set; } = 2;

    /// <summary>Delay between complete passes, including passes with no work.</summary>
    public TimeSpan PassDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cooperative yield between sequential item generations.</summary>
    public TimeSpan ItemYieldDelay { get; set; } = TimeSpan.FromMilliseconds(25);
}
