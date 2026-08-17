using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// An opt-in channel adapter that exists so an integration test can participate in a conversation
/// as a real, named, non-portal channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a mock.</b> The existing integration harness can only join a conversation as
/// a SignalR/portal client, so any multi-channel behaviour — cross-channel fan-out, per-binding
/// delivery, channel-specific echo — could previously only be tested against in-memory doubles that
/// bypass the routing pipeline, the adapter lifecycle and the real dispatcher. This adapter is
/// registered through the ordinary extension loader, started by
/// <c>ChannelStartupCoordinator</c> like every other channel, and dispatches through the real
/// <see cref="IChannelDispatcher"/>. Nothing about the pipeline is stubbed; only the transport is
/// replaced by an HTTP surface a test can drive (see <see cref="TestChannelEndpointContributor"/>).
/// </para>
/// <para>
/// <b>Why it is safe.</b> The shipped manifest carries <c>"enabled": false</c>, so
/// <c>LoadConfiguredExtensionsAsync</c> filters it out of every configuration that does not
/// deliberately turn it on. Being loadable and being loaded are separate things, and this adapter is
/// only ever the latter by explicit choice. <c>TestChannelOptInArchitectureTests</c> fails the build
/// if that manifest flag is ever flipped.
/// </para>
/// <para>
/// <b>Configurable channel key.</b> <see cref="TestChannelOptions.ChannelId"/> lets one instance
/// present itself as any channel key. A test that needs to prove "the echo reached the telegram
/// binding" can register this adapter AS <c>telegram</c>; the router, bindings and fan-out then
/// treat it as that channel, which is the only way to exercise those paths without a live bot.
/// </para>
/// </remarks>
public sealed class TestChannelAdapter : ChannelAdapterBase
{
    private readonly ILogger<TestChannelAdapter> _logger;
    private readonly TestChannelOptions _options;

    // Deliveries are grouped by channel address because that is the unit a test polls and clears.
    // A single flat list would force every test to filter, and a test that forgets to filter would
    // pass on another address's message.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TestChannelOutboundRecord>> _outbound =
        new(StringComparer.Ordinal);

    private long _sequence;

    /// <summary>
    /// Configuration section this adapter binds from, following the
    /// <c>channels:&lt;channelType&gt;</c> convention shared by the other channel extensions.
    /// </summary>
    internal const string ConfigSection = "channels:test";

    /// <summary>Creates the adapter.</summary>
    /// <param name="logger">Adapter logger.</param>
    /// <param name="optionsAccessor">Bound options; defaults apply when nothing is configured.</param>
    public TestChannelAdapter(
        ILogger<TestChannelAdapter> logger,
        IOptions<TestChannelOptions> optionsAccessor)
        : base(logger)
    {
        _logger = logger;
        _options = optionsAccessor.Value;
    }

    /// <inheritdoc/>
    public override ChannelKey ChannelType => ChannelKey.From(_options.ChannelId);

    /// <inheritdoc/>
    public override string DisplayName => _options.DisplayName;

    /// <inheritdoc/>
    /// <remarks>
    /// Streaming is claimed so the adapter RECORDS deltas rather than having the gateway skip it.
    /// A test channel that silently opted out of streaming would make streaming behaviour
    /// untestable through it, which defeats the purpose.
    /// </remarks>
    public override bool SupportsStreaming => true;

    /// <inheritdoc/>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Test channel adapter started as channel '{ChannelType}'; inbound injection and outbound capture are available over HTTP",
            ChannelType.Value);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        _outbound.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        Record(
            message.ChannelAddress.Value,
            message.Content,
            message.SessionId,
            message.ConversationId,
            message.BindingId?.ToString(),
            message.SpeakAs?.ToString(),
            message.ResolveKind().ToString(),
            isStreamDelta: false);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task SendStreamDeltaAsync(
        ChannelStreamTarget target,
        string delta,
        CancellationToken cancellationToken = default)
    {
        Record(
            target.ChannelAddress.Value,
            delta,
            target.SessionId.Value,
            target.ConversationId.Value,
            target.BindingId?.ToString(),
            role: null,
            kind: MessageKind.Message.ToString(),
            isStreamDelta: true);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Injects a message into the gateway exactly as if it had arrived over this channel's real
    /// transport, returning whether the adapter was running and able to dispatch it.
    /// </summary>
    /// <remarks>
    /// Returning a bool rather than swallowing the not-running case matters: a test that injects
    /// into a stopped adapter and is told "accepted" would then wait for a reply that can never
    /// come and fail as a timeout, hiding the real cause.
    /// </remarks>
    /// <param name="address">The channel address (chat id, thread id, …) the message arrives on.</param>
    /// <param name="content">The message text.</param>
    /// <param name="senderId">Channel-native sender token; defaults to <c>test-user</c>.</param>
    /// <param name="targetAgentId">Optional agent routing hint.</param>
    /// <param name="conversationId">Optional conversation routing hint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> InjectInboundAsync(
        string address,
        string content,
        string? senderId = null,
        string? targetAgentId = null,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(content);

        if (!IsRunning)
        {
            _logger.LogWarning(
                "Test channel '{ChannelType}' received an inbound injection for '{Address}' before the adapter was started; the message was NOT dispatched",
                ChannelType.Value,
                address);
            return false;
        }

        var sender = string.IsNullOrWhiteSpace(senderId) ? "test-user" : senderId;

        var inbound = new InboundMessage
        {
            ChannelType = ChannelType,
            SenderId = sender,
            Sender = CitizenId.Of(UserId.From(sender)),
            ChannelAddress = ChannelAddress.From(address),
            Content = content,
            Timestamp = DateTimeOffset.UtcNow,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                targetAgentId: targetAgentId,
                sessionId: null,
                conversationId: conversationId),
        };

        await DispatchInboundAsync(inbound, cancellationToken);

        _logger.LogInformation(
            "Test channel '{ChannelType}' dispatched an injected inbound message on address '{Address}'",
            ChannelType.Value,
            address);

        return true;
    }

    /// <summary>Returns the captured deliveries for one address, in capture order.</summary>
    /// <param name="address">The channel address to read.</param>
    public IReadOnlyList<TestChannelOutboundRecord> GetOutbound(string address)
        => _outbound.TryGetValue(address, out var queue) ? [.. queue] : [];

    /// <summary>Returns every captured delivery across all addresses, ordered by capture sequence.</summary>
    public IReadOnlyList<TestChannelOutboundRecord> GetAllOutbound()
        => [.. _outbound.Values.SelectMany(queue => queue).OrderBy(record => record.Sequence)];

    /// <summary>Clears the captured deliveries for one address.</summary>
    /// <param name="address">The channel address to clear.</param>
    /// <returns>The number of records removed.</returns>
    public int ClearOutbound(string address)
        => _outbound.TryRemove(address, out var queue) ? queue.Count : 0;

    private void Record(
        string address,
        string content,
        string? sessionId,
        string? conversationId,
        string? bindingId,
        string? role,
        string kind,
        bool isStreamDelta)
    {
        var record = new TestChannelOutboundRecord(
            address,
            content,
            sessionId,
            conversationId,
            bindingId,
            role,
            kind,
            isStreamDelta,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow);

        _outbound
            .GetOrAdd(address, _ => new ConcurrentQueue<TestChannelOutboundRecord>())
            .Enqueue(record);

        _logger.LogInformation(
            "Test channel '{ChannelType}' captured an outbound delivery to address '{Address}' (streamDelta={IsStreamDelta}, sequence={Sequence})",
            ChannelType.Value,
            address,
            isStreamDelta,
            record.Sequence);
    }
}
