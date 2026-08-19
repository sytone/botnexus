namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// The subset of the Matrix Client-Server API the adapter depends on, expressed as a seam so the
/// adapter can be exercised against a fake homeserver without a network.
/// </summary>
/// <remarks>
/// Kept deliberately narrow: every member here corresponds to exactly one Client-Server endpoint
/// the first vertical slice needs. Capabilities deferred by #1201 (encryption, media upload,
/// receipts) are absent rather than stubbed, so an unimplemented feature is a compile error at the
/// call site rather than a silent no-op at runtime.
/// </remarks>
public interface IMatrixClient
{
    /// <summary>
    /// Long-polls <c>GET /_matrix/client/v3/sync</c>.
    /// </summary>
    /// <param name="since">
    /// Opaque batch token from the previous sync, or null for an initial sync.
    /// </param>
    /// <param name="timeoutMs">Server-side long-poll timeout in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync response.</returns>
    Task<MatrixSyncResponse> SyncAsync(string? since, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>
    /// Sends an <c>m.room.message</c> event via
    /// <c>PUT /_matrix/client/v3/rooms/{roomId}/send/m.room.message/{txnId}</c>.
    /// </summary>
    /// <param name="roomId">Target Matrix room ID.</param>
    /// <param name="content">The message content to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created event's ID.</returns>
    Task<string> SendMessageAsync(string roomId, MatrixMessageContent content, CancellationToken cancellationToken);

    /// <summary>
    /// Joins a room via <c>POST /_matrix/client/v3/rooms/{roomId}/join</c>.
    /// </summary>
    /// <param name="roomId">Room ID to join.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task JoinRoomAsync(string roomId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the account's typing state via
    /// <c>PUT /_matrix/client/v3/rooms/{roomId}/typing/{userId}</c>.
    /// </summary>
    /// <param name="roomId">Room the typing state applies to.</param>
    /// <param name="typing">Whether the account is typing.</param>
    /// <param name="timeoutMs">
    /// How long the homeserver should consider the typing state live, in milliseconds. Ignored
    /// when <paramref name="typing"/> is <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetTypingAsync(string roomId, bool typing, int timeoutMs, CancellationToken cancellationToken);
}
