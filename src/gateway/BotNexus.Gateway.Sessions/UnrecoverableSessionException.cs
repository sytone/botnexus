namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Signals that a persisted session row cannot be hydrated into a usable
/// <see cref="GatewaySession"/> because its backing data is irreparably inconsistent -
/// for example a non-null <c>conversation_id</c> that references a conversation which no
/// longer exists (issue #2188). Bulk read paths catch this to skip-and-log the offending
/// row rather than aborting the whole enumeration, so one bad row can never poison the
/// full session list or crash gateway startup. Targeted single-session loads still
/// surface it for strict/diagnostic callers.
/// </summary>
public sealed class UnrecoverableSessionException : InvalidOperationException
{
    /// <summary>The id of the session row that could not be hydrated.</summary>
    public string SessionId { get; }

    /// <summary>
    /// Initialises a new <see cref="UnrecoverableSessionException"/>.
    /// </summary>
    /// <param name="sessionId">The id of the unrecoverable session row.</param>
    /// <param name="message">A human-readable explanation of why the row is unrecoverable.</param>
    public UnrecoverableSessionException(string sessionId, string message)
        : base(message)
    {
        SessionId = sessionId;
    }
}
