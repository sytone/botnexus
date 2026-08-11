using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Thrown when <c>ISessionStore.ArchiveAsync</c> could not quiesce the run bound to the target
/// session inside the drain timeout (issue #2903).
/// </summary>
/// <remarks>
/// This is deliberately a distinguishable type rather than a generic <see cref="TimeoutException"/>
/// or a silent no-op: the archive did <b>not</b> happen, nothing was sealed, and no history was
/// rewritten. A caller that sees this should retry later or surface a conflict to the user - it
/// must never interpret it as "archived anyway".
/// </remarks>
public sealed class SessionArchiveDrainTimeoutException : Exception
{
    /// <summary>Creates the exception for the session whose run would not drain.</summary>
    /// <param name="sessionId">The session the archive was refused for.</param>
    /// <param name="timeout">The drain budget that elapsed.</param>
    public SessionArchiveDrainTimeoutException(SessionId sessionId, TimeSpan timeout)
        : base($"Archive of session '{sessionId.Value}' was refused: an active run did not drain within {timeout.TotalSeconds:0.###}s. The session was left untouched.")
    {
        SessionId = sessionId;
        Timeout = timeout;
    }

    /// <summary>The session that still had a live run and was therefore left unsealed.</summary>
    public SessionId SessionId { get; }

    /// <summary>The drain budget that elapsed without the run settling.</summary>
    public TimeSpan Timeout { get; }
}
