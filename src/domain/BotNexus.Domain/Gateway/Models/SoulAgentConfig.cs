namespace BotNexus.Gateway.Abstractions.Models;

public sealed class SoulAgentConfig
{
    public bool Enabled { get; set; }

    public string Timezone { get; set; } = "UTC";

    public string DayBoundary { get; set; } = "00:00";

    public bool ReflectionOnSeal { get; set; }

    public string? ReflectionPrompt { get; set; }
}
