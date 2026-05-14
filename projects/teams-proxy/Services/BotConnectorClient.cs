using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.TeamsProxy.Configuration;
using BotNexus.TeamsProxy.Models;
using Microsoft.Extensions.Options;

namespace BotNexus.TeamsProxy.Services;

public sealed class BotConnectorClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BotConnectorClient> _logger;
    private readonly TeamsProxyOptions _options;
    private readonly ConnectorTokenProvider _tokenProvider;

    public BotConnectorClient(
        HttpClient httpClient,
        ConnectorTokenProvider tokenProvider,
        IOptions<TeamsProxyOptions> options,
        ILogger<BotConnectorClient> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(
        BotNexusOutboundMessage outboundMessage,
        CancellationToken cancellationToken)
    {
        var validationError = outboundMessage.GetValidationError();
        if (validationError is not null)
        {
            throw new InvalidOperationException(validationError);
        }

        var serviceUrl = ValidateServiceUrl(outboundMessage.ServiceUrl!);
        var requestUri = BuildRequestUri(
            serviceUrl,
            outboundMessage.ConversationId!,
            outboundMessage.ReplyToActivityId);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await _tokenProvider.GetTokenAsync(cancellationToken));
        var activity = NormalizeActivity(outboundMessage);
        var activityJson = JsonSerializer.Serialize(activity, JsonDefaults.Options);
        request.Content = JsonContent.Create(
            activity,
            options: JsonDefaults.Options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Bot Connector rejected outbound activity for conversation {ConversationId}. RequestUri={RequestUri}; ActivityJson={ActivityJson}; Response={ResponseBody}",
                outboundMessage.ConversationId,
                requestUri,
                activityJson,
                body);
            throw new HttpRequestException(
                $"Bot Connector returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                response.StatusCode);
        }

        _logger.LogInformation(
            "Sent Teams reply for conversation {ConversationId}.",
            outboundMessage.ConversationId);
    }

    private static BotActivity NormalizeActivity(BotNexusOutboundMessage outboundMessage)
    {
        var activity = outboundMessage.ToActivity();
        activity.Type ??= "message";
        activity.ServiceUrl ??= outboundMessage.ServiceUrl;
        activity.ChannelId ??= outboundMessage.ChannelId;
        activity.Locale ??= outboundMessage.Locale;
        activity.Conversation ??= outboundMessage.Conversation
            ?? new ConversationAccount { Id = outboundMessage.ConversationId };

        if (!string.IsNullOrWhiteSpace(outboundMessage.ReplyToActivityId))
        {
            activity.ReplyToId ??= outboundMessage.ReplyToActivityId;
        }

        activity.From ??= outboundMessage.From;
        activity.Recipient ??= outboundMessage.Recipient;

        return activity;
    }

    private Uri ValidateServiceUrl(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Outbound serviceUrl must be an absolute HTTPS URL.");
        }

        if (_options.AllowedServiceUrlHosts.Length > 0
            && !_options.AllowedServiceUrlHosts.Any(allowedHost => HostMatches(uri.Host, allowedHost)))
        {
            throw new InvalidOperationException(
                $"Outbound serviceUrl host '{uri.Host}' is not in TeamsProxy:AllowedServiceUrlHosts.");
        }

        return uri;
    }

    private static bool HostMatches(string host, string allowedHost)
    {
        return string.Equals(host, allowedHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{allowedHost}", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri BuildRequestUri(
        Uri serviceUrl,
        string conversationId,
        string? replyToActivityId)
    {
        var baseUri = new Uri(serviceUrl.ToString().TrimEnd('/') + "/");
        var conversationSegment = Uri.EscapeDataString(conversationId);
        var relativePath = string.IsNullOrWhiteSpace(replyToActivityId)
            ? $"v3/conversations/{conversationSegment}/activities"
            : $"v3/conversations/{conversationSegment}/activities/{Uri.EscapeDataString(replyToActivityId)}";

        return new Uri(baseUri, relativePath);
    }
}
