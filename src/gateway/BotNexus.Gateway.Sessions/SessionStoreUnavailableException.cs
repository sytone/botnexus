namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Thrown when the session store fails to complete an operation after exhausting
/// all transient-error retries. Maps to HTTP 503 at the API layer.
/// </summary>
public sealed class SessionStoreUnavailableException : Exception
{
    /// <inheritdoc />
    public SessionStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
