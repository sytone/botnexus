using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Core;

/// <summary>
/// Represents cross world channel adapter.
/// </summary>
public sealed class CrossWorldChannelAdapter(
    ILogger<CrossWorldChannelAdapter> logger,
    HttpClient httpClient,
    CrossWorldChannelOptions? options = null) : ChannelAdapterBase(logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient;
    private readonly CrossWorldChannelOptions _options = options ?? new CrossWorldChannelOptions();

    public override ChannelKey ChannelType => ChannelKey.From("cross-world");
    public override string DisplayName => "Cross-World Federation";
    public override bool SupportsStreaming => false;
    public override bool SupportsSteering => false;
    public override bool SupportsFollowUp => false;
    public override bool SupportsThinkingDisplay => false;
    public override bool SupportsToolDisplay => false;

    protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes send async.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The send async result.</returns>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        => _ = await ExchangeAsync(message, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Executes exchange async.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exchange async result.</returns>
    public async Task<CrossWorldRelayResponse> ExchangeAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = RequireMetadata(message.Metadata, "endpoint");
        var sourceWorldId = RequireMetadata(message.Metadata, "sourceWorldId");
        var sourceAgentId = RequireMetadata(message.Metadata, "sourceAgentId");
        var targetAgentId = RequireMetadata(message.Metadata, "targetAgentId");
        var conversationId = RequireMetadata(message.Metadata, "conversationId");
        var sourceSessionId = TryGetMetadata(message.Metadata, "sourceSessionId");
        var remoteSessionId = TryGetMetadata(message.Metadata, "remoteSessionId");
        var apiKey = TryGetMetadata(message.Metadata, "apiKey");

        var requestUri = BuildRelayUri(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(new CrossWorldRelayRequest
            {
                SourceWorldId = sourceWorldId,
                SourceAgentId = sourceAgentId,
                TargetAgentId = targetAgentId,
                Message = message.Content,
                ConversationId = conversationId,
                SourceSessionId = sourceSessionId,
                RemoteSessionId = remoteSessionId
            }, options: JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("X-Cross-World-Key", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Cross-world relay failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var relayResponse = await response.Content.ReadFromJsonAsync<CrossWorldRelayResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (relayResponse is null)
            throw new InvalidOperationException("Cross-world relay returned an empty response payload.");

        return relayResponse;
    }

    private Uri BuildRelayUri(string endpoint)
    {
        var normalized = endpoint.TrimEnd('/');
        return new Uri($"{normalized}/{_options.RelayPath.TrimStart('/')}", UriKind.Absolute);
    }

    private static string RequireMetadata(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        var value = TryGetMetadata(metadata, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required cross-world metadata '{key}'.", nameof(metadata));
        return value;
    }

    private static string? TryGetMetadata(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value.ToString();
    }
}

/// <summary>
/// Represents cross world channel options.
/// </summary>
public sealed class CrossWorldChannelOptions
{
    /// <summary>
    /// Gets or sets the relay path.
    /// </summary>
    public string RelayPath { get; set; } = "api/federation/cross-world/relay";
}
