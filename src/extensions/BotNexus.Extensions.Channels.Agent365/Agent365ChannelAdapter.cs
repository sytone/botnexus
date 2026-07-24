using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Microsoft 365 Agents SDK channel adapter (Register tier).
/// </summary>
/// <remarks>
/// <para>
/// Bridges the Agents SDK <see cref="Activity"/> protocol to the BotNexus routing pipeline so an
/// agent's messages can flow in and out of Microsoft 365 surfaces while <b>BotNexus remains the
/// response engine</b>. Inbound activities arrive on an ASP.NET endpoint (see
/// <see cref="Agent365InboundController"/>), are translated by
/// <see cref="Agent365ActivityTranslator.ToInboundMessage"/> and dispatched; outbound messages are
/// translated to reply activities and sent through the SDK connector.
/// </para>
/// <para>
/// Scope is Register tier only: identity blueprint (PBI3), Work IQ (PBI4) and teammate (PBI5) are
/// out of scope. The SDK is a pure channel abstraction — no response generation happens here.
/// </para>
/// </remarks>
public sealed class Agent365ChannelAdapter : ChannelAdapterBase
{
    private readonly LateBoundChannelOptions<Agent365GatewayOptions> _optionsHolder;
    private readonly IAgent365ConnectorSender _connectorSender;

    // Read at point of use so a runtime config.json edit is reflected without a gateway restart (#2010).
    private Agent365GatewayOptions _options => _optionsHolder.Current;

    /// <summary>
    /// Creates the adapter, resolving options from <see cref="IOptions{T}"/> and falling back to the
    /// <c>channels:agent365</c> configuration section when the extension is loaded after the initial
    /// DI registration pass (mirrors the Telegram adapter's late-binding fallback).
    /// </summary>
    public Agent365ChannelAdapter(
        ILogger<Agent365ChannelAdapter> logger,
        IOptions<Agent365GatewayOptions> optionsAccessor,
        IAgent365ConnectorSender connectorSender,
        IConfiguration? configuration = null)
        : base(logger)
    {
        _optionsHolder = new LateBoundChannelOptions<Agent365GatewayOptions>(
            () => ResolveOptions(optionsAccessor, configuration),
            configuration);
        _connectorSender = connectorSender;
    }

    private static Agent365GatewayOptions ResolveOptions(
        IOptions<Agent365GatewayOptions> optionsAccessor,
        IConfiguration? configuration)
    {
        var opts = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(opts.ClientId) && configuration is not null)
        {
            var bound = new Agent365GatewayOptions();
            configuration.GetSection("channels:agent365").Bind(bound);
            if (!string.IsNullOrWhiteSpace(bound.ClientId))
                return bound;
        }

        return opts;
    }

    /// <inheritdoc />
    public override ChannelKey ChannelType => Agent365ActivityTranslator.ChannelKey;

    /// <inheritdoc />
    public override string DisplayName => "Agent 365";

    /// <summary>
    /// The Register tier delivers complete replies only; Activity streaming is wired in a later PBI,
    /// so streaming is reported unsupported and the BotNexus loop buffers the full response.
    /// </summary>
    public override bool SupportsStreaming => false;

    /// <inheritdoc />
    public override bool SupportsSteering => false;

    /// <inheritdoc />
    public override bool SupportsFollowUp => false;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => false;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => false;

    /// <summary>
    /// Inbound image attachments are surfaced as reference content parts for vision-capable models.
    /// </summary>
    public override bool SupportsInboundImages => true;

    /// <summary>
    /// The configured agent binding inbound messages route to, or <c>null</c> for the default router.
    /// </summary>
    internal string? AgentId => _options.AgentId;

    /// <inheritdoc />
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation(
            "{DisplayName} channel adapter started (AgentId: {AgentId}, InboundRoute: {InboundRoute})",
            DisplayName,
            _options.AgentId ?? "<default-router>",
            _options.InboundRoute);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("{DisplayName} channel adapter stopped", DisplayName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Translates an inbound Agents SDK activity and dispatches it into the routing pipeline. Called
    /// by the inbound controller. Non-actionable activities (non-message types, empty messages) are
    /// silently ignored. Returns <see langword="true"/> when a message was dispatched.
    /// </summary>
    public async Task<bool> HandleInboundActivityAsync(Activity activity, CancellationToken cancellationToken)
    {
        var inbound = Agent365ActivityTranslator.ToInboundMessage(activity, _options.AgentId);
        if (inbound is null)
            return false;

        await DispatchInboundAsync(inbound, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Agent365ChannelAddress.TryDecode(message.ChannelAddress, out var conversationId, out var serviceUrl))
        {
            Logger.LogWarning(
                "{DisplayName} send requested with invalid channel address '{ChannelAddress}'",
                DisplayName,
                message.ChannelAddress.Value);
            return;
        }

        var replyToId = TryGetMetadataString(message.Metadata, "agent365ActivityId");
        var activity = Agent365ActivityTranslator.ToReplyActivity(message, replyToId);
        await _connectorSender.SendReplyAsync(serviceUrl, conversationId, activity, cancellationToken);
    }

    private static string? TryGetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
        => metadata.TryGetValue(key, out var raw) && raw?.ToString() is { Length: > 0 } value ? value : null;
}
