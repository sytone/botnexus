using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory;

/// <summary>
/// Outcome of one reconciliation pass.
/// </summary>
/// <param name="PrunedRows">Number of orphaned memory rows deleted across all agents.</param>
/// <param name="FailedClosed">
/// <see langword="true"/> when the session corpus could not be enumerated and the pass therefore
/// deleted nothing. This is a deliberate refusal, not a failure to run.
/// </param>
public readonly record struct MemoryReconciliationResult(int PrunedRows, bool FailedClosed);

/// <summary>
/// Prunes memory rows whose <c>session_id</c> names a session that no longer exists (issue #2956).
/// </summary>
/// <remarks>
/// <para>
/// Memory indexing is a one-way additive projection of session lifecycle events: it has an insert
/// path and, before #2956, no delete path at all. Any session removed outside the API delete path -
/// a store trimmed by hand, a pre-fix deletion, a restored backup - leaves rows that stay
/// searchable forever and keep surfacing in <c>memory_search</c> attributed to a session that is
/// gone. This pass is the convergence mechanism for that accumulated divergence.
/// </para>
/// <para>
/// <b>Fail-closed posture.</b> A failed or unavailable session-corpus read is indistinguishable
/// from "no sessions exist", and acting on the latter interpretation would delete every
/// session-scoped memory on the instance. So an enumeration fault aborts the pass with zero
/// deletions and a warning rather than pruning against a partial view. Deleting nothing is always
/// recoverable; deleting the corpus is not.
/// </para>
/// <para>
/// Rows with a <see langword="null"/> <c>session_id</c> are outside this pass entirely - they are
/// never returned by <see cref="IMemoryStore.ListSessionIdsAsync"/> and so can never be selected
/// as orphans.
/// </para>
/// </remarks>
public sealed class MemorySessionReconciler(
    IMemoryStoreFactory storeFactory,
    ISessionStore sessions,
    IAgentRegistry agents,
    ILogger<MemorySessionReconciler> logger)
{
    private readonly IMemoryStoreFactory _storeFactory = storeFactory;
    private readonly ISessionStore _sessions = sessions;
    private readonly IAgentRegistry _agents = agents;
    private readonly ILogger<MemorySessionReconciler> _logger = logger;

    /// <summary>
    /// Runs one reconciliation pass across every registered agent's memory store.
    /// </summary>
    public async Task<MemoryReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        HashSet<string> liveSessionIds;
        try
        {
            var live = await _sessions.ListAsync(null, cancellationToken).ConfigureAwait(false);
            liveSessionIds = [.. live.Select(session => session.SessionId.Value)];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: without a trustworthy session corpus every memory row looks orphaned.
            _logger.LogWarning(
                ex,
                "Memory/session reconciliation skipped: the session corpus could not be enumerated. No memory rows were pruned.");
            return new MemoryReconciliationResult(0, FailedClosed: true);
        }

        var pruned = 0;
        foreach (var agent in _agents.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            pruned += await ReconcileAgentAsync(agent.AgentId, liveSessionIds, cancellationToken).ConfigureAwait(false);
        }

        if (pruned > 0)
        {
            _logger.LogInformation(
                "Memory/session reconciliation pruned {PrunedRows} memory row(s) belonging to sessions that no longer exist.",
                pruned);
        }

        return new MemoryReconciliationResult(pruned, FailedClosed: false);
    }

    private async Task<int> ReconcileAgentAsync(
        AgentId agentId,
        HashSet<string> liveSessionIds,
        CancellationToken cancellationToken)
    {
        // #2608: a sub-agent workspace reaped by the sweeper has no memory store location, and
        // opening it yields a permanently unrecoverable SQLITE_CANTOPEN. Skip before SQLite.
        if (!_storeFactory.StoreLocationExists(agentId))
            return 0;

        try
        {
            var store = _storeFactory.Create(agentId);
            var indexedSessionIds = await store.ListSessionIdsAsync(cancellationToken).ConfigureAwait(false);

            var pruned = 0;
            foreach (var sessionId in indexedSessionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The orphan filter. Removing it makes the pass delete every session-scoped row.
                if (liveSessionIds.Contains(sessionId))
                    continue;

                pruned += await store.DeleteBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }

            return pruned;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One agent's unreadable store must not abort reconciliation for every other agent,
            // and must not be interpreted as "this agent has no live sessions".
            _logger.LogWarning(
                ex,
                "Memory/session reconciliation failed for agent '{AgentId}'; its memory store was left untouched.",
                agentId.Value);
            return 0;
        }
    }
}
