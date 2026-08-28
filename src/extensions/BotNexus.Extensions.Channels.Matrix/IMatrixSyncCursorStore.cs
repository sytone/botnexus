namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Durable home for a Matrix account's <c>/sync</c> cursor, keyed by agent id and account name
/// (#3595).
/// </summary>
/// <remarks>
/// <para>
/// The adapter advances <see cref="MatrixAccountRuntime.SinceToken"/> in memory as it processes
/// batches; without a store behind it the token's lifetime is the process, so every gateway restart
/// re-issues <c>/sync</c> with <c>since: null</c> and the account either replays already-answered
/// turns or silently misses messages sent during the restart window.
/// </para>
/// <para>
/// The seam is deliberately narrow — a get and a set over an opaque string. It carries no
/// credential and no routing state, which is what keeps the credential-containment guarantee of
/// <see cref="MatrixAccountIdentity"/> intact: the persisted cursor is derived from the homeserver's
/// response, never from the account's configuration.
/// </para>
/// </remarks>
public interface IMatrixSyncCursorStore
{
    /// <summary>
    /// Reads the last durably-recorded <c>next_batch</c> token for an account, or
    /// <see langword="null"/> when the account has never completed a batch.
    /// </summary>
    /// <param name="agentId">BotNexus agent that owns the account.</param>
    /// <param name="accountName">Configuration key of the account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetAsync(string agentId, string accountName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the <c>next_batch</c> token for an account. Called only after the batch that produced
    /// it has been fully processed, so a crash replays that batch rather than skipping it.
    /// </summary>
    /// <param name="agentId">BotNexus agent that owns the account.</param>
    /// <param name="accountName">Configuration key of the account.</param>
    /// <param name="sinceToken">The opaque token to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string agentId, string accountName, string sinceToken, CancellationToken cancellationToken = default);
}
