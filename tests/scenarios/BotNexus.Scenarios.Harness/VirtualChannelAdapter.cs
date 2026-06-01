using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Scenarios.Harness;

/// <summary>
/// A fully in-memory <see cref="IChannelAdapter"/> used by scenario tests to drive citizen
/// interactions without bringing up a real channel (SignalR, Telegram, Service Bus, ...).
/// </summary>
/// <remarks>
/// <para>
/// The adapter is intentionally passive: it carries inbound <see cref="ChannelAddress"/> verbatim
/// from the test, so multi-binding / multi-conversation scenarios are driven from the test
/// rather than faked inside the adapter. It captures every outbound message and stream event
/// into ordered logs that scenario assertions read from.
/// </para>
/// <para>
/// Each capability flag (streaming, steering, follow-up, thinking, tool, inbound images) is
/// configurable per instance via <see cref="VirtualChannelAdapterOptions"/> so capability-gating
/// scenarios can drive the router with a channel that lacks a specific capability.
/// </para>
/// <para>
/// Use <see cref="SimulateInboundAsync(InboundMessage, CancellationToken)"/> to drive an
/// inbound message through the gateway's <see cref="IChannelDispatcher"/>. The adapter must be
/// started (<see cref="ChannelAdapterBase.StartAsync"/>) before inbound dispatch will succeed.
/// </para>
/// <para>
/// Re-listing <see cref="IChannelAdapter"/> in the class declaration is intentional: it allows
/// the explicit-interface implementation of <see cref="IChannelAdapter.AdapterId"/> below to
/// override the interface's default implementation. <see cref="ChannelAdapterBase"/> does not
/// itself override <see cref="IChannelAdapter.AdapterId"/>, so the explicit impl is currently
/// the only way for a scenario to set a stable adapter id when standing up more than one virtual
/// adapter under the same channel type. If a future refactor adds a virtual or abstract
/// <c>AdapterId</c> to <see cref="ChannelAdapterBase"/>, replace this pattern with a normal
/// override -- the <c>VirtualChannelAdapter_ExposesAdapterId_ViaInterface</c> conformance test
/// will fail loudly if the two surfaces drift.
/// </para>
/// </remarks>
public sealed class VirtualChannelAdapter : ChannelAdapterBase, IChannelAdapter, IStreamEventChannelAdapter
{
    /// <summary>The canonical channel type identifier used by all virtual adapters.</summary>
    public const string VirtualChannelType = "virtual";

    private static readonly ChannelKey VirtualChannelKey = ChannelKey.From(VirtualChannelType);

    private readonly ConcurrentQueue<OutboundMessage> _outbound = new();
    private readonly ConcurrentQueue<InboundMessage> _inbound = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _streamDeltas = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentStreamEvent>> _streamEvents = new();
    private readonly ConcurrentQueue<ChannelStreamTarget> _streamTargets = new();
    private readonly VirtualChannelAdapterOptions _options;
    private int _dispatchCount;

    /// <summary>
    /// Creates a virtual channel adapter with default capabilities (streaming, steering,
    /// follow-up on; thinking, tool, inbound images off).
    /// </summary>
    public VirtualChannelAdapter()
        : this(new VirtualChannelAdapterOptions(), NullLogger<VirtualChannelAdapter>.Instance)
    {
    }

    /// <summary>
    /// Creates a virtual channel adapter with explicit capability and identity options.
    /// </summary>
    public VirtualChannelAdapter(VirtualChannelAdapterOptions options, ILogger<VirtualChannelAdapter>? logger = null)
        : base(logger ?? NullLogger<VirtualChannelAdapter>.Instance)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override ChannelKey ChannelType => VirtualChannelKey;

    /// <inheritdoc />
    public override string DisplayName => string.IsNullOrWhiteSpace(_options.DisplayName)
        ? "Virtual"
        : _options.DisplayName;

    /// <summary>
    /// Optional adapter instance identifier. Set via <see cref="VirtualChannelAdapterOptions.AdapterId"/>
    /// when a single scenario stands up multiple virtual adapters under the same channel type.
    /// </summary>
    string? IChannelAdapter.AdapterId => _options.AdapterId;

    /// <inheritdoc />
    public override bool SupportsStreaming => _options.SupportsStreaming;

    /// <inheritdoc />
    public override bool SupportsSteering => _options.SupportsSteering;

    /// <inheritdoc />
    public override bool SupportsFollowUp => _options.SupportsFollowUp;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => _options.SupportsThinkingDisplay;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => _options.SupportsToolDisplay;

    /// <inheritdoc />
    public override bool SupportsInboundImages => _options.SupportsInboundImages;

    /// <summary>Ordered snapshot of every outbound <see cref="OutboundMessage"/> the gateway sent through this adapter.</summary>
    public IReadOnlyList<OutboundMessage> Outbound => [.. _outbound];

    /// <summary>Ordered snapshot of every inbound <see cref="InboundMessage"/> the test dispatched through this adapter.</summary>
    public IReadOnlyList<InboundMessage> InboundDispatched => [.. _inbound];

    /// <summary>The total number of times <see cref="SimulateInboundAsync"/> reached the gateway dispatcher.</summary>
    public int InboundDispatchCount => Volatile.Read(ref _dispatchCount);

    /// <summary>Stream deltas grouped by the channel-level routing key the gateway used (the target's <see cref="ChannelAddress"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StreamDeltas
        => _streamDeltas.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value.ToArray());

    /// <summary>Structured stream events grouped by the channel-level routing key the gateway used (the target's <see cref="ChannelAddress"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AgentStreamEvent>> StreamEvents
        => _streamEvents.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<AgentStreamEvent>)kvp.Value.ToArray());

    /// <summary>Ordered snapshot of every <see cref="ChannelStreamTarget"/> the gateway routed through this adapter (deltas and events).</summary>
    public IReadOnlyList<ChannelStreamTarget> StreamTargets => [.. _streamTargets];

    /// <summary>
    /// Drives <paramref name="message"/> through the gateway's <see cref="IChannelDispatcher"/>
    /// as if it had arrived from a real channel. The adapter must be started before this is invoked.
    /// </summary>
    /// <param name="message">The inbound message to dispatch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the adapter has not been started.</exception>
    public async Task SimulateInboundAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsRunning)
            throw new InvalidOperationException(
                $"{nameof(VirtualChannelAdapter)} must be started via {nameof(StartAsync)} before {nameof(SimulateInboundAsync)} can be used.");

        _inbound.Enqueue(message);
        Interlocked.Increment(ref _dispatchCount);
        await DispatchInboundAsync(message, cancellationToken);
    }

    /// <summary>
    /// Waits until at least one outbound message matches <paramref name="predicate"/> or the
    /// <paramref name="timeout"/> elapses. Returns the first matching message.
    /// </summary>
    /// <param name="predicate">Selector for the outbound message of interest.</param>
    /// <param name="timeout">Maximum time to wait before giving up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TimeoutException">Thrown when no matching message is observed before the timeout.</exception>
    public async Task<OutboundMessage> WaitForOutboundAsync(
        Func<OutboundMessage, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = _outbound.FirstOrDefault(predicate);
            if (match is not null)
                return match;
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        throw new TimeoutException(
            $"No outbound message matching predicate observed within {timeout.TotalMilliseconds:F0}ms (observed {_outbound.Count}).");
    }

    /// <summary>Clears every captured outbound, inbound, stream-delta, stream-event log, and stream-target list.</summary>
    public void Reset()
    {
        _outbound.Clear();
        _inbound.Clear();
        _streamDeltas.Clear();
        _streamEvents.Clear();
        _streamTargets.Clear();
        Interlocked.Exchange(ref _dispatchCount, 0);
    }

    /// <inheritdoc />
    protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _outbound.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(delta);
        _streamTargets.Enqueue(target);
        _streamDeltas.GetOrAdd(target.ChannelAddress.Value, _ => new ConcurrentQueue<string>()).Enqueue(delta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(streamEvent);
        _streamTargets.Enqueue(target);
        _streamEvents.GetOrAdd(target.ChannelAddress.Value, _ => new ConcurrentQueue<AgentStreamEvent>()).Enqueue(streamEvent);
        return Task.CompletedTask;
    }
}