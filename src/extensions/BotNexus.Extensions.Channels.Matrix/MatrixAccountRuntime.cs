using System.Collections.Concurrent;
using System.Text;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Token-free projection of a <see cref="MatrixAccountConfig"/>: the routing and authorization
/// facts the running adapter needs, with the access token deliberately absent.
/// </summary>
/// <remarks>
/// <para>
/// The access token is needed exactly once — to construct the account's <see cref="IMatrixClient"/>,
/// which then owns it as an <c>Authorization</c> header. Nothing downstream of that construction
/// reads it. Retaining the whole <see cref="MatrixAccountConfig"/> on the long-lived runtime record
/// therefore kept a live credential reachable from process-wide state for no functional reason, and
/// CodeQL flagged it as clear-text storage of sensitive information
/// (<c>cs/cleartext-storage-of-sensitive-information</c>, alert 110).
/// </para>
/// <para>
/// Projecting at the boundary is the structural fix rather than a suppression: the credential is not
/// merely unlogged, it is <b>unreachable</b> from the runtime graph, so no future logging statement,
/// serialiser, debugger dump, or crash-dump walk of the adapter's account dictionary can surface it.
/// A field that does not exist cannot leak.
/// </para>
/// </remarks>
/// <param name="UserId">The account's fully-qualified Matrix user ID. Used for echo suppression.</param>
/// <param name="AutoJoin">Whether the account accepts room invites automatically.</param>
/// <param name="AllowedRoomIds">Room allow-list. Empty permits all joined rooms.</param>
/// <param name="AllowedUserIds">Sender allow-list. Empty permits all senders.</param>
internal sealed record MatrixAccountIdentity(
    string UserId,
    bool AutoJoin,
    IReadOnlyList<string> AllowedRoomIds,
    IReadOnlyList<string> AllowedUserIds)
{
    /// <summary>
    /// Projects a configuration entry, copying the allow-lists so a later mutation of the bound
    /// configuration cannot retroactively widen a running account's authorization.
    /// </summary>
    /// <param name="config">The configured account. Its access token is intentionally not read.</param>
    /// <returns>The token-free projection.</returns>
    public static MatrixAccountIdentity FromConfig(MatrixAccountConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new MatrixAccountIdentity(
            config.UserId ?? string.Empty,
            config.AutoJoin,
            [.. config.AllowedRoomIds],
            [.. config.AllowedUserIds]);
    }

    /// <summary>Whether this account may act in the supplied room. An empty allow-list permits all.</summary>
    /// <param name="roomId">Matrix room ID.</param>
    public bool IsRoomAllowed(string roomId) =>
        AllowedRoomIds.Count == 0
        || AllowedRoomIds.Contains(roomId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this account may process a message from the supplied sender.</summary>
    /// <param name="userId">Fully-qualified Matrix user ID of the sender.</param>
    public bool IsUserAllowed(string userId) =>
        AllowedUserIds.Count == 0
        || AllowedUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the supplied room is explicitly named in this account's allow-list.</summary>
    /// <param name="roomId">Matrix room ID.</param>
    public bool ExplicitlyOwnsRoom(string roomId) =>
        AllowedRoomIds.Contains(roomId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Live state for one configured Matrix account: its client, its sync cursor, its sync loop, and
/// its in-flight streaming accumulators.
/// </summary>
/// <remarks>
/// Holds a token-free <see cref="MatrixAccountIdentity"/> rather than the raw
/// <see cref="MatrixAccountConfig"/>, so the account's access token is not reachable from this
/// long-lived record. See <see cref="MatrixAccountIdentity"/> for why.
/// </remarks>
/// <param name="accountName">Configuration key of the account.</param>
/// <param name="agentId">BotNexus agent inbound messages on this account route to.</param>
/// <param name="identity">Token-free projection of the account's configuration.</param>
/// <param name="client">Client bound to this account's homeserver and token.</param>
internal sealed class MatrixAccountRuntime(
    string accountName,
    string agentId,
    MatrixAccountIdentity identity,
    IMatrixClient client)
{
    private int _startClaimed;

    /// <summary>Configuration key of the account.</summary>
    public string AccountName { get; } = accountName;

    /// <summary>BotNexus agent inbound messages on this account route to.</summary>
    public string AgentId { get; } = agentId;

    /// <summary>
    /// Token-free routing and authorization facts for this account. Deliberately not the raw
    /// configuration: the access token must not be reachable from process-lifetime state.
    /// </summary>
    public MatrixAccountIdentity Identity { get; } = identity;

    /// <summary>Client bound to this account's homeserver and token.</summary>
    public IMatrixClient Client { get; } = client;

    /// <summary>
    /// Opaque <c>next_batch</c> token from the last successfully processed sync, or null before the
    /// first sync. Advanced only after a batch is fully processed so a crash replays rather than
    /// skips.
    /// </summary>
    public string? SinceToken { get; set; }

    /// <summary>Cancellation source for the account's sync loop.</summary>
    public CancellationTokenSource? SyncCancellation { get; set; }

    /// <summary>The running sync loop.</summary>
    public Task? SyncTask { get; set; }

    /// <summary>
    /// In-flight streaming accumulators keyed by channel request ID (falling back to the channel
    /// address), so two concurrent streams in one room cannot share text or an edit target.
    /// </summary>
    public ConcurrentDictionary<string, MatrixStreamingState> StreamingStates { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Claims the one-way start latch. Returns <see langword="false"/> when the account is already
    /// started, which is what makes a retried <c>StartAsync</c> resumable instead of launching a
    /// duplicate sync loop.
    /// </summary>
    public bool TryBeginStart() => Interlocked.CompareExchange(ref _startClaimed, 1, 0) == 0;

    /// <summary>Releases the start latch so a legitimate restart is not silently a no-op.</summary>
    public void AbandonStart() => Interlocked.Exchange(ref _startClaimed, 0);
}

/// <summary>
/// Accumulator for one in-flight streaming response: the text so far and the event being edited
/// in place via <c>m.replace</c>.
/// </summary>
/// <param name="roomId">Room the stream is being written to.</param>
/// <param name="threadRootEventId">Thread root when the stream belongs to a thread.</param>
internal sealed class MatrixStreamingState(string roomId, string? threadRootEventId)
{
    /// <summary>Room the stream is being written to.</summary>
    public string RoomId { get; } = roomId;

    /// <summary>Thread root event ID when the stream belongs to a thread; null for room root.</summary>
    public string? ThreadRootEventId { get; } = threadRootEventId;

    /// <summary>Accumulated text delivered so far.</summary>
    public StringBuilder Buffer { get; } = new();

    /// <summary>
    /// Event ID of the message being edited. Null until the first flush creates it; that
    /// null-to-set transition is what distinguishes "send" from "edit".
    /// </summary>
    public string? RootEventId { get; set; }

    /// <summary>
    /// When the last flush completed. <see cref="DateTimeOffset.MinValue"/> initially so the first
    /// delta always flushes immediately rather than waiting out a buffer interval.
    /// </summary>
    public DateTimeOffset LastFlushUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Serialises concurrent deltas for this stream.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}
