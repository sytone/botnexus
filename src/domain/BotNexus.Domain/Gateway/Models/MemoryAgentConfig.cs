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
