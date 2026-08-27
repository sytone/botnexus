using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Channels.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Matrix Client-Server API channel adapter. Each configured account is one Matrix user on the
/// homeserver owned by one BotNexus agent, with its own <c>/sync</c> long-poll loop.
/// </summary>
/// <remarks>
/// <para>
/// First vertical slice of #1201. In scope: per-agent account configuration, <c>/sync</c> long
/// polling with since-token continuity, <c>m.room.message</c> send and receive, Markdown to
/// <c>org.matrix.custom.html</c> formatting, streaming via <c>m.replace</c> edits, typing
/// indicators, and auto-join on invite.
/// </para>
/// <para>
/// Explicitly deferred: end-to-end encryption (needs device-key management), federation-specific
/// trust decisions, media upload via the content repository, read receipts, and Spaces mapping.
/// The <see cref="IMatrixClient"/> seam carries only the endpoints this slice uses, so a deferred
/// capability is a missing member rather than a silent no-op.
/// </para>
/// </remarks>
public sealed class MatrixChannelAdapter : ChannelAdapterBase, IStreamEventChannelAdapter
{
    /// <summary>
    /// Configuration section this adapter self-binds from when loaded as a dynamic extension after
    /// the host's initial DI options pass. Follows the <c>channels:&lt;channelType&gt;</c>
    /// convention shared by the Telegram, Service Bus and Agent 365 channel extensions.
    /// </summary>
    internal const string ConfigSection = "channels:matrix";

    /// <summary>
    /// How long the homeserver should hold a typing indicator, in milliseconds. Comfortably longer
    /// than the streaming flush interval so the indicator does not flicker between deltas, and
    /// short enough that a crashed adapter's indicator expires on its own.
    /// </summary>
    private const int TypingTimeoutMs = 20_000;

    private readonly ILogger<MatrixChannelAdapter> _logger;
    private readonly LateBoundChannelOptions<MatrixChannelOptions> _optionsHolder;
    private readonly IMatrixClientFactory _clientFactory;
    private readonly IMatrixSyncCursorStore? _cursorStore;

    // Read at point of use so a runtime config.json edit is reflected without a gateway restart
    // (#2010), matching every other channel extension.
    private MatrixChannelOptions Options => _optionsHolder.Current;

    private readonly ConcurrentDictionary<string, MatrixAccountRuntime> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises the adapter.
    /// </summary>
    /// <param name="logger">Adapter logger.</param>
    /// <param name="optionsAccessor">Options accessor, possibly unbound for dynamic extensions.</param>
    /// <param name="clientFactory">Creates one Matrix client per configured account.</param>
    /// <param name="configuration">
    /// Host configuration used for the late-bound self-bind fallback. Null in unit tests.
    /// </param>
    /// <param name="cursorStore">
    /// Durable home for each account's <c>/sync</c> cursor (#3595). Optional: a host that registers
    /// none keeps the pre-#3595 in-memory-only behaviour rather than failing activation.
    /// </param>
    public MatrixChannelAdapter(
        ILogger<MatrixChannelAdapter> logger,
        IOptions<MatrixChannelOptions> optionsAccessor,
        IMatrixClientFactory clientFactory,
        IConfiguration? configuration = null,
        IMatrixSyncCursorStore? cursorStore = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(clientFactory);

        _logger = logger;
        _clientFactory = clientFactory;
        _cursorStore = cursorStore;
        _optionsHolder = new LateBoundChannelOptions<MatrixChannelOptions>(
            () => ResolveOptions(optionsAccessor, configuration),
            configuration);
    }

    /// <summary>
    /// Resolves options from <see cref="IOptions{T}"/> when populated, falling back to binding from
    /// <see cref="IConfiguration"/> for the dynamic-extension load path that never calls
    /// <c>AddBotNexusMatrixChannel</c>.
    /// </summary>
    private static MatrixChannelOptions ResolveOptions(
        IOptions<MatrixChannelOptions> optionsAccessor,
        IConfiguration? configuration)
    {
        var options = optionsAccessor.Value;
        if (options.Agents.Count == 0 && configuration is not null)
        {
            var bound = new MatrixChannelOptions();
            configuration.GetSection(ConfigSection).Bind(bound);
            return bound;
        }

        return options;
    }

    /// <inheritdoc />
    public override ChannelKey ChannelType => ChannelKey.From("matrix");

    /// <inheritdoc />
    public override string DisplayName => "Matrix";

    /// <summary>Matrix supports streaming via send-then-edit (<c>m.replace</c>).</summary>
    public override bool SupportsStreaming => true;

    /// <inheritdoc />
    public override bool SupportsThinkingDisplay => false;

    /// <inheritdoc />
    public override bool SupportsToolDisplay => false;

    /// <summary>
    /// Inbound image handling requires the Matrix content repository download path, deferred out of
    /// this slice, so the adapter must not advertise a capability it cannot honour.
    /// </summary>
    public override bool SupportsInboundImages => false;

    /// <summary>
    /// Matrix rooms are a user-visible surface, so the delimited internal runtime-context envelope
    /// is redacted from outbound text before delivery (#1430).
    /// </summary>
    protected override bool StripsRuntimeContext => true;

    /// <summary>
    /// Starts a <c>/sync</c> loop for every configured account that is not already running.
    /// </summary>
    /// <remarks>
    /// Each account owns a one-way start latch, so a partially-successful start followed by a retry
    /// starts only the accounts that never got going rather than launching a second sync loop on an
    /// already-live account - the duplicate-poller condition #2447 exists to prevent.
    /// </remarks>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        EnsureAccountsInitialized();

        foreach (var runtime in _accounts.Values)
        {
            if (!runtime.TryBeginStart())
            {
                _logger.LogDebug(
                    "{DisplayName} account '{AccountName}' is already started; skipping (resumed start).",
                    DisplayName,
                    runtime.AccountName);
                continue;
            }

            var committed = false;
            try
            {
                runtime.SyncCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                runtime.SyncTask = Task.Run(
                    () => RunSyncLoopAsync(runtime, runtime.SyncCancellation.Token),
                    CancellationToken.None);
                committed = true;

                _logger.LogInformation(
                    "{DisplayName} account '{AccountName}' sync loop started (UserId: {UserId}, AgentId: {AgentId}, AutoJoin: {AutoJoin})",
                    DisplayName,
                    runtime.AccountName,
                    runtime.Identity.UserId,
                    runtime.AgentId,
                    runtime.Identity.AutoJoin);
            }
            finally
            {
                if (!committed)
                    runtime.AbandonStart();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task OnStopAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in _accounts.Values)
            runtime.SyncCancellation?.Cancel();

        foreach (var runtime in _accounts.Values)
        {
            if (runtime.SyncTask is not null)
            {
                try
                {
                    await runtime.SyncTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            runtime.SyncCancellation?.Dispose();
            runtime.StreamingStates.Clear();
            runtime.AbandonStart();
        }

        _accounts.Clear();
        _logger.LogInformation("{DisplayName} channel adapter stopped", DisplayName);
    }

    /// <summary>
    /// Sends a complete outbound message to the Matrix room encoded in the message's channel
    /// address.
    /// </summary>
    public override async Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAccountsInitialized();

        if (!MatrixChannelAddress.TryDecode(message.ChannelAddress, out var roomId, out var threadRootEventId))
        {
            _logger.LogWarning(
                "{DisplayName} send requested with an undecodable channel address '{ChannelAddress}'; message dropped",
                DisplayName,
                message.ChannelAddress.Value);
            return;
        }

        var runtime = ResolveOutboundAccount(message, roomId);
        if (runtime is null)
        {
            _logger.LogWarning(
                "{DisplayName} send to room '{RoomId}' has no configured account to send from; message dropped",
                DisplayName,
                roomId);
            return;
        }

        if (!runtime.Identity.IsRoomAllowed(roomId))
        {
            _logger.LogWarning(
                "{DisplayName} account '{AccountName}' refused a send to room '{RoomId}' that is not in its allow-list",
                DisplayName,
                runtime.AccountName,
                roomId);
            return;
        }

        var text = ProjectOutboundText(message.Content) ?? string.Empty;
        if (!string.IsNullOrEmpty(message.DisplayPrefix))
            text = message.DisplayPrefix + text;

        var content = MatrixMessageFormatter.BuildTextMessage(text, threadRootEventId);
        await runtime.Client.SendMessageAsync(roomId, content, cancellationToken);

        // The turn produced its final message, so the typing indicator has served its purpose.
        await TrySetTypingAsync(runtime, roomId, typing: false, cancellationToken);
    }

    /// <summary>
    /// Streams an incremental delta by sending one message on the first delta and editing it in
    /// place with <c>m.replace</c> thereafter, rate-limited by the configured streaming buffer.
    /// </summary>
    public override async Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAccountsInitialized();

        if (string.IsNullOrEmpty(delta))
            return;

        if (!MatrixChannelAddress.TryDecode(target.ChannelAddress, out var roomId, out var threadRootEventId))
            return;

        var runtime = ResolveStreamingAccount(roomId);
        if (runtime is null || !runtime.Identity.IsRoomAllowed(roomId))
            return;

        var stateKey = target.ChannelRequestId ?? target.ChannelAddress.Value;
        var state = runtime.StreamingStates.GetOrAdd(stateKey, _ => new MatrixStreamingState(roomId, threadRootEventId));

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            state.Buffer.Append(delta);

            var bufferMs = Options.ResolveStreamingBufferMs();
            var elapsed = DateTimeOffset.UtcNow - state.LastFlushUtc;
            if (state.RootEventId is not null && elapsed < TimeSpan.FromMilliseconds(bufferMs))
                return;

            await FlushStreamingStateAsync(runtime, state, cancellationToken);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    /// <inheritdoc />
    public bool CanSendStreamEvent(ChannelStreamTarget target) =>
        target is not null && MatrixChannelAddress.TryDecode(target.ChannelAddress, out _, out _);

    /// <summary>
    /// Handles a structured stream event: content deltas extend the in-place edit, and completion
    /// flushes the final text and clears the typing indicator.
    /// </summary>
    public async Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(streamEvent);
        EnsureAccountsInitialized();

        if (!MatrixChannelAddress.TryDecode(target.ChannelAddress, out var roomId, out _))
            return;

        var runtime = ResolveStreamingAccount(roomId);
        if (runtime is null || !runtime.Identity.IsRoomAllowed(roomId))
            return;

        switch (streamEvent.Type)
        {
            case AgentStreamEventType.ContentDelta when !string.IsNullOrEmpty(streamEvent.ContentDelta):
                await SendStreamDeltaAsync(target, streamEvent.ContentDelta!, cancellationToken);
                break;

            case AgentStreamEventType.RunEnded:
                await FinalizeStreamAsync(runtime, target, roomId, cancellationToken);
                break;

            default:
                // Thinking and tool events are not rendered: the adapter reports
                // SupportsThinkingDisplay and SupportsToolDisplay as false, so surfacing them here
                // would contradict the capability the gateway routed on. RunEnded - not MessageEnd -
                // is the finalisation signal, because MessageEnd fires between tool cycles while the
                // loop is still producing text.
                break;
        }
    }

    /// <summary>
    /// Flushes any buffered stream text, removes the accumulator, and clears the typing indicator.
    /// </summary>
    private async Task FinalizeStreamAsync(
        MatrixAccountRuntime runtime,
        ChannelStreamTarget target,
        string roomId,
        CancellationToken cancellationToken)
    {
        var stateKey = target.ChannelRequestId ?? target.ChannelAddress.Value;
        if (runtime.StreamingStates.TryRemove(stateKey, out var state))
        {
            await state.Lock.WaitAsync(cancellationToken);
            try
            {
                if (state.Buffer.Length > 0)
                    await FlushStreamingStateAsync(runtime, state, cancellationToken);
            }
            finally
            {
                state.Lock.Release();
            }
        }

        await TrySetTypingAsync(runtime, roomId, typing: false, cancellationToken);
    }

    /// <summary>
    /// Sends the accumulated stream text, creating the message on the first flush and editing it
    /// in place with <c>m.replace</c> on every subsequent flush.
    /// </summary>
    /// <remarks>
    /// Caller must hold <see cref="MatrixStreamingState.Lock"/>.
    /// </remarks>
    private async Task FlushStreamingStateAsync(
        MatrixAccountRuntime runtime,
        MatrixStreamingState state,
        CancellationToken cancellationToken)
    {
        var text = state.Buffer.ToString();
        if (string.IsNullOrEmpty(text))
            return;

        var projected = ProjectOutboundText(text) ?? string.Empty;

        if (state.RootEventId is null)
        {
            var content = MatrixMessageFormatter.BuildTextMessage(projected, state.ThreadRootEventId);
            state.RootEventId = await runtime.Client.SendMessageAsync(state.RoomId, content, cancellationToken);
        }
        else
        {
            var content = MatrixMessageFormatter.BuildTextMessage(
                projected,
                state.ThreadRootEventId,
                replacesEventId: state.RootEventId);
            await runtime.Client.SendMessageAsync(state.RoomId, content, cancellationToken);
        }

        state.LastFlushUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Long-polls <c>/sync</c> for one account, dispatching inbound messages and honouring invites,
    /// with a circuit breaker so a terminal fault parks the loop rather than hot-looping on it.
    /// </summary>
    private async Task RunSyncLoopAsync(MatrixAccountRuntime runtime, CancellationToken cancellationToken)
    {
        var breaker = new ChannelLoopCircuitBreaker($"Matrix account '{runtime.AccountName}' sync loop");

        await LoadPersistedCursorAsync(runtime, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var timeoutMs = Options.ResolveSyncTimeoutMs();
                var response = await runtime.Client.SyncAsync(runtime.SinceToken, timeoutMs, cancellationToken);
                breaker.RecordSuccess();

                await ProcessSyncResponseAsync(runtime, response, cancellationToken);

                // Advance the since token only after the batch has been processed, so a crash mid
                // batch replays it rather than skipping the events it contained.
                if (!string.IsNullOrWhiteSpace(response.NextBatch))
                {
                    runtime.SinceToken = response.NextBatch;
                    await PersistCursorAsync(runtime, response.NextBatch!, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var terminal = ex is MatrixApiException { IsTerminal: true };
                var failure = breaker.RecordFailure(terminal ? new InvalidOperationException(ex.Message, ex) : ex);

                if (failure.ShouldStop)
                {
                    if (failure.CircuitOpened)
                    {
                        _logger.LogError(
                            ex,
                            "{DisplayName} account '{AccountName}' sync loop is DEGRADED and has stopped: a non-transient failure was detected and will not clear by retrying. Likely causes are a revoked or invalid access token (HTTP 401/403), a homeserver URL that does not resolve, or an unrecognised fault the classifier fails closed on - see the exception below for which. Resolve the underlying fault and restart the channel.",
                            DisplayName,
                            runtime.AccountName);
                    }

                    break;
                }

                _logger.LogWarning(
                    ex,
                    "{DisplayName} account '{AccountName}' sync loop transient error (failure {FailureCount}); retrying in {RetryDelaySeconds}s",
                    DisplayName,
                    runtime.AccountName,
                    breaker.ConsecutiveTransientFailures,
                    failure.RetryDelay.TotalSeconds);

                try
                {
                    await Task.Delay(failure.RetryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Seeds the account's in-memory cursor from the durable store before the first <c>/sync</c>,
    /// so a restart resumes from the last fully-processed <c>next_batch</c> instead of performing a
    /// fresh initial sync (#3595).
    /// </summary>
    /// <remarks>
    /// A read failure is not fatal: the loop falls back to an initial sync, which is exactly the
    /// pre-#3595 behaviour, rather than refusing to start the channel over a cursor cache miss.
    /// </remarks>
    private async Task LoadPersistedCursorAsync(MatrixAccountRuntime runtime, CancellationToken cancellationToken)
    {
        if (_cursorStore is null)
            return;

        try
        {
            var stored = await _cursorStore.GetAsync(runtime.AgentId, runtime.AccountName, cancellationToken);
            if (string.IsNullOrWhiteSpace(stored))
                return;

            runtime.SinceToken = stored;
            _logger.LogInformation(
                "{DisplayName} account '{AccountName}' resumed /sync from its persisted since-token",
                DisplayName,
                runtime.AccountName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{DisplayName} account '{AccountName}' could not read its persisted since-token; falling back to an initial sync",
                DisplayName,
                runtime.AccountName);
        }
    }

    /// <summary>
    /// Records the account's cursor after the batch that produced it has been fully processed.
    /// </summary>
    /// <remarks>
    /// A write failure degrades to in-memory continuity and must never stop the sync loop: the
    /// in-memory token has already advanced, so the process keeps making forward progress and only
    /// loses restart continuity. Throwing here would turn a cursor-persistence hiccup into a dead
    /// channel, which is strictly worse than the bug this store exists to fix.
    /// </remarks>
    private async Task PersistCursorAsync(MatrixAccountRuntime runtime, string sinceToken, CancellationToken cancellationToken)
    {
        if (_cursorStore is null)
            return;

        try
        {
            await _cursorStore.SetAsync(runtime.AgentId, runtime.AccountName, sinceToken, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{DisplayName} account '{AccountName}' failed to persist its since-token; sync continues with in-memory continuity only",
                DisplayName,
                runtime.AccountName);
        }
    }

    /// <summary>
    /// Processes one sync batch: accepts invites when auto-join is enabled, then dispatches every
    /// eligible <c>m.room.message</c> event.
    /// </summary>
    internal async Task ProcessSyncResponseAsync(
        MatrixAccountRuntime runtime,
        MatrixSyncResponse response,
        CancellationToken cancellationToken)
    {
        if (response.Rooms?.Invite is { Count: > 0 } invites && runtime.Identity.AutoJoin)
        {
            foreach (var roomId in invites.Keys)
            {
                if (!runtime.Identity.IsRoomAllowed(roomId))
                {
                    _logger.LogDebug(
                        "{DisplayName} account '{AccountName}' ignored an invite to room '{RoomId}' that is not in its allow-list",
                        DisplayName,
                        runtime.AccountName,
                        roomId);
                    continue;
                }

                try
                {
                    await runtime.Client.JoinRoomAsync(roomId, cancellationToken);
                    _logger.LogInformation(
                        "{DisplayName} account '{AccountName}' auto-joined room '{RoomId}' on invite",
                        DisplayName,
                        runtime.AccountName,
                        roomId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed join must not abort the batch: the joined-room timeline in the same
                    // response still carries real user messages that would otherwise be dropped.
                    _logger.LogWarning(
                        ex,
                        "{DisplayName} account '{AccountName}' failed to auto-join room '{RoomId}'",
                        DisplayName,
                        runtime.AccountName,
                        roomId);
                }
            }
        }

        if (response.Rooms?.Join is not { Count: > 0 } joined)
            return;

        foreach (var (roomId, room) in joined)
        {
            var events = room.Timeline?.Events;
            if (events is null || events.Count == 0)
                continue;

            foreach (var evt in events)
                await HandleEventAsync(runtime, roomId, evt, cancellationToken);
        }
    }

    /// <summary>
    /// Dispatches a single timeline event when it is an actionable inbound user message.
    /// </summary>
    private async Task HandleEventAsync(
        MatrixAccountRuntime runtime,
        string roomId,
        MatrixEvent evt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(evt.Type, "m.room.message", StringComparison.Ordinal))
            return;

        var sender = evt.Sender;
        if (string.IsNullOrWhiteSpace(sender))
            return;

        // Echo suppression. The account's own messages come back on its next sync; dispatching them
        // would feed the agent its own output and loop.
        if (string.Equals(sender, runtime.Identity.UserId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!runtime.Identity.IsRoomAllowed(roomId))
        {
            _logger.LogDebug(
                "{DisplayName} account '{AccountName}' ignored a message from room '{RoomId}' that is not in its allow-list",
                DisplayName,
                runtime.AccountName,
                roomId);
            return;
        }

        if (!runtime.Identity.IsUserAllowed(sender))
        {
            _logger.LogDebug(
                "{DisplayName} account '{AccountName}' ignored a message from unauthorized sender '{Sender}'",
                DisplayName,
                runtime.AccountName,
                sender);
            return;
        }

        var content = evt.Content;
        if (content is null)
            return;

        // Edits of an earlier message are not new user turns. Dispatching one would replay a turn
        // the agent has already answered.
        if (string.Equals(content.RelatesTo?.RelType, "m.replace", StringComparison.Ordinal))
            return;

        // This slice handles text-shaped messages only; media requires the content repository,
        // which is deferred. Anything else is skipped rather than dispatched as an empty turn.
        if (!string.Equals(content.MsgType, "m.text", StringComparison.Ordinal)
            && !string.Equals(content.MsgType, "m.notice", StringComparison.Ordinal)
            && !string.Equals(content.MsgType, "m.emote", StringComparison.Ordinal))
        {
            return;
        }

        var body = content.Body;
        if (string.IsNullOrWhiteSpace(body))
            return;

        var threadRootEventId = string.Equals(content.RelatesTo?.RelType, "m.thread", StringComparison.Ordinal)
            ? content.RelatesTo!.EventId
            : null;

        // The indicator is best-effort feedback; a homeserver that rejects it must not stop the
        // message reaching the agent.
        await TrySetTypingAsync(runtime, roomId, typing: true, cancellationToken);

        await DispatchInboundAsync(
            new InboundMessage
            {
                ChannelType = ChannelType,
                SenderId = sender,
                Sender = CitizenId.Of(UserId.From(sender)),
                ChannelAddress = MatrixChannelAddress.Encode(roomId, threadRootEventId),
                Content = body,
                Timestamp = evt.OriginServerTs is { } ts
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ts)
                    : DateTimeOffset.UtcNow,
                RoutingHints = InboundMessageRoutingHints.LiftFromStrings(
                    targetAgentId: runtime.AgentId,
                    sessionId: null,
                    conversationId: null),
                Metadata = new Dictionary<string, object?>
                {
                    ["matrixAccountKey"] = runtime.AccountName,
                    ["matrixRoomId"] = roomId,
                    ["matrixEventId"] = evt.EventId,
                    ["matrixSender"] = sender,
                },
            },
            cancellationToken);
    }

    /// <summary>
    /// Sets the typing indicator, swallowing failures. Typing state is cosmetic; a homeserver that
    /// rejects it must never fail the surrounding send or dispatch.
    /// </summary>
    private async Task TrySetTypingAsync(
        MatrixAccountRuntime runtime,
        string roomId,
        bool typing,
        CancellationToken cancellationToken)
    {
        try
        {
            await runtime.Client.SetTypingAsync(roomId, typing, TypingTimeoutMs, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "{DisplayName} account '{AccountName}' failed to set typing={Typing} in room '{RoomId}'",
                DisplayName,
                runtime.AccountName,
                typing,
                roomId);
        }
    }

    /// <summary>
    /// Chooses the account an outbound message is sent from: the account owning the target agent
    /// when the message names one, otherwise the account that most recently saw the room, otherwise
    /// the single configured account.
    /// </summary>
    private MatrixAccountRuntime? ResolveOutboundAccount(OutboundMessage message, string roomId)
    {
        if (message.Metadata.TryGetValue("matrixAccountKey", out var accountName)
            && accountName is string key
            && _accounts.TryGetValue(key, out var byKey))
        {
            return byKey;
        }

        return ResolveStreamingAccount(roomId);
    }

    /// <summary>
    /// Chooses the account responsible for a room. Prefers an account whose allow-list explicitly
    /// names the room, then falls back to the sole configured account.
    /// </summary>
    /// <remarks>
    /// Multi-account routing by live room membership needs a membership cache the first slice does
    /// not build; with one account configured (the documented starting shape) this is exact, and
    /// with several it prefers an explicit allow-list entry over a guess.
    /// </remarks>
    private MatrixAccountRuntime? ResolveStreamingAccount(string roomId)
    {
        foreach (var runtime in _accounts.Values)
        {
            if (runtime.Identity.ExplicitlyOwnsRoom(roomId))
                return runtime;
        }

        return _accounts.Count == 1 ? _accounts.Values.First() : null;
    }

    /// <summary>
    /// Materialises one runtime per configured account, skipping entries missing the credentials
    /// they need rather than failing the whole adapter for one bad entry.
    /// </summary>
    /// <remarks>
    /// The raw access token is wrapped in a <see cref="MatrixAccessToken"/> the moment it is read
    /// and is handed straight to the client factory. It is never held in a local alongside the
    /// account key, never stored on the runtime record, and cannot be rendered into the warning
    /// below - see <see cref="MatrixAccessToken"/> and <see cref="MatrixAccountIdentity"/> for why
    /// (CodeQL <c>cs/cleartext-storage-of-sensitive-information</c>, alert 110).
    /// </remarks>
    private void EnsureAccountsInitialized()
    {
        if (!_accounts.IsEmpty)
            return;

        var options = Options;

        // Iterate the KEY COLLECTION, not the key/value pairs. CodeQL taints the whole
        // KeyValuePair<string, MatrixAccountConfig> because its value type carries AccessToken, so
        // a key destructured from the pair inherits that taint and every later use of it - including
        // the perfectly innocuous warning below - is reported as clear-text storage of a secret
        // (cs/cleartext-storage-of-sensitive-information, alert 110). Reading the key from
        // Keys and looking the config up separately means the account key never derives from an
        // expression that touches the credential, which is both what the analyser needs and an
        // honest statement of the data flow: the key is a configuration label, not a secret.
        foreach (var accountName in options.Agents.Keys.ToArray())
        {
            if (!options.Agents.TryGetValue(accountName, out var config) || config is null)
                continue;

            var homeserver = string.IsNullOrWhiteSpace(config.Homeserver) ? options.Homeserver : config.Homeserver;

            // Wrap before any branching so the credential exists only as a redacting value type from
            // here on. A `default` token means "absent", which is what the completeness check reads.
            _ = MatrixAccessToken.TryCreate(config.AccessToken, out var accessToken);

            if (string.IsNullOrWhiteSpace(homeserver)
                || string.IsNullOrWhiteSpace(config.UserId)
                || !accessToken.HasValue)
            {
                _logger.LogWarning(
                    "{DisplayName} account '{AccountName}' is incomplete (homeserver, user ID and access token are all required) and was skipped",
                    DisplayName,
                    accountName);
                continue;
            }

            var client = _clientFactory.Create(accountName, homeserver, config.UserId, accessToken);
            var agentId = string.IsNullOrWhiteSpace(config.AgentId) ? accountName : config.AgentId;

            // Project to a token-free identity before storing. The token has now served its only
            // purpose - constructing the client, which owns it as an Authorization header - so
            // retaining the raw config on a process-lifetime record would keep a live credential
            // reachable from adapter state for no functional reason.
            _accounts.TryAdd(
                accountName,
                new MatrixAccountRuntime(accountName, agentId, MatrixAccountIdentity.FromConfig(config), client));
        }
    }

    /// <summary>Number of materialised account runtimes. Test observability only.</summary>
    internal int GetAccountCount()
    {
        EnsureAccountsInitialized();
        return _accounts.Count;
    }

    /// <summary>Looks up a materialised account runtime by key. Test observability only.</summary>
    internal MatrixAccountRuntime? GetAccount(string accountName)
    {
        EnsureAccountsInitialized();
        return _accounts.TryGetValue(accountName, out var runtime) ? runtime : null;
    }
}
