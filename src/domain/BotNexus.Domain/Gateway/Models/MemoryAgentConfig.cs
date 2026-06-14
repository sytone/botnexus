namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents memory agent config.
/// </summary>
public sealed class MemoryAgentConfig
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the optional memory root or file override path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the indexing.
    /// </summary>
    public string Indexing { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the search.
    /// </summary>
    public MemorySearchAgentConfig? Search { get; set; }

    /// <summary>
    /// Gets or sets prompt-memory injection mode: <c>full</c>, <c>summary</c>, or <c>none</c>.
    /// </summary>
    public string? PromptInjection { get; set; } = "full";
}

/// <summary>
/// Represents memory search agent config.
/// </summary>
public sealed class MemorySearchAgentConfig
{
    /// <summary>
    /// Gets or sets the default top k.
    /// </summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of results an agent-supplied <c>topK</c> can request.
    /// Caller-provided values above this ceiling are clamped so a single search cannot fan out
    /// over the entire store; protects against runaway embedding fetches and oversized tool results.
    /// </summary>
    public int MaxTopK { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of entries a <c>memory_get</c> session listing can return.
    /// Caller-provided <c>limit</c> values above this ceiling are clamped to bound the fetch and
    /// the resulting serialized payload.
    /// </summary>
    public int MaxLimit { get; set; } = 100;

    /// <summary>
    /// Gets or sets the temporal decay.
    /// </summary>
    public TemporalDecayAgentConfig? TemporalDecay { get; set; }
}

/// <summary>
/// Represents temporal decay agent config.
/// </summary>
public sealed class TemporalDecayAgentConfig
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the half life days.
    /// </summary>
    public int HalfLifeDays { get; set; } = 30;
}
