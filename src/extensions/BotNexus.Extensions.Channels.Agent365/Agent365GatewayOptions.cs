namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Top-level gateway configuration for the Agent 365 channel extension.
/// </summary>
/// <remarks>
/// <para>
/// The Register tier of the Agent 365 integration (PBI1 of epic #1875) needs only enough
/// configuration to (a) authenticate outbound Activity replies through the Microsoft 365 Agents
/// SDK connector and (b) bind inbound Activities to a single BotNexus agent. Identity blueprint
/// (PBI3), Work IQ (PBI4) and teammate (PBI5) concerns are intentionally out of scope and add no
/// fields here.
/// </para>
/// <para>
/// BotNexus remains the response engine: the SDK is a pure channel abstraction. These options
/// carry the Entra app credentials the connector authenticates with, the channel-service endpoint
/// outbound activities are posted to, and the agent binding.
/// </para>
/// </remarks>
public sealed class Agent365GatewayOptions
{
    /// <summary>
    /// Entra application (client) ID of the registered Agent 365 app. Required to authenticate the
    /// outbound connector and to validate inbound Activity recipients.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Entra application client secret used to acquire tokens for outbound Activity replies. Kept
    /// out of logs; the manifest marks it sensitive.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Entra tenant ID for the app registration. Optional — multi-tenant apps leave this null and
    /// resolve the tenant from the inbound Activity's channel data.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Base URL of the channel service outbound activities are posted to. When null the adapter
    /// falls back to the <c>serviceUrl</c> carried on the inbound Activity, which is the normal
    /// Agents SDK reply flow.
    /// </summary>
    public string? ChannelServiceEndpoint { get; set; }

    /// <summary>
    /// BotNexus agent ID inbound messages route to. In the Register tier a single Agent 365 channel
    /// binds one agent; multi-agent routing arrives with later PBIs.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// HTTP route the Agents SDK message endpoint is hosted on for inbound Activity delivery.
    /// Defaults to <c>/agent365/messages</c>.
    /// </summary>
    public string InboundRoute { get; set; } = "/agent365/messages";
}
