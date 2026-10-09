using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>Optional backend capability for durable, payload-free agent-run observations.</summary>
/// <remarks>Does not create sessions, infer legacy runs, or grant access to a session.</remarks>
public interface IAgentRunEvidenceStore
{
    /// <summary>Records an observed start or terminal acknowledgement for an existing writable session.</summary>
    /// <param name="sessionId">Session whose ownership the caller has already established.</param>
    /// <param name="evidence">Observation; the first terminal and initial start time are immutable.</param>
    /// <param name="cancellationToken">Cancellation for persistence; terminal callers use None after interruption.</param>
    /// <returns>A task completed after persistence. Retries and late starts cannot regress a terminal.</returns>
    Task RecordAgentRunAsync(SessionId sessionId, AgentRunEvidence evidence, CancellationToken cancellationToken);

    /// <summary>Reads only a bounded ascending page and calculates statistics over that selected page.</summary>
    /// <param name="sessionId">Session whose ownership the caller has already established.</param>
    /// <param name="limit">Maximum selected rows, from 1 through 1000, including unknown and running rows.</param>
    /// <param name="afterSequence">Exclusive monotonic cursor, zero for the initial page.</param>
    /// <param name="cancellationToken">Cancellation for the bounded read.</param>
    /// <returns>Selected observations, nearest-rank percentiles, and independently bounded legacy coverage.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Limit or cursor is outside the documented range.</exception>
    Task<AgentRunEvidencePage> QueryAgentRunsAsync(SessionId sessionId, int limit, long afterSequence, CancellationToken cancellationToken);
}
