using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>
/// Reads one session's tool calls out of the session store.
/// </summary>
/// <remarks>
/// <para>
/// The session is fixed at construction, from the execution context the tool was contributed for.
/// It is not a tool argument, so an agent has no way to point the recorder at another conversation
/// or another agent's run — there is nothing to name one in.
/// </para>
/// <para>
/// Reading the STORE rather than <c>AgentExecutionContext.History</c> is the whole point. That
/// property is the history as it stood when the handle was created, so a tool built from it would
/// record every run except the one in progress — which is the only run anyone wants to record.
/// </para>
/// </remarks>
public sealed class SessionStoreTraceSource(ISessionStore sessions, SessionId sessionId) : ISessionTraceSource
{
    /// <inheritdoc />
    public SessionId SessionId => sessionId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordedStep>> GetStepsAsync(CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return [];

        return SkillRecorder.FromHistory(session.GetHistorySnapshot());
    }
}
