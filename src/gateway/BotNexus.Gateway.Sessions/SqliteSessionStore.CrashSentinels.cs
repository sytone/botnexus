using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions;

public sealed partial class SqliteSessionStore
{
    /// <inheritdoc />
    /// <remarks>
    /// A narrow mutation like <see cref="AppendEntriesAsync"/>: one fenced <c>DELETE</c> under the striped
    /// session lock, never a save of an aggregate, so it cannot write back stale rows or session columns.
    /// The fence check and the conditional delete share one transaction.
    /// </remarks>
    public override async Task<bool> TryDeleteRedundantCrashSentinelAsync(
        SessionWriteFence fence,
        string persistenceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistenceKey);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var sessionLock = await AcquireSessionLockAsync(fence.ExpectedSessionId, cancellationToken).ConfigureAwait(false);

        var deleted = await RetryOnTransientAsync(async () =>
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadFenceSnapshotAsync(connection, fence.ExpectedSessionId, cancellationToken, transaction).ConfigureAwait(false);
            if (!SessionFenceEvaluator.Passes(fence, snapshot))
                return false;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM session_history
                WHERE session_id = $sessionId AND persistence_key = $persistenceKey AND is_crash_sentinel = 1
                  AND EXISTS (
                      SELECT 1 FROM session_history other
                      WHERE other.session_id = $sessionId AND other.is_crash_sentinel = 1
                        AND other.persistence_key IS NOT $persistenceKey)
                """;
            command.Parameters.AddWithValue("$sessionId", fence.ExpectedSessionId.Value);
            command.Parameters.AddWithValue("$persistenceKey", persistenceKey);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        // A cached aggregate may still hold the row; drop it so the next read sees the store.
        if (deleted)
            InvalidateCachedSession(fence.ExpectedSessionId);
        return deleted;
    }
}
