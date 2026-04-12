namespace BotNexus.Gateway.Abstractions.Models;

public sealed class MemoryAgentConfig
{
    public bool Enabled { get; set; }

    public string Indexing { get; set; } = "auto";

    public MemorySearchAgentConfig? Search { get; set; }
}

public sealed class MemorySearchAgentConfig
{
    public int DefaultTopK { get; set; } = 10;

    public TemporalDecayAgentConfig? TemporalDecay { get; set; }
}

public sealed class TemporalDecayAgentConfig
{
    public bool Enabled { get; set; } = true;

    public int HalfLifeDays { get; set; } = 30;
}
