namespace BotNexus.TeamsProxy.Configuration;

public sealed class TeamsProxyOptions
{
    public const string SectionName = "TeamsProxy";

    /// <summary>
    /// Target BotNexus agent identifier sent in every inbound queue envelope.
    /// Corresponds to the <c>agentId</c> field in the Service Bus inbound envelope.
    /// Leave empty to let BotNexus use its default agent routing.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Optional BotNexus session identifier sent in every inbound queue envelope.
    /// Leave empty to allow BotNexus to create or resolve sessions from the conversation.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    public string BotClientId { get; set; } = string.Empty;

    public string ManagedIdentityClientId { get; set; } = string.Empty;

    public string ServiceBusFullyQualifiedNamespace { get; set; } = string.Empty;

    public string InboundQueueName { get; set; } = "botnexus-inbound";

    public string OutboundQueueName { get; set; } = "botnexus-outbound";

    /// <summary>
    /// Requests Service Bus streaming replies. The sample worker deliberately ignores delta
    /// envelopes and sends only the terminal consolidated response to Teams.
    /// </summary>
    public bool StreamResponses { get; set; }

    public string BotOpenIdMetadataUrl { get; set; } =
        "https://login.botframework.com/v1/.well-known/openidconfiguration";

    public string BotTokenIssuer { get; set; } = "https://api.botframework.com";

    public string ConnectorApiScope { get; set; } = "https://api.botframework.com/.default";

    public bool RequireServiceUrlClaim { get; set; } = true;

    public bool AllowUnauthenticatedRequests { get; set; }

    public string[] AllowedServiceUrlHosts { get; set; } = ["smba.trafficmanager.net"];

    public string[] SkipOutboundServiceUrlHosts { get; set; } = ["webchat.botframework.com"];

    public bool OutboundWorkerEnabled { get; set; } = true;

    public int OutboundMaxConcurrentCalls { get; set; } = 1;
}
