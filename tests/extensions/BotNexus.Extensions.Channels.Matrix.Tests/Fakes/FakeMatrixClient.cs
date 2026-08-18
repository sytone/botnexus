using System.Collections.Concurrent;

namespace BotNexus.Extensions.Channels.Matrix.Tests.Fakes;

/// <summary>
/// A single message the adapter sent to the fake homeserver.
/// </summary>
/// <param name="RoomId">Room the message was sent to.</param>
/// <param name="Content">The content that was sent.</param>
public sealed record SentMessage(string RoomId, MatrixMessageContent Content);

/// <summary>
/// A typing-state change the adapter requested.
/// </summary>
/// <param name="RoomId">Room the state applies to.</param>
/// <param name="Typing">Whether typing was turned on or off.</param>
public sealed record TypingCall(string RoomId, bool Typing);

/// <summary>
/// In-memory <see cref="IMatrixClient"/> standing in for a homeserver. Sync responses are supplied
/// as a scripted queue so a test can drive the adapter's loop deterministically, and every write is
/// recorded for assertion.
/// </summary>
public sealed class FakeMatrixClient : IMatrixClient
{
    private readonly ConcurrentQueue<Func<string?, MatrixSyncResponse>> _syncScript = new();

    /// <summary>Messages the adapter sent, in order.</summary>
    public List<SentMessage> SentMessages { get; } = [];

    /// <summary>Rooms the adapter joined, in order.</summary>
    public List<string> JoinedRooms { get; } = [];

    /// <summary>Typing-state calls the adapter made, in order.</summary>
    public List<TypingCall> TypingCalls { get; } = [];

    /// <summary>The <c>since</c> tokens the adapter supplied on each sync, in order.</summary>
    public List<string?> SinceTokens { get; } = [];

    /// <summary>The <c>timeout</c> values the adapter supplied on each sync, in order.</summary>
    public List<int> SyncTimeouts { get; } = [];

    /// <summary>Number of event IDs minted so far, used to make each send's ID unique.</summary>
    private int _eventCounter;

    /// <summary>When set, <see cref="SendMessageAsync"/> throws this on every call.</summary>
    public Exception? SendFailure { get; set; }

    /// <summary>When set, <see cref="JoinRoomAsync"/> throws this on every call.</summary>
    public Exception? JoinFailure { get; set; }

    /// <summary>When set, <see cref="SetTypingAsync"/> throws this on every call.</summary>
    public Exception? TypingFailure { get; set; }

    /// <summary>Queues one scripted sync response.</summary>
    /// <param name="response">The response to return.</param>
    public void EnqueueSync(MatrixSyncResponse response) => _syncScript.Enqueue(_ => response);

    /// <summary>Queues one scripted sync failure.</summary>
    /// <param name="exception">The exception to throw from the sync call.</param>
    public void EnqueueSyncFailure(Exception exception) =>
        _syncScript.Enqueue(_ => throw exception);

    /// <inheritdoc />
    public Task<MatrixSyncResponse> SyncAsync(string? since, int timeoutMs, CancellationToken cancellationToken)
    {
        SinceTokens.Add(since);
        SyncTimeouts.Add(timeoutMs);

        if (_syncScript.TryDequeue(out var next))
            return Task.FromResult(next(since));

        // The script is exhausted. Block until cancelled rather than returning empty batches in a
        // hot loop, which is what a real long poll does when nothing is happening.
        return Task.Delay(Timeout.Infinite, cancellationToken)
            .ContinueWith(_ => new MatrixSyncResponse(), TaskScheduler.Default);
    }

    /// <inheritdoc />
    public Task<string> SendMessageAsync(string roomId, MatrixMessageContent content, CancellationToken cancellationToken)
    {
        if (SendFailure is not null)
            throw SendFailure;

        lock (SentMessages)
        {
            SentMessages.Add(new SentMessage(roomId, content));
            _eventCounter++;
            return Task.FromResult($"$event{_eventCounter}");
        }
    }

    /// <inheritdoc />
    public Task JoinRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        if (JoinFailure is not null)
            throw JoinFailure;

        lock (JoinedRooms)
            JoinedRooms.Add(roomId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetTypingAsync(string roomId, bool typing, int timeoutMs, CancellationToken cancellationToken)
    {
        if (TypingFailure is not null)
            throw TypingFailure;

        lock (TypingCalls)
            TypingCalls.Add(new TypingCall(roomId, typing));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Factory handing out <see cref="FakeMatrixClient"/> instances, one per account key, so a test can
/// inspect each account's traffic independently.
/// </summary>
public sealed class FakeMatrixClientFactory : IMatrixClientFactory
{
    private readonly ConcurrentDictionary<string, FakeMatrixClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Credentials the adapter passed for each account key.</summary>
    public ConcurrentDictionary<string, (string Homeserver, string UserId, MatrixAccessToken AccessToken)> Credentials { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets (creating if needed) the client for an account key.</summary>
    /// <param name="accountName">The account key.</param>
    public FakeMatrixClient ClientFor(string accountName) =>
        _clients.GetOrAdd(accountName, _ => new FakeMatrixClient());

    /// <inheritdoc />
    public IMatrixClient Create(string accountName, string homeserver, string userId, MatrixAccessToken accessToken)
    {
        Credentials[accountName] = (homeserver, userId, accessToken);
        return ClientFor(accountName);
    }
}
