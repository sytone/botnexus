using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Channels.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Channels;

/// <summary>
/// Base class for channel adapters that provides common lifecycle management,
/// allow-list enforcement, and logging.
/// </summary>
/// <remarks>
/// Subclasses implement <see cref="OnStartAsync"/> and <see cref="OnStopAsync"/>
/// for protocol-specific connection management, and override <see cref="SendAsync"/>
/// and <see cref="SendStreamDeltaAsync"/> for outbound message delivery.
/// </remarks>
public abstract class ChannelAdapterBase : IChannelAdapter
{
    protected readonly ILogger Logger;
    private IChannelDispatcher? _dispatcher;
    private bool _isRunning;

    private IReadOnlyList<string> _allowList = [];
    private HashSet<string> _allowSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allow-list of sender IDs. If empty, all senders are allowed.
    /// </summary>
    /// <remarks>
    /// #3593: the list is retained for shape (count, enumeration, diagnostics) but membership is
    /// tested through <see cref="IsSenderAllowed"/> against an <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// set. Testing <c>IReadOnlyList&lt;string&gt;.Contains</c> directly resolves to the default
    /// ordinal comparer, which silently drops a legitimate sender whose channel-reported identifier
    /// differs only in case from the configured entry.
    /// </remarks>
    protected IReadOnlyList<string> AllowList
    {
        get => _allowList;
        init
        {
            _allowList = value ?? [];
            _allowSet = new HashSet<string>(_allowList, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Decides whether an inbound sender identifier satisfies the configured allow-list.
    /// </summary>
    /// <remarks>
    /// Default semantics are case-insensitive ordinal, because every identifier shape that actually
    /// reaches this seam is case-insensitive in its own namespace: SMTP addresses (ServiceBus,
    /// Agent365), Matrix MXIDs, Telegram <c>@handles</c>, and the synthetic ids used by the Test and
    /// Tui adapters. A channel whose identifiers are genuinely case-significant must override this
    /// method explicitly rather than relying on an implicit comparer.
    /// </remarks>
    protected virtual bool IsSenderAllowed(string senderId) =>
        _allowSet.Count == 0 || (senderId is not null && _allowSet.Contains(senderId));

    protected ChannelAdapterBase(ILogger logger) => Logger = logger;

    /// <inheritdoc />
    public abstract ChannelKey ChannelType { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public virtual bool SupportsStreaming => false;

    /// <inheritdoc />
    public virtual bool SupportsSteering => false;

    /// <inheritdoc />
    public virtual bool SupportsFollowUp => false;

    /// <inheritdoc />
    public virtual bool SupportsThinkingDisplay => false;

    /// <inheritdoc />
    public virtual bool SupportsToolDisplay => false;

    /// <inheritdoc />
    public virtual bool SupportsInboundImages => false;

    /// <inheritdoc />
    public virtual bool SupportsInteractivePrompts => false;

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Whether this adapter is a user-visible surface that must redact the delimited internal
    /// runtime-context envelope from outbound text before delivery (#1430).
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/> so internal, agent-to-agent and transcript-shaped
    /// surfaces keep the block (they legitimately want it for debugging and self-diagnosis).
    /// Concrete user-facing adapters (SignalR/portal, Telegram, TUI) opt in by overriding this.
    /// The strip itself fails closed - see <see cref="RuntimeContextRedactor"/>.
    /// </remarks>
    protected virtual bool StripsRuntimeContext => false;

    /// <summary>
    /// Projects assistant text onto this channel's user-visible surface, applying the guarded
    /// runtime-context strip when <see cref="StripsRuntimeContext"/> is enabled. Returns the input
    /// unchanged for internal surfaces and whenever no BEGIN delimiter is present; the strip itself
    /// fails closed on malformed delimiters (#2520).
    /// </summary>
    /// <param name="text">The outbound text about to be written to the channel.</param>
    /// <returns>The channel-projected text.</returns>
    [return: NotNullIfNotNull(nameof(text))]
    protected string? ProjectOutboundText(string? text)
        => StripsRuntimeContext ? RuntimeContextRedactor.Strip(text) : text;

    /// <inheritdoc />
    public async Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.start", ActivityKind.Server);
        activity?.SetTag("botnexus.channel.type", ChannelType);
        activity?.SetTag("botnexus.correlation.id", Activity.Current?.TraceId.ToString());

        _dispatcher = dispatcher;
        await OnStartAsync(cancellationToken);
        _isRunning = true;
        Logger.LogInformation("Channel adapter '{ChannelType}' started", ChannelType);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using var activity = ChannelDiagnostics.Source.StartActivity("channel.stop", ActivityKind.Internal);
        activity?.SetTag("botnexus.channel.type", ChannelType);
        activity?.SetTag("botnexus.correlation.id", Activity.Current?.TraceId.ToString());

        await OnStopAsync(cancellationToken);
        _isRunning = false;
        _dispatcher = null;
        Logger.LogInformation("Channel adapter '{ChannelType}' stopped", ChannelType);
    }

    /// <inheritdoc />
    public abstract Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatches an inbound message to the Gateway routing pipeline.
    /// Checks the allow-list before dispatching.
    /// </summary>
    protected async Task DispatchInboundAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (!IsSenderAllowed(message.SenderId))
        {
            // #3501 AC4: this is a total blackhole for the blocked sender. At LogDebug a wrong
            // non-empty allow-list drops every message with no operator-visible signal at all,
            // which is indistinguishable from the channel being dead. Warn instead, and name the
            // configured entries so the misconfiguration is diagnosable from the log line alone.
            Logger.LogWarning(
                "Blocked message from '{SenderId}' — not in allow list for '{ChannelType}' (allow list has {AllowListCount} entries)",
                message.SenderId,
                ChannelType,
                AllowList.Count);
            return;
        }

        if (_dispatcher is null)
        {
            Logger.LogWarning("Channel '{ChannelType}' received message but no dispatcher is registered", ChannelType);
            return;
        }

        await _dispatcher.DispatchAsync(message, cancellationToken);
    }

    /// <summary>
    /// Protocol-specific startup logic. Called by <see cref="StartAsync"/>.
    /// </summary>
    protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Protocol-specific shutdown logic. Called by <see cref="StopAsync"/>.
    /// </summary>
    protected abstract Task OnStopAsync(CancellationToken cancellationToken);
}
