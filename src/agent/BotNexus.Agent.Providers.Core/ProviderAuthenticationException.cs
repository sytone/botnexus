namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Thrown when a provider rejects a request with an authentication failure (HTTP 401, or 403
/// when the body indicates an auth/credential problem rather than a quota/permission one).
/// </summary>
/// <remarks>
/// Why this is a distinct exception type:
/// <list type="bullet">
/// <item>An auth failure is <b>terminal</b> -- retrying with the same invalid/expired key is
/// pointless, so it must NOT be classified as transient (the agent loop's <c>IsTransientError</c>
/// only treats rate-limit / 5xx / timeout messages as retryable, and an auth message contains
/// none of those tokens, so a typed auth exception fails fast instead of walking the model
/// fallback ladder and logging <c>LLM-stream-failed</c> repeatedly).</item>
/// <item>The <see cref="System.Exception.Message"/> is written to be <b>actionable and
/// user-facing</b>. The agent loop surfaces a terminal stream failure by copying
/// <c>ex.Message</c> straight into the assistant message's <c>ErrorMessage</c>
/// (<c>StopReason.Error</c>), so a clear "auth failed for provider X -- rotate the key or switch
/// models" message reaches the channel verbatim with no extra plumbing.</item>
/// </list>
/// Deriving from <see cref="HttpRequestException"/> (mirroring <see cref="ProviderRateLimitException"/>)
/// keeps existing catch sites that expect an <see cref="HttpRequestException"/> working unchanged.
/// </remarks>
public sealed class ProviderAuthenticationException : HttpRequestException
{
    /// <summary>
    /// The name of the provider that rejected the credentials (e.g. <c>openai</c>, <c>copilot</c>).
    /// Surfaced in the user-facing message so the failure is self-diagnosing.
    /// </summary>
    public string ProviderName { get; }

    public ProviderAuthenticationException(string message, int statusCode, string providerName)
        : base(message, null, (System.Net.HttpStatusCode)statusCode)
    {
        ProviderName = providerName;
    }

    /// <summary>
    /// Builds the canonical actionable, user-facing message for an authentication failure.
    /// Names the provider and the HTTP status, and tells the user what to do (rotate the key or
    /// switch models) so the failure does not fall through as a generic, undiagnosable error.
    /// </summary>
    /// <param name="providerName">The provider that rejected the request.</param>
    /// <param name="statusCode">The HTTP status code (typically 401 or 403).</param>
    /// <param name="errorBody">The raw error body from the provider, appended for diagnosis.</param>
    public static string BuildMessage(string providerName, int statusCode, string errorBody)
    {
        var detail = string.IsNullOrWhiteSpace(errorBody) ? string.Empty : $" Provider response: {errorBody}";
        return
            $"Authentication failed for provider '{providerName}' (HTTP {statusCode}): the provider rejected your " +
            $"credentials. Check or rotate the API key for '{providerName}', or switch to a model whose provider is " +
            $"configured.{detail}";
    }
}
