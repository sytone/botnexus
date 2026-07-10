using System.Net.Http;
using BotNexus.Domain.Primitives;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Default <see cref="IAgent365ConnectorSender"/> that delivers reply activities through the
/// Microsoft 365 Agents SDK <see cref="RestConnectorClient"/>, authenticating with an Entra
/// client-credential (confidential-client) MSAL flow.
/// </summary>
/// <remarks>
/// <para>
/// The connector caches per <c>serviceUrl</c>; a token is acquired lazily via MSAL for the
/// <c>{serviceUrl}/.default</c> scope and reused until expiry (MSAL's own in-memory cache handles
/// refresh). This keeps outbound Activity replies flowing without BotNexus modelling channel-service
/// auth itself — the SDK is a pure channel abstraction and the BotNexus loop still generates the
/// content being replied with.
/// </para>
/// </remarks>
public sealed class Agent365ConnectorSender : IAgent365ConnectorSender
{
    private readonly Agent365GatewayOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Agent365ConnectorSender> _logger;
    private readonly IConfidentialClientApplication? _msal;

    /// <summary>
    /// Creates the connector sender. When client credentials are present an MSAL confidential-client
    /// application is built once for token acquisition; when they are absent the sender logs and
    /// no-ops on send so a misconfigured channel never throws into the agent loop.
    /// </summary>
    public Agent365ConnectorSender(
        IOptions<Agent365GatewayOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<Agent365ConnectorSender> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            var builder = ConfidentialClientApplicationBuilder
                .Create(_options.ClientId)
                .WithClientSecret(_options.ClientSecret);

            builder = string.IsNullOrWhiteSpace(_options.TenantId)
                ? builder.WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdMultipleOrgs)
                : builder.WithTenantId(_options.TenantId);

            _msal = builder.Build();
        }
    }

    /// <inheritdoc />
    public async Task SendReplyAsync(string? serviceUrl, string conversationId, Activity activity, CancellationToken cancellationToken)
    {
        var endpoint = serviceUrl ?? _options.ChannelServiceEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("Agent 365 reply dropped: no serviceUrl on the activity and no configured channelServiceEndpoint.");
            return;
        }

        if (_msal is null)
        {
            _logger.LogWarning("Agent 365 reply dropped: clientId/clientSecret are not configured, cannot authenticate the connector.");
            return;
        }

        var baseUri = new Uri(endpoint, UriKind.Absolute);
        var client = new RestConnectorClient(
            baseUri,
            _httpClientFactory,
            () => AcquireTokenAsync(baseUri, cancellationToken),
            namedClient: "agent365",
            maxApxConversationIdLength: null);

        await client.Conversations.ReplyToActivityAsync(activity, cancellationToken);
    }

    // Acquires a client-credential token for the channel service audience. MSAL's in-memory token
    // cache satisfies subsequent calls until the token nears expiry.
    private async Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken)
    {
        var scope = $"{audience.GetLeftPart(UriPartial.Authority)}/.default";
        var result = await _msal!
            .AcquireTokenForClient([scope])
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }
}
