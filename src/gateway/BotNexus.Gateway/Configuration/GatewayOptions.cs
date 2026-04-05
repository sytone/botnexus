namespace BotNexus.Gateway.Configuration;

public sealed class GatewayOptions
{
    /// <summary>
    /// Optional default agent used when no explicit target or session-bound agent is available.
    /// </summary>
    public string? DefaultAgentId { get; set; }
}
